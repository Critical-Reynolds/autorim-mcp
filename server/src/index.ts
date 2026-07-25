#!/usr/bin/env node
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

import { BridgeError, health, rpc } from "./bridge.js";
import { TOOLS } from "./tools.js";

const VERSION = "0.1.0";

/** Actions that queue or perform something irreversible. Mirrors the mod's own tiering. */
const DESTRUCTIVE = new Set([
  "designate.slaughter",
  "designate.deconstruct",
  "designate.strip",
  "designate.release_animal",
  "designate.uninstall",
  "health.add_surgery",
  "prisoners.release",
  "prisoners.execute",
  "trade.execute",
  "caravan.form",
]);

function textResult(payload: unknown, isError = false) {
  return {
    isError,
    content: [
      {
        type: "text" as const,
        text: typeof payload === "string" ? payload : JSON.stringify(payload, null, 2),
      },
    ],
  };
}

/**
 * Turns a command failure into something the model can act on. The mod already returns a
 * code, a message and often a hint plus candidates; the job here is to keep all of that
 * visible rather than collapsing it to "request failed".
 */
function formatFailure(command: string, error: { code: string; message: string; hint?: string; data?: unknown }) {
  const lines = [`${command} failed [${error.code}]: ${error.message}`];
  if (error.hint) lines.push(`Hint: ${error.hint}`);

  if (error.code === "NEEDS_CONFIRM") {
    lines.push(
      "Nothing was changed. Show the user what this would do, get their agreement, then send the same call again with confirm: true.",
    );
  }

  if (error.data !== undefined) {
    lines.push("Details:", JSON.stringify(error.data, null, 2));
  }

  return textResult(lines.join("\n"), true);
}

async function main() {
  const server = new McpServer({ name: "rimworld", version: VERSION });

  for (const spec of TOOLS) {
    const inputSchema: Record<string, z.ZodTypeAny> = {
      action: z
        .enum(spec.actions)
        .describe(`Which ${spec.subsystem} operation to perform.`),
      ...spec.params,
    };

    server.registerTool(
      spec.name,
      {
        title: spec.title,
        description: spec.description,
        inputSchema,
      },
      async (rawArgs: Record<string, unknown>) => {
        const { action, ...args } = rawArgs;
        const command = `${spec.subsystem}.${action}`;

        // Drop keys the caller left undefined so the mod sees a clean argument object and
        // its "was this argument supplied" checks behave.
        const cleaned: Record<string, unknown> = {};
        for (const [key, value] of Object.entries(args)) {
          if (value !== undefined) cleaned[key] = value;
        }

        try {
          // Destructive commands may trigger a safety autosave in game, which on a large
          // colony takes noticeably longer than a normal call.
          const timeoutMs = DESTRUCTIVE.has(command) ? 60000 : 20000;
          const response = await rpc(command, cleaned, timeoutMs);

          if (response.ok) return textResult(response.data);
          return formatFailure(command, response.error);
        } catch (error) {
          if (error instanceof BridgeError) {
            const lines = [`${command} could not be sent [${error.code}]: ${error.message}`];
            if (error.hint) lines.push(`Hint: ${error.hint}`);
            return textResult(lines.join("\n"), true);
          }
          return textResult(
            `${command} failed unexpectedly: ${error instanceof Error ? error.message : String(error)}`,
            true,
          );
        }
      },
    );
  }

  // Warn on drift between this server's tool surface and what the running mod actually
  // provides. A stale mod DLL is the usual cause, and it is otherwise a confusing failure.
  void checkDrift();

  await server.connect(new StdioServerTransport());
}

async function checkDrift() {
  const status = await health();
  if (!status) {
    console.error(
      "[autorim] RimWorld is not reachable yet. Tools will report this clearly until the game is running with the AutoRim mod enabled.",
    );
    return;
  }

  try {
    const response = await rpc("meta.list_commands", {}, 5000);
    if (!response.ok) return;

    const data = response.data as { commands?: Array<{ name: string }>; version?: string };
    const available = new Set((data.commands ?? []).map((c) => c.name));

    const missing: string[] = [];
    for (const spec of TOOLS) {
      for (const action of spec.actions) {
        const command = `${spec.subsystem}.${action}`;
        if (!available.has(command)) missing.push(command);
      }
    }

    if (missing.length > 0) {
      console.error(
        `[autorim] The running mod (v${data.version}) does not provide ${missing.length} command(s) this server exposes: ${missing.join(", ")}. Rebuild and redeploy the mod, then restart RimWorld.`,
      );
    } else {
      console.error(`[autorim] Connected. Mod v${data.version}, ${available.size} commands available.`);
    }
  } catch {
    // Drift checking is a convenience; never let it stop the server starting.
  }
}

main().catch((error) => {
  console.error("[autorim] Fatal:", error);
  process.exit(1);
});
