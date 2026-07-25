import { z } from "zod";

/**
 * Tool surface.
 *
 * One tool per subsystem with an `action` discriminator, rather than one tool per command.
 * There are over a hundred commands behind these; exposing them flat would swamp tool
 * selection, and the subsystem grouping matches how a player thinks about the game anyway.
 *
 * Descriptions carry real weight here: they are the only thing telling the model which
 * arguments an action needs and which actions destroy something.
 */

const pawnRef = z
  .union([z.string(), z.number()])
  .describe("Pawn name or numeric id from pawns.list. Ids are stable; names may be ambiguous.");

const cell = z
  .object({ x: z.number().int(), z: z.number().int() })
  .describe("A map cell. RimWorld's ground plane is x/z; north is +z.");

const rect = z
  .object({
    x1: z.number().int(),
    z1: z.number().int(),
    x2: z.number().int(),
    z2: z.number().int(),
  })
  .describe("An inclusive rectangle of cells, corner to corner.");

const paging = {
  limit: z.number().int().min(1).max(300).optional().describe("Maximum rows to return."),
  offset: z.number().int().min(0).optional().describe("Rows to skip, for paging."),
};

const confirm = z
  .boolean()
  .optional()
  .describe(
    "Required (true) for destructive actions. Without it the call returns a preview of what would happen and changes nothing.",
  );

const mapIndex = z
  .number()
  .int()
  .optional()
  .describe("Map index from map.info. Defaults to the map currently on screen.");

export interface ToolSpec {
  name: string;
  subsystem: string;
  title: string;
  description: string;
  actions: [string, ...string[]];
  params: Record<string, z.ZodTypeAny>;
}

