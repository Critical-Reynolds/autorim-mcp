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
    /// Shared implementation for every designation order (hunt, mine, chop, harvest, tame,
    /// haul, forbid, deconstruct and the rest).
    ///
    /// Designations are how a player actually asks for work in RimWorld: you mark the thing,
    /// and whoever has the relevant work type gets to it. Going through the real Designator
    /// classes rather than writing Designation objects by hand means every eligibility rule,
    /// side effect and error message is the game's own.
    ///
    /// Cell-based designators (mine, smooth) and thing-based ones (hunt, tame) are handled by
    /// the same path: each candidate is offered to both CanDesignateCell and CanDesignateThing,
    /// and whichever the designator accepts is the one that applies.
    /// </summary>
    public abstract class DesignateCommandBase : CommandBase
    {
        /// <summary>Guards against a typo'd rectangle locking the game up for minutes.</summary>
        private const int MaxAreaCells = 10000;

        private const int MaxReportedFailures = 5;

        protected abstract Designator MakeDesignator();

        public override JsonValue Execute(JsonValue args) => Run(args, apply: true);

        protected JsonValue Run(JsonValue args, bool apply)
        {
            var map = Find.CurrentMap;
            if (map == null) throw CommandException.NoGame();

            var designator = MakeDesignator();

            var thingTargets = new List<Thing>();
            var cellTargets = new List<IntVec3>();
            CollectTargets(args, map, thingTargets, cellTargets);

            if (thingTargets.Count == 0 && cellTargets.Count == 0)
                throw CommandException.BadArgs(
                    "Nothing to designate.",
                    "Pass things (array of ids), cell, cells, or area {x1,z1,x2,z2}.");

            int designated = 0;
            var affected = new List<Thing>();
            var reasons = new Dictionary<string, int>();

            foreach (var cell in cellTargets)
            {
                var report = SafeCanDesignateCell(designator, cell);
                if (report.Accepted)
                {
                    if (apply) designator.DesignateSingleCell(cell);
                    designated++;
                }
                else if (!string.IsNullOrEmpty(report.Reason))
                {
                    Count(reasons, report.Reason);
                }
            }

            foreach (var thing in thingTargets)
            {
                var report = SafeCanDesignateThing(designator, thing);
                if (report.Accepted)
                {
                    if (apply) designator.DesignateThing(thing);
                    designated++;
                    if (affected.Count < 50) affected.Add(thing);
                }
                else
                {
                    Count(reasons, ExplainRejection(map, thing, report));
                }
            }

            if (apply && designated > 0)
            {
                // Finalize is what plays the sound, closes out the drag and lets the designator
                // do any bookkeeping; skipping it leaves designations that look wrong in game.
                try
                {
                    designator.Finalize(true);
                }
                catch (Exception ex)
                {
                    ARLog.Exception($"finalizing {Name}", ex);
                }
            }

            string action = Name.Substring(Name.IndexOf('.') + 1);
            var result = JsonValue.NewObject()
                .Set("action", action)
                .Set("designated", designated)
                .Set("consideredThings", thingTargets.Count)
                .Set("consideredCells", cellTargets.Count)
                .Set("applied", apply);

            if (affected.Count > 0)
            {
                var array = JsonValue.NewArray();
                foreach (var thing in affected) array.Add(Refs.Ref(thing));
                result.Set("targets", array);
            }

            if (reasons.Count > 0)
            {
                var skipped = JsonValue.NewArray();
                foreach (var entry in reasons.OrderByDescending(e => e.Value).Take(MaxReportedFailures))
                    skipped.Add(JsonValue.NewObject().Set("reason", entry.Key).Set("count", entry.Value));
                result.Set("skipped", skipped);
            }

            result.Set("summary", apply
                ? $"{action}: designated {designated}."
                : $"{action}: would designate {designated}.");

            if (designated == 0 && apply)
                result.Set("note", "Nothing matched. The skipped reasons explain why.");

            return result;
        }

        private void CollectTargets(JsonValue args, Map map, List<Thing> things, List<IntVec3> cells)
        {
            // Hash-based dedup, not List.Contains. A multi-cell building is returned once per
            // cell it occupies, so sweeping a large area would otherwise be quadratic — on the
            // main thread, inside the frame budget.
            var seen = new HashSet<Thing>();

            // Explicit thing ids.
            var ids = args.OptIntList("things");
            foreach (int id in ids)
            {
                var match = map.listerThings.AllThings.FirstOrDefault(t => t.thingIDNumber == id)
                            ?? map.mapPawns.AllPawns.FirstOrDefault(p => p.thingIDNumber == id);
                if (match == null)
                    throw CommandException.NotFound($"No thing with id {id} on the current map.");
                if (seen.Add(match)) things.Add(match);
            }

            // Single cell.
            if (args.Has("cell")) cells.Add(Refs.RequireCell(args, "cell", map));

            // Explicit cell list.
            if (args["cells"].Type == JsonType.Array)
            {
                foreach (var node in args["cells"].Items)
                {
                    var wrapper = JsonValue.NewObject().Set("c", node);
                    cells.Add(Refs.RequireCell(wrapper, "c", map));
                }
            }

            // Rectangle, optionally narrowed to one kind of thing.
            if (args.Has("area"))
            {
                var area = args["area"];
                int x1 = area["x1"].AsInt(), z1 = area["z1"].AsInt();
                int x2 = area["x2"].AsInt(), z2 = area["z2"].AsInt();

                int minX = Math.Max(0, Math.Min(x1, x2));
                int maxX = Math.Min(map.Size.x - 1, Math.Max(x1, x2));
                int minZ = Math.Max(0, Math.Min(z1, z2));
                int maxZ = Math.Min(map.Size.z - 1, Math.Max(z1, z2));

                long cellCount = (long)(maxX - minX + 1) * (maxZ - minZ + 1);
                if (cellCount > MaxAreaCells)
                    throw CommandException.BadArgs(
                        $"Area covers {cellCount} cells, more than the {MaxAreaCells} limit.",
                        "Work in smaller rectangles.");

                ThingDef filter = null;
                if (args.Has("defName"))
                    filter = DefResolver.Resolve<ThingDef>(args.RequireString("defName"), "defName");

                for (int x = minX; x <= maxX; x++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        var cell = new IntVec3(x, 0, z);
                        if (filter == null) cells.Add(cell);

                        foreach (var thing in map.thingGrid.ThingsListAtFast(cell))
                        {
                            if (filter != null && thing.def != filter) continue;
                            if (seen.Add(thing)) things.Add(thing);
                        }
                    }
                }
            }

            // Whole map, narrowed by def. "hunt every muffalo" without drawing a rectangle.
            if (args.OptBool("wholeMap") && args.Has("defName"))
            {
                var filter = DefResolver.Resolve<ThingDef>(args.RequireString("defName"), "defName");
                foreach (var thing in map.listerThings.AllThings.Where(t => t.def == filter))
                    if (seen.Add(thing)) things.Add(thing);

                foreach (var pawn in map.mapPawns.AllPawnsSpawned.Where(p => p.def == filter))
                    if (seen.Add(pawn)) things.Add(pawn);
            }
        }

        /// <summary>
        /// Designators usually reject a target with a bare false and no reason — most often
        /// because it is already designated. Reporting "designated 1 of 2" with no explanation
        /// leaves the caller guessing, so fill in the common case ourselves.
        /// </summary>
        private static string ExplainRejection(Map map, Thing thing, AcceptanceReport report)
        {
            if (!string.IsNullOrEmpty(report.Reason)) return report.Reason;

            try
            {
                var existing = map.designationManager?.AllDesignationsOn(thing);
                if (existing != null && existing.Any())
                    return "already designated";
            }
            catch (Exception)
            {
            }

            return "not a valid target for this designation";
        }

        private static AcceptanceReport SafeCanDesignateThing(Designator designator, Thing thing)
        {
            try
            {
                return designator.CanDesignateThing(thing);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static AcceptanceReport SafeCanDesignateCell(Designator designator, IntVec3 cell)
        {
            try
            {
                return designator.CanDesignateCell(cell);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void Count(IDictionary<string, int> counts, string reason)
        {
            counts.TryGetValue(reason, out int existing);
            counts[reason] = existing + 1;
        }
    }

    // ---- reversible designations ----------------------------------------------------------

    public class DesignateHuntCommand : DesignateCommandBase
    {
        public override string Name => "designate.hunt";
        public override string Description => "Marks animals for hunting. Use wholeMap+defName to hunt every animal of one kind.";
        protected override Designator MakeDesignator() => new Designator_Hunt();
    }

    public class DesignateMineCommand : DesignateCommandBase
    {
        public override string Name => "designate.mine";
        public override string Description => "Marks rock or ore cells for mining.";
        protected override Designator MakeDesignator() => new Designator_Mine();
    }

    public class DesignateMineVeinCommand : DesignateCommandBase
    {
        public override string Name => "designate.mine_vein";
        public override string Description => "Marks a whole ore vein for mining from one cell in it.";
        protected override Designator MakeDesignator() => new Designator_MineVein();
    }

    public class DesignateChopCommand : DesignateCommandBase
    {
        public override string Name => "designate.chop";
        public override string Description => "Marks trees for chopping, keeping the wood.";
        protected override Designator MakeDesignator() => new Designator_PlantsHarvestWood();
    }

    public class DesignateCutCommand : DesignateCommandBase
    {
        public override string Name => "designate.cut";
        public override string Description => "Marks plants to be cut down and discarded.";
        protected override Designator MakeDesignator() => new Designator_PlantsCut();
    }

    public class DesignateHarvestCommand : DesignateCommandBase
    {
        public override string Name => "designate.harvest";
        public override string Description => "Marks grown crops for harvesting.";
        protected override Designator MakeDesignator() => new Designator_PlantsHarvest();
    }

    public class DesignateTameCommand : DesignateCommandBase
    {
        public override string Name => "designate.tame";
        public override string Description => "Marks wild animals for taming.";
        protected override Designator MakeDesignator() => new Designator_Tame();
    }

    public class DesignateHaulCommand : DesignateCommandBase
    {
        public override string Name => "designate.haul";
        public override string Description => "Marks items to be hauled to storage.";
        protected override Designator MakeDesignator() => new Designator_Haul();
    }

    public class DesignateForbidCommand : DesignateCommandBase
    {
        public override string Name => "designate.forbid";
        public override string Description => "Forbids things, so colonists leave them alone.";
        protected override Designator MakeDesignator() => new Designator_Forbid();
    }

    public class DesignateUnforbidCommand : DesignateCommandBase
    {
        public override string Name => "designate.unforbid";
        public override string Description => "Allows previously forbidden things.";
        protected override Designator MakeDesignator() => new Designator_Unforbid();
    }

    public class DesignateClaimCommand : DesignateCommandBase
    {
        public override string Name => "designate.claim";
        public override string Description => "Claims unowned buildings for the colony.";
        protected override Designator MakeDesignator() => new Designator_Claim();
    }

    public class DesignateSmoothCommand : DesignateCommandBase
    {
        public override string Name => "designate.smooth";
        public override string Description => "Marks rough stone floors or walls to be smoothed.";
        protected override Designator MakeDesignator() => new Designator_SmoothSurface();
    }

    public class DesignateCancelCommand : DesignateCommandBase
    {
        public override string Name => "designate.cancel";
        public override string Description => "Cancels designations and construction orders in an area.";
        protected override Designator MakeDesignator() => new Designator_Cancel();
    }

    public class DesignateListCommand : CommandBase
    {
        public override string Name => "designate.list";
        public override string Description => "Current outstanding designations on the map, grouped by kind.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            var manager = map.designationManager;
            if (manager == null) return JsonValue.NewObject().Set("designations", JsonValue.NewArray());

            var groups = manager.AllDesignations
                .Where(d => d?.def != null)
                .GroupBy(d => d.def)
                .OrderByDescending(g => g.Count())
                .ToList();

            var array = JsonValue.NewArray();
            foreach (var group in groups)
            {
                var sample = JsonValue.NewArray();
                foreach (var designation in group.Take(5))
                {
                    sample.Add(designation.target.HasThing
                        ? Refs.Ref(designation.target.Thing)
                        : Refs.Cell(designation.target.Cell));
                }

                array.Add(JsonValue.NewObject()
                    .Set("defName", group.Key.defName)
                    .Set("count", group.Count())
                    .Set("sample", sample));
            }

            return JsonValue.NewObject()
                .Set("totalDesignations", manager.AllDesignations.Count())
                .Set("byKind", array);
        }
    }

    // ---- irreversible designations ---------------------------------------------------------

    /// <summary>
    /// Slaughtering, stripping and deconstruction destroy something the colony owns, so they
    /// sit behind the confirm gate and describe exactly what they would hit first.
    /// </summary>
    public abstract class DestructiveDesignateCommandBase : DesignateCommandBase, IPreviewable
    {
        public override SafetyTier Tier => SafetyTier.Destructive;

        public JsonValue Preview(JsonValue args) => Run(args, apply: false);
    }

    public class DesignateSlaughterCommand : DestructiveDesignateCommandBase
    {
        public override string Name => "designate.slaughter";
        public override string Description => "Marks colony animals to be slaughtered. Irreversible; needs confirm.";
        protected override Designator MakeDesignator() => new Designator_Slaughter();
    }

    public class DesignateDeconstructCommand : DestructiveDesignateCommandBase
    {
        public override string Name => "designate.deconstruct";
        public override string Description => "Marks buildings for deconstruction. Returns some materials; needs confirm.";
        protected override Designator MakeDesignator() => new Designator_Deconstruct();
    }

    public class DesignateStripCommand : DestructiveDesignateCommandBase
    {
        public override string Name => "designate.strip";
        public override string Description => "Marks pawns or corpses to be stripped of gear. Needs confirm.";
        protected override Designator MakeDesignator() => new Designator_Strip();
    }

    public class DesignateReleaseCommand : DestructiveDesignateCommandBase
    {
        public override string Name => "designate.release_animal";
        public override string Description => "Releases tamed animals back to the wild. They stop being yours; needs confirm.";
        protected override Designator MakeDesignator() => new Designator_ReleaseAnimalToWild();
    }

    public class DesignateUninstallCommand : DestructiveDesignateCommandBase
    {
        public override string Name => "designate.uninstall";
        public override string Description => "Marks buildings to be uninstalled into minified form. Needs confirm.";
        protected override Designator MakeDesignator() => new Designator_Uninstall();
    }
}
