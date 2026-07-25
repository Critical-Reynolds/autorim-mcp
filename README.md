# AutoRim

Natural-language control of a running RimWorld colony, through MCP.

Ask for what you want — "who should be cooking?", "set up a hunting run", "queue electricity
research", "wall off the north approach" — and it happens in the live game.

Built for **RimWorld 1.6** (developed against 1.6.4871).

---

## How it works

RimWorld has no external API, so the only way to reach live colony state is from inside the
game process. AutoRim is therefore two pieces:

```
Claude  ──MCP/stdio──►  server/  ──HTTP on 127.0.0.1──►  AutoRim mod (inside RimWorld)
                                                              │
                                                     main-thread dispatcher
                                                              │
                                                      Verse / RimWorld API
```

- **`mod-src/`** — a C# mod (`net472`) that runs a loopback-only HTTP listener and executes
  commands against the game's own APIs.
- **`server/`** — a TypeScript MCP server that Claude talks to, forwarding to the mod.

Two design choices worth knowing:

**No Harmony.** RimWorld auto-instantiates every `GameComponent` found in a loaded mod
assembly, which gives a per-frame hook without patching anything. Alerts and events are polled
rather than intercepted. Nothing but `AutoRim.dll` is loaded into the game — even the JSON
layer is hand-rolled, because shipping a second copy of a common library into RimWorld's
AppDomain is a well-known source of conflicts with other mods.

**Socket threads never touch the game.** Unity and RimWorld are not thread-safe. Requests are
queued and executed inside `GameComponentUpdate` under a per-frame time budget; the socket
thread only ever waits on a handle. This is the load-bearing part of the design.

---

## Install

```powershell
# 1. Build and install the mod
.\scripts\deploy.ps1

# 2. Build the MCP server
cd server
npm install
npm run build
```

Then in RimWorld: **Mods** → enable **AutoRim - MCP Bridge** → restart when prompted.

Verify the bridge while a colony is loaded:

```powershell
.\scripts\smoke.ps1                  # basic checks
.\scripts\smoke.ps1 -Concurrency 50  # also stress the main-thread dispatcher
```

Register the MCP server with Claude Code (`.mcp.json` in this repo already does this):

```json
{
  "mcpServers": {
    "rimworld": {
      "command": "node",
      "args": ["server/dist/index.js"]
    }
  }
}
```

> The mod's DLL is memory-mapped while RimWorld runs, so **close the game before redeploying**.

---

## Safety

Most of what the tools do is reversible through normal play, and runs immediately. Anything
that is not sits behind an explicit gate.

**Destructive actions require `confirm: true`.** Without it the call changes nothing and
returns a preview describing exactly what would happen:

| Action | What it does |
|---|---|
| `designate.slaughter` | Kills colony animals |
| `designate.deconstruct` / `uninstall` | Tears down buildings |
| `designate.strip` | Strips gear from pawns or corpses |
| `designate.release_animal` | Releases tamed animals to the wild |
| `health.add_surgery` | Irreversible, and can maim or kill |
| `prisoners.release` / `execute` | Prisoner leaves for good, or dies |
| `trade.execute` | Goods and silver change hands |
| `caravan.form` | Pawns leave the map |

Alongside that:

- **A rolling restore point.** Before any confirmed destructive action the mod saves to
  `AutoRim-safety` (rate-limited to once a minute). One save, overwritten each time, so there
  is always a "just before it did something" state to fall back to. On by default — leave it on
  if you play permadeath.
- **`control.save` cannot overwrite your saves.** Names are forced into an `AutoRim-` slot.
- **An audit log** at `…/Config/AutoRim/actions.log`, plus an in-game message each time.
- **A kill switch**: Options → Mod settings → AutoRim, or the `control.disable_bridge` tool.
- **Loopback only**, with a random token generated on first run.

---

## Tools

24 tools, ~110 commands. Each tool takes an `action`.

| Tool | Covers |
|---|---|
| `rimworld_colony` | snapshot, alerts, letters, resources, power |
| `rimworld_pawns` | list, detail, rename, area, medical care, hostility response |
| `rimworld_work` | work priorities, bulk assignment |
| `rimworld_jobs` | draft, move, attack, prioritise, stop |
| `rimworld_designate` | hunt, mine, chop, harvest, tame, haul, forbid, claim, smooth + destructive ones |
| `rimworld_build` | place, lines and rectangles, placement checks, what's buildable |
| `rimworld_research` | current, list, set, stop, suggest |
| `rimworld_bills` | workbench production orders |
| `rimworld_zones` | stockpiles and growing zones |
| `rimworld_storage` | priorities and filters |
| `rimworld_areas` | allowed areas |
| `rimworld_policies` | apparel, food, drug |
| `rimworld_schedule` | timetables |
| `rimworld_health` | surgery |
| `rimworld_animals` | training, masters |
| `rimworld_prisoners` | interaction modes, release, execution |
| `rimworld_trade` | full trade session |
| `rimworld_caravan` | list, sendable, form |
| `rimworld_world` | factions, settlements, quests |
| `rimworld_ideology` | ideoligions (Ideology DLC) |
| `rimworld_map` | map info, thing search, ASCII region view |
| `rimworld_query` | definition lookup |
| `rimworld_analyze` | best pawn for a job, idle pawns, bottlenecks, threats |
| `rimworld_control` | speed, save, notify, bridge status |

### Two that carry more weight than their size suggests

**`rimworld_query`** — the game's internal names rarely match what a person says. When
something is rejected as not found or ambiguous, the error carries candidates; this tool is how
you resolve them properly.

**`rimworld_analyze best_pawn_for`** — returns the whole ranking with reasoning, not a winner.
"Ivy, cooking 8 with a burning passion, only on two other jobs" is something you can disagree
with; "assign Ivy" is not.

---

## Known limitations

- **Caravans leave with only what pawns carry.** `caravan.form` uses the immediate-departure
  path. Assembling a cargo manifest is the game's multi-step loading flow and is not covered —
  use the in-game Form Caravan dialog for a real supply run.
- **Designations act on the map currently on screen**, because that is what RimWorld's
  `Designator` classes bind to. Reads accept a `map` index; designations do not.
- **Only Core + Ideology are exercised.** Royalty, Biotech, Anomaly and Odyssey commands are
  gated on `ModsConfig` and return a clean `DLC_NOT_ACTIVE` rather than failing oddly, but they
  are untested here.
- **Redeploying needs the game closed**, since Windows keeps the loaded DLL mapped.

---

## Repository layout

```
mod-src/           C# mod source
  AutoRim/
    Bridge/        HTTP listener, JSON, main-thread dispatcher
    Core/          command registry, safety gate, def resolver, identity
    Read/          state serializers
    Commands/      one file per subsystem
dist-mod/AutoRim/  deployable mod folder (About/ + Assemblies/)
server/            TypeScript MCP server
scripts/           deploy.ps1, smoke.ps1, rpc.ps1
```

`scripts/rpc.ps1` sends a single command and prints the raw response with its size — useful
when developing, since keeping reads small is a hard constraint here rather than an
afterthought.

```powershell
.\scripts\rpc.ps1 colony.snapshot
.\scripts\rpc.ps1 analyze.best_pawn_for '{"work":"Cooking"}'
```