export const TOOLS: ToolSpec[] = [
  {
    name: "rimworld_colony",
    subsystem: "colony",
    title: "Colony overview",
    description:
      "Read the state of the colony. Start here for almost any question.\n" +
      "- snapshot: date, weather, threat, wealth, every colonist in one line each, food days, key resources, research, power. The default read.\n" +
      "- alerts: what the game is warning about, with explanations.\n" +
      "- letters: events waiting on a decision.\n" +
      "- resources: full stockpile counts (filter narrows by name).\n" +
      "- power: per-network generation and stored charge.",
    actions: ["snapshot", "alerts", "letters", "resources", "power"],
    params: {
      map: mapIndex,
      filter: z.string().optional().describe("resources: substring to match against item names."),
      ...paging,
    },
  },
  {
    name: "rimworld_pawns",
    subsystem: "pawns",
    title: "Pawns",
    description:
      "Inspect and configure individual pawns.\n" +
      "- list: one line each. filter: colonists (default), animals, prisoners, slaves, hostiles, wild, all.\n" +
      "- detail: everything about one pawn — skills, traits, health, needs, gear, work priorities, schedule. Needs `pawn`.\n" +
      "- rename: needs `pawn` and `nick` (optionally `first`, `last`).\n" +
      "- set_medical_care: needs `pawn` and `care` (none, nomeds, herbal, normal, best).\n" +
      "- set_area: needs `pawn`; `area` is an allowed-area name, or omit to remove the restriction.\n" +
      "- set_hostility_response: needs `pawn` and `response` (ignore, attack, flee).\n" +
      "Equipment:\n" +
      "- list_equippable: weapons and apparel a `pawn` could pick up, nearest first. Use this to find item ids.\n" +
      "- equip: needs `pawn` and `item` (a weapon id or name). The pawn walks to it and swaps weapons; not instant.\n" +
      "- wear: needs `pawn` and `item` (apparel). Conflicting apparel comes off automatically.\n" +
      "- unequip: needs `pawn`. Drops their current weapon on the ground; pick it back up with equip.",
    actions: [
      "list",
      "detail",
      "rename",
      "set_medical_care",
      "set_area",
      "set_hostility_response",
      "list_equippable",
      "equip",
      "wear",
      "unequip",
    ],
    params: {
      pawn: pawnRef.optional(),
      filter: z.string().optional(),
      nick: z.string().optional(),
      first: z.string().optional(),
      last: z.string().optional(),
      care: z.string().optional(),
      area: z.string().optional(),
      response: z.string().optional(),
      item: z
        .union([z.string(), z.number()])
        .optional()
        .describe("Thing id from list_equippable, or an item name resolved against what is near the pawn."),
      kind: z.enum(["weapons", "apparel", "all"]).optional(),
      includeUnusable: z.boolean().optional(),
      ...paging,
    },
  },
  {
    name: "rimworld_work",
    subsystem: "work",
    title: "Work priorities",
    description:
      "The work tab. Priority 0 means the pawn will not do that work at all; 1 is highest and 4 lowest.\n" +
      "- list_types: every work type and the skills it uses.\n" +
      "- get_priorities: for one `pawn`, or all colonists when omitted.\n" +
      "- set_priority: needs `pawn`, `work`, `priority`.\n" +
      "- set_bulk: needs `assignments`, an array of {pawn, work, priority}. Rows that fail are reported individually and do not abort the rest.\n" +
      "- clear: sets every work type to 0 for `pawn`.\n" +
      "To decide who should take a job, use rimworld_analyze best_pawn_for first.",
    actions: ["list_types", "get_priorities", "set_priority", "set_bulk", "clear"],
    params: {
      pawn: pawnRef.optional(),
      work: z.string().optional().describe("Work type name or defName, e.g. Cooking, Doctor, Hauling."),
      priority: z.number().int().min(0).max(4).optional(),
      assignments: z
        .array(
          z.object({
            pawn: pawnRef,
            work: z.string(),
            priority: z.number().int().min(0).max(4),
          }),
        )
        .optional(),
    },
  },
  {
    name: "rimworld_jobs",
    subsystem: "jobs",
    title: "Direct orders",
    description:
      "Immediate orders to a specific pawn, the equivalent of clicking them and issuing a command.\n" +
      "- current: what everyone is doing, and who is idle.\n" +
      "- draft / undraft: pass `pawn` and `drafted` (true or false).\n" +
      "- move_to: needs `pawn` and `cell`. Drafts them first unless draft:false.\n" +
      "- attack: needs `pawn` and `target` (thing id).\n" +
      "- prioritize: forces a pawn to work a specific `target` now, like right-click prioritise. The target usually needs a designation first.\n" +
      "- stop: cancels the current job and the queue.",
    actions: ["current", "draft", "move_to", "attack", "prioritize", "stop"],
    params: {
      pawn: pawnRef.optional(),
      drafted: z.boolean().optional(),
      draft: z.boolean().optional(),
      cell: cell.optional(),
      target: z.number().int().optional().describe("Thing id from map.things or map.region."),
      melee: z.boolean().optional(),
    },
  },
  {
    name: "rimworld_designate",
    subsystem: "designate",
    title: "Designations",
    description:
      "Mark things for work. This is how you ask for hunting, mining, chopping and so on: colonists with the matching work type then do it.\n" +
      "Targets can be given as `things` (array of ids), `cell`, `cells`, or `area` (a rectangle). With `area` you may add `defName` to only affect one kind of thing. `wholeMap` with `defName` covers the entire map — for example every muffalo.\n" +
      "Reversible: hunt, mine, mine_vein, chop, cut, harvest, tame, haul, forbid, unforbid, claim, smooth, cancel, list.\n" +
      "DESTRUCTIVE, needs confirm:true: slaughter, deconstruct, strip, release_animal, uninstall. Without confirm you get a preview of what would be hit.",
    actions: [
      "list",
      "hunt",
      "mine",
      "mine_vein",
      "chop",
      "cut",
      "harvest",
      "tame",
      "haul",
      "forbid",
      "unforbid",
      "claim",
      "smooth",
      "cancel",
      "slaughter",
      "deconstruct",
      "strip",
      "release_animal",
      "uninstall",
    ],
    params: {
      things: z.array(z.number().int()).optional(),
      cell: cell.optional(),
      cells: z.array(cell).optional(),
      area: rect.optional(),
      defName: z.string().optional().describe("Narrows an area or wholeMap sweep to one kind of thing."),
      wholeMap: z.boolean().optional(),
      confirm,
    },
  },
  {
    name: "rimworld_build",
    subsystem: "build",
    title: "Construction",
    description:
      "Place build orders. Blueprints are queued for colonists with Construction; nothing is built instantly.\n" +
      "- place: needs `thing` and `cell`. `thing` accepts natural phrasing like 'steel wall'; if you omit `stuff` a material the colony actually has is chosen.\n" +
      "- place_line: needs `thing`, `from`, `to`, and `mode` (line for a straight run, rect for an outline, filled for a solid block). Use this for walls rather than many single placements.\n" +
      "- check: tests one placement without doing it.\n" +
      "- list_buildable: what research currently allows, optionally filtered by `search`.\n" +
      "Check the ground first with rimworld_map region.",
    actions: ["place", "place_line", "check", "list_buildable"],
    params: {
      thing: z.string().optional(),
      stuff: z.string().optional().describe("Material, e.g. Steel, WoodLog, Granite blocks."),
      cell: cell.optional(),
      from: cell.optional(),
      to: cell.optional(),
      mode: z.enum(["line", "rect", "filled"]).optional(),
      rotation: z.union([z.string(), z.number()]).optional().describe("north, east, south, west or 0-3."),
      search: z.string().optional(),
      includeLocked: z.boolean().optional(),
      ...paging,
    },
  },
  {
    name: "rimworld_research",
    subsystem: "research",
    title: "Research",
    description:
      "- current: the active project and its progress.\n" +
      "- list: filter by available (default), finished, locked or all.\n" +
      "- set_current: needs `project`. Progress on the previous project is kept.\n" +
      "- stop: clears the active project without losing progress.\n" +
      "- suggest: ranks what to take next by what it unlocks, cost and existing progress.",
    actions: ["current", "list", "set_current", "stop", "suggest"],
    params: {
      project: z.string().optional(),
      filter: z.enum(["available", "finished", "locked", "all"]).optional(),
      search: z.string().optional(),
      ...paging,
    },
  },
  {
    name: "rimworld_bills",
    subsystem: "bills",
    title: "Workbench bills",
    description:
      "Production orders on workbenches. This is where most of what the colony makes gets decided.\n" +
      "- list_workbenches: every bench, with its id.\n" +
      "- list: bills on one `bench`.\n" +
      "- add: needs `bench` and `recipe`. `repeat` is forever, count (with `repeatCount`) or target (with `targetCount`, meaning 'keep this many in stock').\n" +
      "- set: change an existing bill by `index` — repeat mode, counts, `worker`, or `suspended`.\n" +
      "- remove / reorder: by `index`. reorder takes `offset` (-1 moves it one place earlier).",
    actions: ["list_workbenches", "list", "add", "set", "remove", "reorder"],
    params: {
      bench: z.union([z.string(), z.number()]).optional(),
      recipe: z.string().optional(),
      index: z.number().int().optional(),
      repeat: z.enum(["forever", "count", "target"]).optional(),
      repeatCount: z.number().int().optional(),
      targetCount: z.number().int().optional(),
      worker: pawnRef.optional(),
      suspended: z.boolean().optional(),
      offset: z.number().int().optional(),
      map: mapIndex,
      limit: z.number().int().optional(),
    },
  },
  {
    name: "rimworld_zones",
    subsystem: "zones",
    title: "Stockpile and growing zones",
    description:
      "- list: every zone with bounds and settings.\n" +
      "- create_stockpile: needs `area`. Optional `name`, `preset` (default or dumping), `priority`.\n" +
      "- create_growing: needs `area`. Optional `plant`, `name`.\n" +
      "- set_plant: needs `zone` and `plant`.\n" +
      "- expand: adds an `area` to an existing `zone`.\n" +
      "- delete: removes a `zone`; the things inside it stay.\n" +
      "Cells already covered by another zone are skipped, and the count is reported.",
    actions: ["list", "create_stockpile", "create_growing", "set_plant", "expand", "delete"],
    params: {
      zone: z.union([z.string(), z.number()]).optional(),
      area: rect.optional(),
      name: z.string().optional(),
      plant: z.string().optional(),
      preset: z.enum(["default", "dumping"]).optional(),
      priority: z.string().optional(),
      map: mapIndex,
    },
  },
  {
    name: "rimworld_storage",
    subsystem: "storage",
    title: "Storage settings",
    description:
      "What a stockpile or shelf accepts, and how strongly it pulls items. Target either a `zone` (name or index) or a `building` (thing id).\n" +
      "- get_settings: current priority and a sample of what is allowed.\n" +
      "- set_priority: low, normal, preferred, important, critical.\n" +
      "- set_allowed: pass `things` (names) with `allow` true or false, or `all:true` to allow/disallow everything at once.",
    actions: ["get_settings", "set_priority", "set_allowed"],
    params: {
      zone: z.union([z.string(), z.number()]).optional(),
      building: z.number().int().optional(),
      priority: z.string().optional(),
      things: z.array(z.string()).optional(),
      allow: z.boolean().optional(),
      all: z.boolean().optional(),
    },
  },
  {
    name: "rimworld_areas",
    subsystem: "areas",
    title: "Allowed areas",
    description:
      "Allowed areas restrict where pawns may go. Assign one to a pawn with rimworld_pawns set_area.\n" +
      "- list: all areas including the built-in home and roof areas.\n" +
      "- create: needs `area` (a rectangle) and optionally `name`.\n" +
      "- modify: needs `area` (the name) plus a rectangle, and `include` true to add or false to remove.",
    actions: ["list", "create", "modify"],
    params: {
      area: z.union([rect, z.string()]).optional(),
      name: z.string().optional(),
      include: z.boolean().optional(),
      map: mapIndex,
    },
  },
  {
    name: "rimworld_policies",
    subsystem: "policies",
    title: "Apparel, food and drug policies",
    description:
      "- list: existing policies of each kind, and which pawn is on which.\n" +
      "- assign: needs `pawn`, `type` (apparel, food or drug) and `policy` (the policy name).",
    actions: ["list", "assign"],
    params: {
      pawn: pawnRef.optional(),
      type: z.enum(["apparel", "food", "drug"]).optional(),
      policy: z.string().optional(),
    },
  },
  {
    name: "rimworld_schedule",
    subsystem: "schedule",
    title: "Timetables",
    description:
      "The 24-hour schedule.\n" +
      "- get: needs `pawn`.\n" +
      "- set: needs `pawn`, `assignment` (Work, Sleep, Joy, Anything, Meditate) and either `hours` (a list of 0-23) or `fromHour`+`toHour`. Ranges may wrap past midnight.",
    actions: ["get", "set"],
    params: {
      pawn: pawnRef.optional(),
      assignment: z.string().optional(),
      hours: z.array(z.number().int().min(0).max(23)).optional(),
      fromHour: z.number().int().min(0).max(23).optional(),
      toHour: z.number().int().min(0).max(23).optional(),
    },
  },
  {
    name: "rimworld_health",
    subsystem: "health",
    title: "Medical and surgery",
    description:
      "- list_surgeries: what can be performed on `pawn` right now, with valid body parts.\n" +
      "- pending_surgeries: what is already queued on `pawn`.\n" +
      "- add_surgery: DESTRUCTIVE, needs confirm:true. Needs `pawn` and `recipe`, plus `part` when several body parts are valid. Surgery is irreversible and failure can maim or kill.\n" +
      "- cancel_surgery: removes a queued operation by `index`. Safe, since nothing has happened yet.",
    actions: ["list_surgeries", "pending_surgeries", "add_surgery", "cancel_surgery"],
    params: {
      pawn: pawnRef.optional(),
      recipe: z.string().optional(),
      part: z.string().optional(),
      index: z.number().int().optional(),
      search: z.string().optional(),
      confirm,
    },
  },
  {
    name: "rimworld_animals",
    subsystem: "animals",
    title: "Animals",
    description:
      "- set_training: needs `pawn` (the animal), `training` (Tameness, Obedience, Release, Rescue) and `wanted`. Prerequisites are enabled automatically.\n" +
      "- set_master: needs `animal`, and `master` (omit to clear).\n" +
      "Use rimworld_pawns list with filter=animals to see the herd, and rimworld_designate tame/slaughter for the rest.",
    actions: ["set_training", "set_master"],
    params: {
      pawn: pawnRef.optional(),
      animal: pawnRef.optional(),
      master: pawnRef.optional(),
      training: z.string().optional(),
      wanted: z.boolean().optional(),
    },
  },
  {
    name: "rimworld_prisoners",
    subsystem: "prisoners",
    title: "Prisoners",
    description:
      "- list: prisoners with resistance, will and current mode.\n" +
      "- set_interaction: needs `pawn` and `mode` (maintain, recruit, reduce_resistance, convert, enslave, reduce_will).\n" +
      "- release: DESTRUCTIVE, needs confirm:true. They leave for good.\n" +
      "- execute: DESTRUCTIVE, needs confirm:true. This kills the prisoner and most colonists take a lasting mood penalty.",
    actions: ["list", "set_interaction", "release", "execute"],
    params: {
      pawn: pawnRef.optional(),
      mode: z.string().optional(),
      confirm,
    },
  },
  {
    name: "rimworld_trade",
    subsystem: "trade",
    title: "Trading",
    description:
      "Trading is a session: open, adjust quantities, check the balance, then execute. Closing without executing costs nothing.\n" +
      "- list_traders: who is available now.\n" +
      "- open: optionally `trader` and `negotiator`; your best social pawn is chosen by default.\n" +
      "- stock: what is on offer. filter: trader, colony or all.\n" +
      "- set: needs `item` and `count`. Positive buys from the trader, negative sells to them.\n" +
      "- evaluate: what moves each way and the silver balance.\n" +
      "- execute: DESTRUCTIVE, needs confirm:true. Goods and silver change hands immediately.\n" +
      "- close: abandons the session, exchanging nothing.",
    actions: ["list_traders", "open", "stock", "set", "evaluate", "execute", "close"],
    params: {
      trader: z.string().optional(),
      negotiator: pawnRef.optional(),
      giftMode: z.boolean().optional(),
      item: z.string().optional(),
      count: z.number().int().optional(),
      filter: z.enum(["trader", "colony", "all"]).optional(),
      search: z.string().optional(),
      confirm,
      ...paging,
    },
  },
  {
    name: "rimworld_caravan",
    subsystem: "caravan",
    title: "Caravans",
    description:
      "- list: your caravans on the world map.\n" +
      "- sendable: who could leave now, and nearby destinations with their tile numbers.\n" +
      "- form: DESTRUCTIVE, needs confirm:true. Needs `pawns` and either `destination` (settlement name) or `destinationTile`. The pawns leave the map immediately with only what they are carrying; to haul a large cargo load use the in-game Form Caravan dialog instead.",
    actions: ["list", "sendable", "form"],
    params: {
      pawns: z.array(pawnRef).optional(),
      destination: z.string().optional(),
      destinationTile: z.number().int().optional(),
      confirm,
    },
  },
  {
    name: "rimworld_world",
    subsystem: "world",
    title: "World map",
    description:
      "- factions: who is known and how they feel about you.\n" +
      "- settlements: places on the world map; `tradersOnly` narrows to ones you can trade with.\n" +
      "- quests: active quests and their state.",
    actions: ["factions", "settlements", "quests"],
    params: {
      tradersOnly: z.boolean().optional(),
      activeOnly: z.boolean().optional(),
      ...paging,
    },
  },
  {
    name: "rimworld_ideology",
    subsystem: "ideology",
    title: "Ideoligions",
    description:
      "- list: ideoligions in play with memes, precepts and roles. Pass full:true to expand every ideo rather than just yours. Requires the Ideology expansion.",
    actions: ["list"],
    params: {
      full: z.boolean().optional(),
    },
  },
  {
    name: "rimworld_map",
    subsystem: "map",
    title: "Map contents",
    description:
      "- info: map sizes, biome and which maps are loaded.\n" +
      "- things: find items and buildings. Filter by `defName`, `category` (item, building, plant, filth, gas), `search`, `forbidden`, or `near`+`radius`. At least one filter is required. Results are grouped by kind unless group:false.\n" +
      "- region: an ASCII top-down view around `center` with `radius` (max 30), plus a legend and the notable things in view. Use this before building anything.",
    actions: ["info", "things", "region"],
    params: {
      map: mapIndex,
      defName: z.string().optional(),
      category: z.string().optional(),
      search: z.string().optional(),
      forbidden: z.boolean().optional(),
      near: cell.optional(),
      radius: z.number().int().optional(),
      center: cell.optional(),
      group: z.boolean().optional(),
      ...paging,
    },
  },
  {
    name: "rimworld_query",
    subsystem: "query",
    title: "Definition lookup",
    description:
      "Look up what the game calls things. Reach for this when a name is rejected as not found or ambiguous.\n" +
      "- search_defs: needs `type` (thing, terrain, recipe, research, work, skill, trait, hediff, pawnkind, biome, plant, weapon, apparel, building) and optionally `query`.\n" +
      "- thing_info: needs `thing`. Build cost, materials, research needed, size, and which recipes make it.\n" +
      "- recipe_info: needs `recipe`. Ingredients, products, work amount, skill required, and which benches can run it.",
    actions: ["search_defs", "thing_info", "recipe_info"],
    params: {
      type: z.string().optional(),
      query: z.string().optional(),
      thing: z.string().optional(),
      recipe: z.string().optional(),
      limit: z.number().int().optional(),
    },
  },
  {
    name: "rimworld_analyze",
    subsystem: "analyze",
    title: "Analysis and recommendations",
    description:
      "Derived judgements, all read-only.\n" +
      "- best_pawn_for: needs `work`. Ranks colonists by skill, passion, health and current workload, and explains each score. Use this before assigning work.\n" +
      "- idle_pawns: who has nothing to do and the likely reason.\n" +
      "- bottlenecks: unassigned work types, suspended bills, unusable benches, idle research, low food, power deficit.\n" +
      "- threats: hostiles on the map weighed against who you have able to fight.",
    actions: ["best_pawn_for", "idle_pawns", "bottlenecks", "threats"],
    params: {
      work: z.string().optional(),
      limit: z.number().int().optional(),
      includeIneligible: z.boolean().optional(),
    },
  },
  {
    name: "rimworld_control",
    subsystem: "control",
    title: "Game control",
    description:
      "- bridge_status: is the bridge up, is a game loaded, how deep is the queue, does it run while unfocused.\n" +
      "- set_speed: paused, normal, fast, superfast, ultrafast.\n" +
      "- set_run_in_background: `enabled` true keeps the colony simulating while the RimWorld window is unfocused. This is on by default and required for anything to work while the player is typing elsewhere; if calls start timing out whenever they switch windows, check this first.\n" +
      "- save: writes a save. The name is always forced into an 'AutoRim-' slot, so this can never overwrite one of the player's own saves.\n" +
      "- notify: shows a message in game; `type` may be neutral, positive, negative, threat or caution.\n" +
      "- disable_bridge: shuts the bridge off. Re-enabling requires the in-game mod settings.",
    actions: ["bridge_status", "set_speed", "set_run_in_background", "save", "notify", "disable_bridge"],
    params: {
      speed: z.string().optional(),
      name: z.string().optional(),
      text: z.string().optional(),
      type: z.string().optional(),
      enabled: z.boolean().optional(),
    },
  },
];
