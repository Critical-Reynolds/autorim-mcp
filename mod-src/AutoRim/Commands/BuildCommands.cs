using System;
using System.Collections.Generic;
using System.Linq;
using AutoRim.Bridge;
using AutoRim.Core;
using AutoRim.Read;
using RimWorld;
using Verse;

namespace AutoRim.Commands
{
    /// <summary>
    /// Placement helpers shared by the build commands.
    /// </summary>
    internal static class BuildHelpers
    {
        /// <summary>
        /// Resolves what to build, coping with the way people actually phrase it. "steel wall"
        /// is not a def — Wall is the def and Steel is its stuff — so when a whole-phrase lookup
        /// fails we try splitting the phrase into a stuff prefix and a buildable suffix.
        /// </summary>
        public static void ResolveBuildable(JsonValue args, out BuildableDef def, out ThingDef stuff)
        {
            string query = args.RequireString("thing");
            string explicitStuff = args.OptString("stuff");

            def = null;
            stuff = null;

            // Floors and other terrain are buildable too, and share the same placement path.
            var terrain = DefResolver.ResolveOrNull<TerrainDef>(query, t => t.BuildableByPlayer);

            var thing = DefResolver.ResolveOrNull<ThingDef>(query, t => t.BuildableByPlayer);

            if (thing != null) def = thing;
            else if (terrain != null) def = terrain;

            if (def == null)
            {
                var split = TrySplitStuffPrefix(query);
                if (split != null)
                {
                    def = split.Item1;
                    if (string.IsNullOrEmpty(explicitStuff)) stuff = split.Item2;
                }
            }

            if (def == null)
            {
                // Fall back to the strict resolver so the caller gets candidates and a good error.
                def = DefResolver.Resolve<ThingDef>(query, "thing", t => t.BuildableByPlayer);
            }

            if (!string.IsNullOrEmpty(explicitStuff))
                stuff = DefResolver.Resolve<ThingDef>(explicitStuff, "stuff", t => t.IsStuff);

            if (def.MadeFromStuff && stuff == null) stuff = PickStuff(def);

            if (!def.MadeFromStuff && stuff != null)
                throw CommandException.BadArgs($"'{def.label}' is not made from a material.",
                    "Omit 'stuff' for this one.");

            if (stuff != null && !AllowedStuffs(def).Contains(stuff))
            {
                var error = CommandException.BadArgs(
                    $"'{stuff.label}' cannot be used to build '{def.label}'.",
                    "The candidates below are accepted.");
                error.Payload = JsonValue.NewObject()
                    .Set("allowedStuff", DefResolver.Describe(AllowedStuffs(def).Take(20)));
                throw error;
            }
        }

        private static Tuple<BuildableDef, ThingDef> TrySplitStuffPrefix(string query)
        {
            var words = query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2) return null;

            // Prefer the longest buildable suffix: "granite block wall" is Wall out of
            // "granite block", not "block wall" out of "granite".
            for (int split = 1; split < words.Length; split++)
            {
                string stuffPart = string.Join(" ", words, 0, split);
                string thingPart = string.Join(" ", words, split, words.Length - split);

                var candidate = (BuildableDef)DefResolver.ResolveOrNull<ThingDef>(thingPart, t => t.BuildableByPlayer)
                                ?? DefResolver.ResolveOrNull<TerrainDef>(thingPart, t => t.BuildableByPlayer);
                if (candidate == null) continue;

                var stuff = DefResolver.ResolveOrNull<ThingDef>(stuffPart, t => t.IsStuff);
                if (stuff == null) continue;

                return Tuple.Create(candidate, stuff);
            }

            return null;
        }

        public static List<ThingDef> AllowedStuffs(BuildableDef def)
        {
            try
            {
                return GenStuff.AllowedStuffsFor(def, TechLevel.Undefined, false).ToList();
            }
            catch (Exception)
            {
                return new List<ThingDef>();
            }
        }

        /// <summary>
        /// Picks a sensible default material: whichever allowed stuff the colony has most of.
        /// This is what makes "build a wall here" work without the caller having to know or
        /// care what is in the stockpile.
        /// </summary>
        public static ThingDef PickStuff(BuildableDef def)
        {
            var allowed = AllowedStuffs(def);
            if (allowed.Count == 0) return GenStuff.DefaultStuffFor(def);

            var map = Find.CurrentMap;
            var counter = map?.resourceCounter;

            if (counter != null)
            {
                var best = allowed
                    .Select(s => new { Stuff = s, Count = counter.GetCount(s) })
                    .Where(x => x.Count >= def.CostStuffCount)
                    .OrderByDescending(x => x.Count)
                    .FirstOrDefault();

                if (best != null) return best.Stuff;
            }

            return GenStuff.DefaultStuffFor(def) ?? allowed[0];
        }

