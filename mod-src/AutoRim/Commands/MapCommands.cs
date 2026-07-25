using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AutoRim.Bridge;
using AutoRim.Core;
using AutoRim.Read;
using RimWorld;
using Verse;

namespace AutoRim.Commands
{
    public class MapInfoCommand : CommandBase
    {
        public override string Name => "map.info";
        public override string Description => "Map size, biome, terrain summary and which maps are loaded.";

        public override JsonValue Execute(JsonValue args)
        {
            var maps = JsonValue.NewArray();
            for (int i = 0; i < Find.Maps.Count; i++)
            {
                var map = Find.Maps[i];
                maps.Add(JsonValue.NewObject()
                    .Set("index", i)
                    .Set("current", map == Find.CurrentMap)
                    .Set("sizeX", map.Size.x)
                    .Set("sizeZ", map.Size.z)
                    .Set("biome", map.Biome?.label ?? "unknown")
                    .Set("isPlayerHome", map.IsPlayerHome)
                    .Set("colonists", map.mapPawns?.FreeColonistsCount ?? 0));
            }

            return JsonValue.NewObject()
                .Set("currentMap", Find.Maps.IndexOf(Find.CurrentMap))
                .Set("maps", maps);
        }
    }

    /// <summary>
    /// Filtered queries over map contents. Deliberately has no "give me everything" mode: a
    /// mid-game map holds many thousands of things and dumping them is never the right answer.
    /// </summary>
    public class MapThingsCommand : CommandBase
    {
        public override string Name => "map.things";
        public override string Description =>
            "Finds things on the map. Filter by defName, category (item/building/plant/corpse/filth), forbidden state, or proximity.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.ResolveMap(args.Has("map") ? args.OptInt("map", 0) : (int?)null);

            string defQuery = args.OptString("defName");
            string category = args.OptString("category");
            string search = args.OptString("search");
            bool? forbidden = args.Has("forbidden") ? args.OptBool("forbidden") : (bool?)null;

            ThingDef targetDef = null;
            if (!string.IsNullOrEmpty(defQuery))
                targetDef = DefResolver.Resolve<ThingDef>(defQuery, "defName");

            IntVec3? near = null;
            int radius = args.OptInt("radius", 0);
            if (args.Has("near"))
            {
                near = Refs.RequireCell(args, "near", map);
                if (radius <= 0) radius = 10;
            }

            if (targetDef == null && string.IsNullOrEmpty(category) && string.IsNullOrEmpty(search) && near == null)
                throw CommandException.BadArgs(
                    "map.things needs at least one filter.",
                    "Pass defName, category, search, or near+radius.");

            ThingCategory? categoryFilter = ParseCategory(category);

            var matches = new List<Thing>();
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing?.def == null) continue;
                if (thing is Pawn) continue; // pawns have their own commands

                if (targetDef != null && thing.def != targetDef) continue;
                if (categoryFilter.HasValue && thing.def.category != categoryFilter.Value) continue;

                if (!string.IsNullOrEmpty(search) &&
                    (thing.Label ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                    thing.def.defName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (forbidden.HasValue && thing.IsForbidden(Faction.OfPlayer) != forbidden.Value) continue;

                if (near.HasValue && !thing.Position.InHorDistOf(near.Value, radius)) continue;

                matches.Add(thing);
            }

            // Group identical stacks so "steel" reads as one line, not four hundred.
            if (args.OptBool("group", true) && targetDef == null)
            {
                var grouped = matches
                    .GroupBy(t => new { t.def, Stuff = t.Stuff })
                    .Select(g => new
                    {
                        Def = g.Key.def,
                        g.Key.Stuff,
                        Count = g.Sum(t => t.stackCount),
                        Piles = g.Count(),
                        Sample = g.First()
                    })
                    .OrderByDescending(g => g.Count)
                    .ToList();

                args.ReadPaging(out int gOffset, out int gLimit, 50, 200);
                var page = Describe.Page(grouped, gOffset, gLimit, g => JsonValue.NewObject()
                    .Set("defName", g.Def.defName)
                    .Set("label", g.Def.label)
                    .Set("stuff", g.Stuff?.label ?? "")
                    .Set("count", g.Count)
                    .Set("piles", g.Piles)
                    .Set("samplePos", Refs.Cell(g.Sample.Position))
                    .Set("sampleId", g.Sample.thingIDNumber));
                page.Set("grouped", true);
                return page;
            }

            args.ReadPaging(out int offset, out int limit, 50, 200);
            return Describe.Page(matches, offset, limit, Describe.ThingSummary);
        }

