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
    internal static class ZoneHelpers
    {
        private const int MaxZoneCells = 4000;

        public static Zone ResolveZone(JsonValue args, Map map, string argName = "zone")
        {
            var node = args[argName];
            var zones = map.zoneManager.AllZones;

            if (node.Type == JsonType.Number)
            {
                int index = node.AsInt();
                if (index < 0 || index >= zones.Count)
                    throw CommandException.NotFound($"No zone at index {index}; there are {zones.Count}.");
                return zones[index];
            }

            string query = node.AsString();
            if (string.IsNullOrEmpty(query))
                throw CommandException.BadArgs($"Missing '{argName}' (zone name or index).");

            var exact = zones.Where(z => string.Equals(z.label, query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];

            var partial = zones.Where(z =>
                (z.label ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (partial.Count == 1) return partial[0];

            if (partial.Count == 0)
                throw CommandException.NotFound($"No zone matches '{query}'.", "zones.list shows what exists.");

            var error = new CommandException(ErrorCode.Ambiguous,
                $"'{query}' matches {partial.Count} zones.", "Use the numeric index from zones.list.");
            var candidates = JsonValue.NewArray();
            foreach (var zone in partial.Take(8))
                candidates.Add(JsonValue.NewObject()
                    .Set("index", zones.IndexOf(zone))
                    .Set("label", zone.label));
            error.Payload = JsonValue.NewObject().Set("candidates", candidates);
            throw error;
        }

        /// <summary>Reads a rectangle argument and returns the cells inside it.</summary>
        public static List<IntVec3> ReadArea(JsonValue args, Map map, string argName = "area")
        {
            var area = args[argName];
            if (area.Type != JsonType.Object)
                throw CommandException.BadArgs($"Missing '{argName}'.",
                    "Pass a rectangle as {\"x1\":10,\"z1\":10,\"x2\":20,\"z2\":20}.");

            int x1 = area["x1"].AsInt(), z1 = area["z1"].AsInt();
            int x2 = area["x2"].AsInt(), z2 = area["z2"].AsInt();

            int minX = Math.Max(0, Math.Min(x1, x2));
            int maxX = Math.Min(map.Size.x - 1, Math.Max(x1, x2));
            int minZ = Math.Max(0, Math.Min(z1, z2));
            int maxZ = Math.Min(map.Size.z - 1, Math.Max(z1, z2));

            long count = (long)(maxX - minX + 1) * (maxZ - minZ + 1);
            if (count > MaxZoneCells)
                throw CommandException.BadArgs($"That rectangle is {count} cells, over the {MaxZoneCells} limit.");

            var cells = new List<IntVec3>();
            for (int x = minX; x <= maxX; x++)
                for (int z = minZ; z <= maxZ; z++)
                    cells.Add(new IntVec3(x, 0, z));

            return cells;
        }

        public static JsonValue DescribeZone(Zone zone, int index)
        {
            var result = JsonValue.NewObject()
                .Set("index", index)
                .Set("label", zone.label ?? "")
                .Set("cellCount", zone.Cells.Count)
                .Set("type", zone is Zone_Stockpile ? "stockpile" : zone is Zone_Growing ? "growing" : zone.GetType().Name);

            if (zone.Cells.Count > 0)
            {
                var min = new IntVec3(zone.Cells.Min(c => c.x), 0, zone.Cells.Min(c => c.z));
                var max = new IntVec3(zone.Cells.Max(c => c.x), 0, zone.Cells.Max(c => c.z));
                result.Set("bounds", JsonValue.NewObject()
                    .Set("x1", min.x).Set("z1", min.z).Set("x2", max.x).Set("z2", max.z));
            }

            if (zone is Zone_Growing growing)
                result.Set("growing", growing.GetPlantDefToGrow()?.label ?? "nothing");

            if (zone is Zone_Stockpile stockpile)
                result.Set("priority", stockpile.settings?.Priority.ToString() ?? "");

            return result;
        }

        public static StoragePriority ParsePriority(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "unstored": return StoragePriority.Unstored;
                case "low": return StoragePriority.Low;
                case "normal": return StoragePriority.Normal;
                case "preferred": return StoragePriority.Preferred;
                case "important": return StoragePriority.Important;
                case "critical": return StoragePriority.Critical;
                default:
                    throw CommandException.BadArgs($"Unknown priority '{value}'.",
                        "Use low, normal, preferred, important or critical.");
            }
        }
    }

    public class ZonesListCommand : CommandBase
    {
        public override string Name => "zones.list";
        public override string Description => "All stockpile and growing zones, with size, bounds and settings.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.ResolveMap(args.Has("map") ? args.OptInt("map", 0) : (int?)null);
            var zones = map.zoneManager.AllZones;

            var array = JsonValue.NewArray();
            for (int i = 0; i < zones.Count; i++)
                array.Add(ZoneHelpers.DescribeZone(zones[i], i));

            return JsonValue.NewObject().Set("count", zones.Count).Set("zones", array);
        }
    }

    public class ZonesCreateStockpileCommand : CommandBase
    {
        public override string Name => "zones.create_stockpile";
        public override string Description =>
            "Creates a stockpile over a rectangle. preset: default or dumping. Cells already zoned are skipped.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            var cells = ZoneHelpers.ReadArea(args, map);

            var preset = args.OptString("preset", "default").ToLowerInvariant() == "dumping"
                ? StorageSettingsPreset.DumpingStockpile
                : StorageSettingsPreset.DefaultStockpile;

            var zone = new Zone_Stockpile(preset, map.zoneManager);
            map.zoneManager.RegisterZone(zone);

            string name = args.OptString("name");
            if (!string.IsNullOrEmpty(name)) zone.label = name;

            int added = AddCells(zone, cells, map);

            if (added == 0)
            {
                zone.Delete();
                throw CommandException.Failed(
                    "Every cell in that rectangle is already zoned or unusable.",
                    "Pick an area that is not already covered by a zone.");
            }

            if (args.Has("priority"))
                zone.settings.Priority = ZoneHelpers.ParsePriority(args.RequireString("priority"));

            return JsonValue.NewObject()
                .Set("zone", ZoneHelpers.DescribeZone(zone, map.zoneManager.AllZones.IndexOf(zone)))
                .Set("cellsAdded", added)
                .Set("cellsSkipped", cells.Count - added)
                .Set("summary", $"Created stockpile '{zone.label}' with {added} cells.");
        }

        internal static int AddCells(Zone zone, List<IntVec3> cells, Map map)
        {
            int added = 0;
            foreach (var cell in cells)
            {
                // A cell can only belong to one zone, and the game will throw if we force it.
                if (map.zoneManager.ZoneAt(cell) != null) continue;
                if (!cell.InBounds(map)) continue;

                try
                {
                    zone.AddCell(cell);
                    added++;
                }
                catch (Exception)
                {
                    // Fogged or otherwise unusable cell; skip it.
                }
            }
            return added;
        }
    }

    public class ZonesCreateGrowingCommand : CommandBase
    {
        public override string Name => "zones.create_growing";
        public override string Description => "Creates a growing zone over a rectangle, optionally setting the crop.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            var cells = ZoneHelpers.ReadArea(args, map);

            var zone = new Zone_Growing(map.zoneManager);
            map.zoneManager.RegisterZone(zone);

            string name = args.OptString("name");
            if (!string.IsNullOrEmpty(name)) zone.label = name;

            int added = ZonesCreateStockpileCommand.AddCells(zone, cells, map);

            if (added == 0)
            {
                zone.Delete();
                throw CommandException.Failed(
                    "Every cell in that rectangle is already zoned or cannot be sown.",
                    "Growing zones need open, sowable ground.");
            }

            if (args.Has("plant"))
            {
                var plant = DefResolver.Resolve<ThingDef>(args.RequireString("plant"), "plant",
                    d => d.plant != null && d.plant.Sowable);
                zone.SetPlantDefToGrow(plant);
            }

            return JsonValue.NewObject()
                .Set("zone", ZoneHelpers.DescribeZone(zone, map.zoneManager.AllZones.IndexOf(zone)))
                .Set("cellsAdded", added)
                .Set("cellsSkipped", cells.Count - added)
                .Set("summary", $"Created growing zone '{zone.label}' with {added} cells.");
        }
    }

    public class ZonesSetPlantCommand : CommandBase
    {
        public override string Name => "zones.set_plant";
        public override string Description => "Changes the crop grown in a growing zone.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            var zone = ZoneHelpers.ResolveZone(args, map);

            if (!(zone is Zone_Growing growing))
                throw CommandException.BadArgs($"'{zone.label}' is not a growing zone.");

            var plant = DefResolver.Resolve<ThingDef>(args.RequireString("plant"), "plant",
                d => d.plant != null && d.plant.Sowable);

            string previous = growing.GetPlantDefToGrow()?.label ?? "nothing";
            growing.SetPlantDefToGrow(plant);

            return JsonValue.NewObject()
                .Set("zone", zone.label)
                .Set("from", previous)
                .Set("to", plant.label)
                .Set("summary", $"'{zone.label}' now grows {plant.label} (was {previous}).");
        }
    }

    public class ZonesExpandCommand : CommandBase
    {
        public override string Name => "zones.expand";
        public override string Description => "Adds a rectangle of cells to an existing zone.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            var zone = ZoneHelpers.ResolveZone(args, map);
            var cells = ZoneHelpers.ReadArea(args, map);

            int added = ZonesCreateStockpileCommand.AddCells(zone, cells, map);

            return JsonValue.NewObject()
                .Set("zone", ZoneHelpers.DescribeZone(zone, map.zoneManager.AllZones.IndexOf(zone)))
                .Set("cellsAdded", added)
                .Set("summary", $"Added {added} cells to '{zone.label}'.");
        }
    }

    public class ZonesDeleteCommand : CommandBase
    {
        public override string Name => "zones.delete";
        public override string Description => "Deletes a zone. The things inside it are untouched.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            var zone = ZoneHelpers.ResolveZone(args, map);

            string label = zone.label;
            int cells = zone.Cells.Count;
            zone.Delete();

            return JsonValue.NewObject()
                .Set("deleted", label)
                .Set("cells", cells)
                .Set("summary", $"Deleted zone '{label}' ({cells} cells).");
        }
    }

    // ---- storage settings --------------------------------------------------------------------

    public class StorageGetCommand : CommandBase
    {
        public override string Name => "storage.get_settings";
        public override string Description => "Storage priority and a summary of what a stockpile or shelf accepts.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            var settings = StorageHelpers.ResolveSettings(args, map, out string label);

            var allowed = DefDatabase<ThingDef>.AllDefsListForReading
                .Where(d => d.EverStorable(false))
                .Where(d => settings.filter.Allows(d))
                .ToList();

            return JsonValue.NewObject()
                .Set("target", label)
                .Set("priority", settings.Priority.ToString())
                .Set("allowedCount", allowed.Count)
                .Set("allowedSample", DefResolver.Describe(allowed.Take(30)))
                .Set("note", allowed.Count > 30 ? "Showing the first 30 of the allowed things." : "");
        }
    }

    internal static class StorageHelpers
    {
        public static StorageSettings ResolveSettings(JsonValue args, Map map, out string label)
        {
            // A stockpile zone or any building with storage settings (shelves, and in 1.6 a
            // number of production buildings) can be targeted the same way.
            if (args.Has("zone"))
            {
                var zone = ZoneHelpers.ResolveZone(args, map);
                if (!(zone is Zone_Stockpile stockpile))
                    throw CommandException.BadArgs($"'{zone.label}' is not a stockpile.");
                label = zone.label;
                return stockpile.settings;
            }

            if (args.Has("building"))
            {
                var thing = Refs.ResolveThing(args, "building");
                if (!(thing is IStoreSettingsParent parent))
                    throw CommandException.BadArgs($"{thing.LabelShort} has no storage settings.");
                label = thing.LabelShort;
                return parent.GetStoreSettings();
            }

            throw CommandException.BadArgs("Pass either 'zone' or 'building'.",
                "zones.list shows stockpiles; map.things with category building finds shelves.");
        }
    }

    public class StorageSetPriorityCommand : CommandBase
    {
        public override string Name => "storage.set_priority";
        public override string Description => "Sets storage priority: low, normal, preferred, important or critical.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            var settings = StorageHelpers.ResolveSettings(args, map, out string label);

            var previous = settings.Priority;
            settings.Priority = ZoneHelpers.ParsePriority(args.RequireString("priority"));

            return JsonValue.NewObject()
                .Set("target", label)
                .Set("from", previous.ToString())
                .Set("to", settings.Priority.ToString())
                .Set("summary", $"{label} priority {previous} -> {settings.Priority}.");
        }
    }

    public class StorageAllowCommand : CommandBase
    {
        public override string Name => "storage.set_allowed";
        public override string Description =>
            "Allows or disallows things in a stockpile. Pass things (names), or all:true to allow/disallow everything.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            var settings = StorageHelpers.ResolveSettings(args, map, out string label);
            bool allow = args.OptBool("allow", true);

            if (args.OptBool("all"))
            {
                if (allow) settings.filter.SetAllowAll(null);
                else settings.filter.SetDisallowAll();

                return JsonValue.NewObject()
                    .Set("target", label)
                    .Set("summary", $"{label} now {(allow ? "accepts everything" : "accepts nothing")}.");
            }

            var names = args.RequireStringList("things");
            var changed = new List<string>();

            foreach (string name in names)
            {
                var def = DefResolver.Resolve<ThingDef>(name, "things", d => d.EverStorable(false));
                settings.filter.SetAllow(def, allow);
                changed.Add(def.label);
            }

            return JsonValue.NewObject()
                .Set("target", label)
                .Set("allow", allow)
                .Set("changed", Describe.ToArray(changed))
                .Set("summary", $"{label}: {(allow ? "allowed" : "disallowed")} {changed.Count} thing(s).");
        }
    }

    // ---- allowed areas -----------------------------------------------------------------------

    public class AreasListCommand : CommandBase
    {
        public override string Name => "areas.list";
        public override string Description => "Allowed areas and the built-in home, roof and no-roof areas.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.ResolveMap(args.Has("map") ? args.OptInt("map", 0) : (int?)null);

            var array = JsonValue.NewArray();
            foreach (var area in map.areaManager.AllAreas)
            {
                array.Add(JsonValue.NewObject()
                    .Set("label", area.Label)
                    .Set("cellCount", area.TrueCount)
                    .Set("assignable", area.AssignableAsAllowed())
                    .Set("type", area.GetType().Name));
            }

            return JsonValue.NewObject().Set("count", array.Count).Set("areas", array);
        }
    }

    public class AreasCreateCommand : CommandBase
    {
        public override string Name => "areas.create";
        public override string Description => "Creates a named allowed area covering a rectangle.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            var cells = ZoneHelpers.ReadArea(args, map);

            if (!map.areaManager.CanMakeNewAllowed())
                throw CommandException.Failed("The maximum number of allowed areas already exists.",
                    "Delete one in the Assign tab first.");

            if (!map.areaManager.TryMakeNewAllowed(out Area_Allowed area))
                throw CommandException.Failed("Could not create a new allowed area.");

            string name = args.OptString("name");
            if (!string.IsNullOrEmpty(name)) area.SetLabel(name);

            foreach (var cell in cells) area[cell] = true;

            return JsonValue.NewObject()
                .Set("label", area.Label)
                .Set("cellCount", area.TrueCount)
                .Set("summary", $"Created area '{area.Label}' covering {area.TrueCount} cells.");
        }
    }

    public class AreasModifyCommand : CommandBase
    {
        public override string Name => "areas.modify";
        public override string Description => "Adds or removes a rectangle of cells from an allowed area. Pass include:false to remove.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            string name = args.RequireString("area");

            var area = map.areaManager.AllAreas
                .FirstOrDefault(a => string.Equals(a.Label, name, StringComparison.OrdinalIgnoreCase));

            if (area == null)
                throw CommandException.NotFound($"No area named '{name}'.", "areas.list shows what exists.");

            if (!area.Mutable)
                throw CommandException.Failed($"Area '{area.Label}' cannot be edited.");

            var cells = ZoneHelpers.ReadArea(args, map);
            bool include = args.OptBool("include", true);

            foreach (var cell in cells)
                if (cell.InBounds(map)) area[cell] = include;

            return JsonValue.NewObject()
                .Set("label", area.Label)
                .Set("cellCount", area.TrueCount)
                .Set("summary", $"Area '{area.Label}' now covers {area.TrueCount} cells.");
        }
    }
}