        public static Rot4 ParseRotation(JsonValue args)
        {
            if (!args.Has("rotation")) return Rot4.North;

            var node = args["rotation"];
            if (node.Type == JsonType.Number)
            {
                int value = ((node.AsInt() % 4) + 4) % 4;
                return new Rot4(value);
            }

            switch (node.AsString("").Trim().ToLowerInvariant())
            {
                case "": case "north": case "n": case "up": return Rot4.North;
                case "east": case "e": case "right": return Rot4.East;
                case "south": case "s": case "down": return Rot4.South;
                case "west": case "w": case "left": return Rot4.West;
                default:
                    throw CommandException.BadArgs($"Unknown rotation '{node.AsString()}'.",
                        "Use north, east, south, west, or 0-3.");
            }
        }

        public static AcceptanceReport CanPlace(BuildableDef def, IntVec3 cell, Rot4 rotation, Map map, ThingDef stuff)
        {
            try
            {
                return GenConstruct.CanPlaceBlueprintAt(def, cell, rotation, map, false, null, null, stuff);
            }
            catch (Exception ex)
            {
                ARLog.Exception($"checking placement of {def.defName}", ex);
                return new AcceptanceReport("placement check failed");
            }
        }

        public static JsonValue CostSummary(BuildableDef def, ThingDef stuff, int quantity)
        {
            var costs = JsonValue.NewArray();

            if (def.CostList != null)
            {
                foreach (var cost in def.CostList)
                    costs.Add(JsonValue.NewObject()
                        .Set("label", cost.thingDef?.label ?? "")
                        .Set("total", cost.count * quantity));
            }

            if (stuff != null && def.CostStuffCount > 0)
                costs.Add(JsonValue.NewObject()
                    .Set("label", stuff.label)
                    .Set("total", def.CostStuffCount * quantity));

            return costs;
        }
    }

    public class BuildPlaceCommand : CommandBase
    {
        public override string Name => "build.place";
        public override string Description =>
            "Places a build order at a cell. Accepts 'steel wall' style phrasing; picks a material automatically if none is given.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            BuildHelpers.ResolveBuildable(args, out var def, out var stuff);
            var rotation = BuildHelpers.ParseRotation(args);
            var cell = Refs.RequireCell(args, "cell", map);

            var report = BuildHelpers.CanPlace(def, cell, rotation, map, stuff);
            if (!report.Accepted)
                throw CommandException.Failed(
                    $"Cannot place '{def.label}' at ({cell.x},{cell.z}): {report.Reason}",
                    "map.region shows what is already there.");

            GenConstruct.PlaceBlueprintForBuild(def, cell, map, rotation, Faction.OfPlayer, stuff);

            return JsonValue.NewObject()
                .Set("thing", def.defName)
                .Set("label", def.label)
                .Set("stuff", stuff?.label ?? JsonValue.Null)
                .Set("cell", Refs.Cell(cell))
                .Set("rotation", rotation.AsInt)
                .Set("cost", BuildHelpers.CostSummary(def, stuff, 1))
                .Set("summary", $"Placed {(stuff != null ? stuff.label + " " : "")}{def.label} at ({cell.x},{cell.z}).");
        }
    }

    /// <summary>
    /// Lines and rectangles. Walls are almost never placed one cell at a time, and making the
    /// caller issue forty separate calls to enclose a room would be unusable.
    /// </summary>
    public class BuildPlaceLineCommand : CommandBase
    {
        public override string Name => "build.place_line";
        public override string Description =>
            "Places build orders along a straight line or around/inside a rectangle. mode: line, rect (outline), filled.";

        private const int MaxPlacements = 400;

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            BuildHelpers.ResolveBuildable(args, out var def, out var stuff);
            var rotation = BuildHelpers.ParseRotation(args);

            var from = Refs.RequireCell(args, "from", map);
            var to = Refs.RequireCell(args, "to", map);
            string mode = args.OptString("mode", "line").ToLowerInvariant();

            var cells = PlanCells(from, to, mode);

            if (cells.Count > MaxPlacements)
                throw CommandException.BadArgs(
                    $"That covers {cells.Count} cells, more than the {MaxPlacements} limit.",
                    "Build it in sections.");

            int placed = 0;
            var reasons = new Dictionary<string, int>();

            foreach (var cell in cells)
            {
                if (!cell.InBounds(map)) continue;

                var report = BuildHelpers.CanPlace(def, cell, rotation, map, stuff);
                if (!report.Accepted)
                {
                    string reason = string.IsNullOrEmpty(report.Reason) ? "blocked" : report.Reason;
                    reasons.TryGetValue(reason, out int existing);
                    reasons[reason] = existing + 1;
                    continue;
                }

                GenConstruct.PlaceBlueprintForBuild(def, cell, map, rotation, Faction.OfPlayer, stuff);
                placed++;
            }

            var result = JsonValue.NewObject()
                .Set("thing", def.defName)
                .Set("stuff", stuff?.label ?? JsonValue.Null)
                .Set("mode", mode)
                .Set("considered", cells.Count)
                .Set("placed", placed)
                .Set("cost", BuildHelpers.CostSummary(def, stuff, placed));

            if (reasons.Count > 0)
            {
                var skipped = JsonValue.NewArray();
                foreach (var entry in reasons.OrderByDescending(e => e.Value).Take(5))
                    skipped.Add(JsonValue.NewObject().Set("reason", entry.Key).Set("count", entry.Value));
                result.Set("skipped", skipped);
            }

            result.Set("summary", $"Placed {placed} of {cells.Count} {def.label} orders.");
            return result;
        }

        private static List<IntVec3> PlanCells(IntVec3 from, IntVec3 to, string mode)
        {
            var cells = new List<IntVec3>();

            int minX = Math.Min(from.x, to.x), maxX = Math.Max(from.x, to.x);
            int minZ = Math.Min(from.z, to.z), maxZ = Math.Max(from.z, to.z);

            switch (mode)
            {
                case "line":
                    // Straight lines only: a diagonal run of walls is never what is wanted.
                    if (from.x == to.x)
                        for (int z = minZ; z <= maxZ; z++) cells.Add(new IntVec3(from.x, 0, z));
                    else if (from.z == to.z)
                        for (int x = minX; x <= maxX; x++) cells.Add(new IntVec3(x, 0, from.z));
                    else
                        throw CommandException.BadArgs(
                            "'line' needs from and to to share an x or a z.",
                            "Use mode 'rect' for a rectangle, or split it into two straight runs.");
                    break;

                case "rect":
                    for (int x = minX; x <= maxX; x++)
                    {
                        cells.Add(new IntVec3(x, 0, minZ));
                        if (maxZ != minZ) cells.Add(new IntVec3(x, 0, maxZ));
                    }
                    for (int z = minZ + 1; z < maxZ; z++)
                    {
                        cells.Add(new IntVec3(minX, 0, z));
                        if (maxX != minX) cells.Add(new IntVec3(maxX, 0, z));
                    }
                    break;

                case "filled":
                    for (int x = minX; x <= maxX; x++)
                        for (int z = minZ; z <= maxZ; z++)
                            cells.Add(new IntVec3(x, 0, z));
                    break;

                default:
                    throw CommandException.BadArgs($"Unknown mode '{mode}'.",
                        "Use line, rect or filled.");
            }

            return cells;
        }
    }

    public class BuildCheckCommand : CommandBase
    {
        public override string Name => "build.check";
        public override string Description => "Checks whether something can be placed at a cell, without placing it.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            BuildHelpers.ResolveBuildable(args, out var def, out var stuff);
            var rotation = BuildHelpers.ParseRotation(args);
            var cell = Refs.RequireCell(args, "cell", map);

            var report = BuildHelpers.CanPlace(def, cell, rotation, map, stuff);

            return JsonValue.NewObject()
                .Set("thing", def.defName)
                .Set("stuff", stuff?.label ?? JsonValue.Null)
                .Set("cell", Refs.Cell(cell))
                .Set("canPlace", report.Accepted)
                .Set("reason", report.Accepted ? "" : (report.Reason ?? "blocked"))
                .Set("cost", BuildHelpers.CostSummary(def, stuff, 1));
        }
    }

    public class BuildListCommand : CommandBase
    {
        public override string Name => "build.list_buildable";
        public override string Description =>
            "What the colony can build right now, respecting completed research. Optionally filtered by search text.";

        public override JsonValue Execute(JsonValue args)
        {
            string search = args.OptString("search");
            bool includeLocked = args.OptBool("includeLocked", false);

            var things = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => d.BuildableByPlayer && d.designationCategory != null)
                .Where(d => includeLocked || d.IsResearchFinished)
                .Where(d => string.IsNullOrEmpty(search) ||
                            (d.label ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            d.defName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(d => d.designationCategory.defName)
                .ThenBy(d => d.label)
                .ToList();

            args.ReadPaging(out int offset, out int limit, 60, 300);

            return Describe.Page(things, offset, limit, d =>
            {
                var entry = JsonValue.NewObject()
                    .Set("defName", d.defName)
                    .Set("label", d.label ?? "")
                    .Set("category", d.designationCategory.defName)
                    .Set("size", JsonValue.NewObject().Set("x", d.Size.x).Set("z", d.Size.z));

                if (d.MadeFromStuff) entry.Set("madeFromStuff", true);
                if (!d.IsResearchFinished) entry.Set("researchLocked", true);

                return entry;
            });
        }
    }
}
