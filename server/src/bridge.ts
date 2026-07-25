import { readFile } from "node:fs/promises";
import { homedir } from "node:os";
import { join } from "node:path";

/**
 * HTTP client for the in-game AutoRim bridge.
 *
 * The bridge only exists while RimWorld is running with the mod enabled, so the failure
 * everyone hits first is "connection refused". That case gets a specific, actionable message
 * rather than a raw socket error.
 */

export interface RpcSuccess {
  ok: true;
  data: unknown;
}

export interface RpcFailure {
  ok: false;
  error: {
    code: string;
    message: string;
    hint?: string;
    data?: unknown;
  };
}

export type RpcResponse = RpcSuccess | RpcFailure;

const DEFAULT_PORT = 7789;
const DEFAULT_TIMEOUT_MS = 20000;

function configRoot(): string {
  // RimWorld writes to LocalLow, which has no standard env var on Windows.
  const override = process.env.AUTORIM_CONFIG_DIR;
  if (override) return override;

  return join(
    homedir(),
    "AppData",
    "LocalLow",
    "Ludeon Studios",
    "RimWorld by Ludeon Studios",
    "Config",
    "AutoRim",
  );
}

function tokenPath(): string {
  return process.env.AUTORIM_TOKEN_FILE ?? join(configRoot(), "bridge.token");
}

function port(): number {
  const raw = process.env.AUTORIM_PORT;
  if (!raw) return DEFAULT_PORT;
  const parsed = Number.parseInt(raw, 10);
  return Number.isFinite(parsed) ? parsed : DEFAULT_PORT;
}

let cachedToken: string | null = null;

async function readToken(): Promise<string> {
  if (cachedToken) return cachedToken;

  const path = tokenPath();
  try {
    const contents = await readFile(path, "utf8");
    cachedToken = contents.trim();
    if (!cachedToken) throw new Error("empty token file");
    return cachedToken;
  } catch {
    throw new BridgeError(
      "AUTH_TOKEN_MISSING",
      `Could not read the AutoRim bridge token from ${path}.`,
      "The mod writes this file the first time RimWorld runs with AutoRim enabled. Start RimWorld, enable the mod, then retry.",
    );
  }
}

export class BridgeError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly hint?: string,
    readonly data?: unknown,
  ) {
    super(message);
    this.name = "BridgeError";
  }
}

/**
 * Sends one command. Transport problems throw BridgeError; command-level failures come back
 * inside the envelope so callers can distinguish "RimWorld is not running" from "that pawn
 * does not exist".
 */
export async function rpc(
  command: string,
  args: Record<string, unknown> = {},
  timeoutMs = DEFAULT_TIMEOUT_MS,
): Promise<RpcResponse> {
  const token = await readToken();
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs + 5000);

  try {
    const response = await fetch(`http://127.0.0.1:${port()}/rpc`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-AutoRim-Token": token,
      },
      body: JSON.stringify({ command, args, timeoutMs }),
      signal: controller.signal,
    });

    if (response.status === 401) {
      // The mod regenerates the token if its file is lost; drop the cache and say so.
      cachedToken = null;
      throw new BridgeError(
        "AUTH_FAILED",
        "The AutoRim bridge rejected our token.",
        `Check ${tokenPath()} matches the running game, then retry.`,
      );
    }

    const text = await response.text();
    let parsed: RpcResponse;
    try {
      parsed = JSON.parse(text) as RpcResponse;
    } catch {
      throw new BridgeError(
        "BAD_RESPONSE",
        `The bridge returned something that is not JSON (HTTP ${response.status}).`,
        text.slice(0, 200),
      );
    }

    return parsed;
  } catch (error) {
    if (error instanceof BridgeError) throw error;

    if (error instanceof Error && error.name === "AbortError") {
      throw new BridgeError(
        "TIMEOUT",
        `'${command}' did not return within ${timeoutMs} ms.`,
        "RimWorld may be paused mid-load or busy saving. Check the game window.",
      );
    }

    throw new BridgeError(
      "RIMWORLD_NOT_RUNNING",
      "Could not reach RimWorld on 127.0.0.1:" + port() + ".",
      "Start RimWorld, enable the AutoRim mod under Mods, and load a colony. If it is already running, check that the bridge is enabled in Options > Mod settings > AutoRim.",
    );
  } finally {
    clearTimeout(timer);
  }
}

/** Liveness probe that does not require a token. */
export async function health(): Promise<unknown | null> {
  try {
    const response = await fetch(`http://127.0.0.1:${port()}/health`, {
      signal: AbortSignal.timeout(3000),
    });
    if (!response.ok) return null;
    return await response.json();
  } catch {
    return null;
  }
}