        private static ThingCategory? ParseCategory(string category)
        {
            if (string.IsNullOrEmpty(category)) return null;
            switch (category.ToLowerInvariant())
            {
                case "item": return ThingCategory.Item;
                case "building": return ThingCategory.Building;
                case "plant": return ThingCategory.Plant;
                case "filth": return ThingCategory.Filth;
                case "gas": return ThingCategory.Gas;
                case "corpse": return ThingCategory.Item; // corpses are items; label filtering narrows further
                default:
                    throw CommandException.BadArgs($"Unknown category '{category}'.",
                        "Use item, building, plant, filth or gas.");
            }
        }
    }

    /// <summary>
    /// A local top-down view as an ASCII grid. This is what makes build planning possible:
    /// listing a thousand cells as JSON is unreadable, but thirty rows of characters shows the
    /// shape of a room at a glance.
    /// </summary>
    public class MapRegionCommand : CommandBase
    {
        public override string Name => "map.region";
        public override string Description =>
            "ASCII top-down view around a cell, with a legend and the notable things in view. Use before placing buildings.";

        private const int MaxRadius = 30;

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.ResolveMap(args.Has("map") ? args.OptInt("map", 0) : (int?)null);
            var center = Refs.RequireCell(args, "center", map);

            int radius = args.OptInt("radius", 12);
            if (radius < 1) radius = 1;
            if (radius > MaxRadius)
                throw CommandException.BadArgs($"radius {radius} is too large (max {MaxRadius}).",
                    "Large regions produce unreadable output; take several smaller views.");

            int minX = Math.Max(0, center.x - radius);
            int maxX = Math.Min(map.Size.x - 1, center.x + radius);
            int minZ = Math.Max(0, center.z - radius);
            int maxZ = Math.Min(map.Size.z - 1, center.z + radius);

            var rows = JsonValue.NewArray();
            var legendUsed = new SortedDictionary<char, string>();
            var notable = new List<Thing>();

            // North (higher z) is drawn first so the grid reads like the game's camera.
            for (int z = maxZ; z >= minZ; z--)
            {
                var line = new StringBuilder(maxX - minX + 1);
                for (int x = minX; x <= maxX; x++)
                {
                    var cell = new IntVec3(x, 0, z);
                    line.Append(Glyph(map, cell, legendUsed, notable));
                }
                rows.Add(line.ToString());
            }

            var legend = JsonValue.NewObject();
            foreach (var entry in legendUsed) legend.Set(entry.Key.ToString(), entry.Value);

            return JsonValue.NewObject()
                .Set("center", Refs.Cell(center))
                .Set("bounds", JsonValue.NewObject()
                    .Set("minX", minX).Set("maxX", maxX)
                    .Set("minZ", minZ).Set("maxZ", maxZ))
                .Set("note", "rows[0] is z=maxZ and runs south; character[0] of each row is x=minX.")
                .Set("legend", legend)
                .Set("rows", rows)
                .Set("notable", GroupNotable(notable));
        }

        private static char Glyph(Map map, IntVec3 cell, IDictionary<char, string> legend, List<Thing> notable)
        {
            if (map.fogGrid != null && map.fogGrid.IsFogged(cell))
                return Mark(legend, '?', "unexplored");

            var edifice = cell.GetEdifice(map);
            if (edifice != null)
            {
                // Walls, doors and rock are fully described by their glyph, and listing every
                // one of them individually swamps the response for no added information. Only
                // buildings a player would actually refer to get an entry with an id.
                if (edifice.def.IsDoor) return Mark(legend, '+', "door");
                if (edifice.def.building != null && edifice.def.building.isNaturalRock)
                    return Mark(legend, '^', "natural rock");
                if (edifice.def.passability == Traversability.Impassable)
                    return Mark(legend, '#', "wall or impassable building");

                notable.Add(edifice);
                return Mark(legend, 'B', "furniture or workbench");
            }

            var things = map.thingGrid.ThingsListAtFast(cell);
            bool hasItem = false, hasPlant = false, hasPawn = false;
            Thing plant = null;

            foreach (var thing in things)
            {
                if (thing is Pawn) { hasPawn = true; continue; }
                if (thing.def.category == ThingCategory.Item) { hasItem = true; notable.Add(thing); }
                else if (thing.def.category == ThingCategory.Plant) { hasPlant = true; plant = thing; }
            }

            if (hasPawn) return Mark(legend, '@', "pawn");
            if (hasItem) return Mark(legend, 'i', "item on the ground");

            if (hasPlant && plant != null)
            {
                if (plant.def.plant != null && plant.def.plant.IsTree)
                    return Mark(legend, 'T', "tree");
                return Mark(legend, ',', "plant");
            }

            var terrain = map.terrainGrid.TerrainAt(cell);
            if (terrain != null)
            {
                if (terrain.IsWater) return Mark(legend, '~', "water");
                if (terrain.passability == Traversability.Impassable) return Mark(legend, '^', "impassable terrain");
                if (terrain.IsFloor) return Mark(legend, '_', "constructed floor");
            }

            if (map.roofGrid != null && map.roofGrid.Roofed(cell))
                return Mark(legend, ':', "roofed open cell");

            return Mark(legend, '.', "open ground");
        }

        private static char Mark(IDictionary<char, string> legend, char glyph, string meaning)
        {
            if (!legend.ContainsKey(glyph)) legend[glyph] = meaning;
            return glyph;
        }

        /// <summary>
        /// Collapses the notable list by kind. Twelve identical beds are one line with twelve
        /// positions, not twelve lines.
        /// </summary>
        private static JsonValue GroupNotable(List<Thing> things)
        {
            const int MaxGroups = 25;
            const int MaxPositionsPerGroup = 8;

            var result = JsonValue.NewArray();

            var groups = things
                .Distinct()
                .GroupBy(t => new { t.def, Stuff = t.Stuff })
                .OrderByDescending(g => g.Count())
                .Take(MaxGroups);

            foreach (var group in groups)
            {
                var items = group.ToList();
                var entry = JsonValue.NewObject()
                    .Set("defName", group.Key.def.defName)
                    .Set("label", group.Key.def.label ?? "")
                    .Set("count", items.Count);

                if (group.Key.Stuff != null) entry.Set("stuff", group.Key.Stuff.label);

                var positions = JsonValue.NewArray();
                foreach (var thing in items.Take(MaxPositionsPerGroup))
                    positions.Add(JsonValue.NewObject()
                        .Set("id", thing.thingIDNumber)
                        .Set("x", thing.Position.x)
                        .Set("z", thing.Position.z));
                entry.Set("at", positions);

                if (items.Count > MaxPositionsPerGroup) entry.Set("positionsTruncated", true);

                result.Add(entry);
            }

            return result;
        }
    }
}
