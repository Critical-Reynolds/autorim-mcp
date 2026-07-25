using System;
using System.Collections.Generic;
using System.Linq;
using AutoRim.Bridge;
using AutoRim.Core;
using AutoRim.Read;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoRim.Commands
{
    /// <summary>
    /// Equipment and apparel.
    ///
    /// These go through real jobs rather than shoving the item into the pawn's hands. The pawn
    /// walks over, picks it up, and puts it on — which means reservations, reachability and
    /// interruptions all behave the way the player expects, and a contested item does not get
    /// duplicated.
    /// </summary>
    internal static class EquipHelpers
    {
        public static Thing ResolveItem(JsonValue args, Pawn pawn, string argName = "item")
        {
            var node = args[argName];

            // By id: the normal path, using ids from pawns.list_equippable or map.things.
            if (node.Type == JsonType.Number || int.TryParse(node.AsString(""), out _))
                return Refs.ResolveThing(args, argName);

            // By name: convenient, but only unambiguous within reach of this pawn.
            string query = node.AsString();
            if (string.IsNullOrEmpty(query))
                throw CommandException.BadArgs($"Missing '{argName}' (thing id, or an item name).");

            var map = pawn.Map ?? GameState.RequireMap();
            var matches = map.listerThings.AllThings
                .Where(t => t.def != null && (t.def.IsWeapon || t is Apparel))
                .Where(t => !t.IsForbidden(Faction.OfPlayer))
                .Where(t => (t.LabelNoCount ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            t.def.defName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(t => t.Position.DistanceTo(pawn.Position))
                .ToList();

            if (matches.Count == 0)
                throw CommandException.NotFound($"No available weapon or apparel matches '{query}'.",
                    "pawns.list_equippable shows what this pawn could pick up.");

            // Several identical items is not ambiguity — take the nearest.
            var best = matches[0];
            var distinct = matches.Select(t => t.def).Distinct().ToList();
            if (distinct.Count > 1)
            {
                var error = new CommandException(ErrorCode.Ambiguous,
                    $"'{query}' matches {distinct.Count} different items.",
                    "Resend using the numeric id from pawns.list_equippable.");
                var candidates = JsonValue.NewArray();
                foreach (var thing in matches.Take(8))
                    candidates.Add(JsonValue.NewObject()
                        .Set("id", thing.thingIDNumber)
                        .Set("label", thing.LabelNoCount)
                        .Set("distance", (int)thing.Position.DistanceTo(pawn.Position)));
                error.Payload = JsonValue.NewObject().Set("candidates", candidates);
                throw error;
            }

            return best;
        }

        public static void RequireReachable(Pawn pawn, Thing item)
        {
            if (!item.Spawned)
                throw CommandException.Failed($"{item.LabelNoCount} is not on the map.",
                    "It may already be carried or stored inside something.");

            if (item.Map != pawn.Map)
                throw CommandException.Failed($"{item.LabelNoCount} is on a different map.");

            if (item.IsForbidden(Faction.OfPlayer))
                throw CommandException.Failed($"{item.LabelNoCount} is forbidden.",
                    "Use designate.unforbid on it first.");

            if (!pawn.CanReserveAndReach(item, PathEndMode.ClosestTouch, Danger.Deadly))
                throw CommandException.Failed(
                    $"{pawn.LabelShort} cannot reach {item.LabelNoCount}.",
                    "Another pawn may have claimed it, or it may be walled off.");
        }
    }

    public class PawnsListEquippableCommand : CommandBase
    {
        public override string Name => "pawns.list_equippable";
        public override string Description =>
            "Weapons and apparel on the map that a pawn could pick up, nearest first, with the reason for anything unusable.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            if (!pawn.Spawned || pawn.Map == null)
                throw CommandException.Failed($"{pawn.LabelShort} is not on a map.");

            string kind = args.OptString("kind", "all").ToLowerInvariant();
            bool includeUnusable = args.OptBool("includeUnusable", false);
            args.ReadPaging(out int offset, out int limit, 30, 100);

            var map = pawn.Map;
            var candidates = map.listerThings.AllThings
                .Where(t => t.def != null && t.Spawned)
                .Where(t => kind == "weapons" ? t.def.IsWeapon
                          : kind == "apparel" ? t is Apparel
                          : (t.def.IsWeapon || t is Apparel))
                .OrderBy(t => t.Position.DistanceTo(pawn.Position))
                .Take(400)
                .ToList();

            var usable = new List<JsonValue>();

            foreach (var thing in candidates)
            {
                string problem = Problem(pawn, thing);
                if (problem != null && !includeUnusable) continue;

                var entry = JsonValue.NewObject()
                    .Set("id", thing.thingIDNumber)
                    .Set("label", thing.LabelNoCount)
                    .Set("kind", thing.def.IsWeapon ? "weapon" : "apparel")
                    .Set("pos", Refs.Cell(thing.Position))
                    .Set("distance", (int)thing.Position.DistanceTo(pawn.Position));

                if (thing.def.IsWeapon)
                {
                    entry.Set("melee", thing.def.IsMeleeWeapon);
                    entry.Set("ranged", thing.def.IsRangedWeapon);
                }

                if (problem != null) entry.Set("unusable", problem);

                usable.Add(entry);
                if (usable.Count >= offset + limit) break;
            }

            var page = Describe.Page(usable, offset, limit, e => e);
            page.Set("pawn", Refs.Ref(pawn));
            page.Set("currentWeapon", pawn.equipment?.Primary?.LabelNoCount ?? "none");
            return page;
        }

        /// <summary>Returns why the pawn cannot use this, or null if they can.</summary>
        private static string Problem(Pawn pawn, Thing thing)
        {
            if (thing.IsForbidden(Faction.OfPlayer)) return "forbidden";

            if (thing.def.IsWeapon)
            {
                if (!EquipmentUtility.CanEquip(thing, pawn, out string reason, true))
                    return string.IsNullOrEmpty(reason) ? "cannot equip" : reason;
            }
            else if (thing is Apparel apparel)
            {
                if (!ApparelUtility.HasPartsToWear(pawn, apparel.def)) return "wrong body type";
                if (pawn.apparel != null && pawn.apparel.Wearing(apparel)) return "already worn";
            }

            if (!pawn.CanReserveAndReach(thing, PathEndMode.ClosestTouch, Danger.Deadly))
                return "unreachable or reserved";

            return null;
        }
    }

    public class PawnsEquipCommand : CommandBase
    {
        public override string Name => "pawns.equip";
        public override string Description =>
            "Orders a pawn to pick up and equip a weapon. They walk to it first; any current weapon is dropped.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            if (pawn.equipment == null)
                throw CommandException.Failed($"{pawn.LabelShort} cannot carry equipment.");

            var item = EquipHelpers.ResolveItem(args, pawn);

            if (!item.def.IsWeapon)
                throw CommandException.BadArgs($"{item.LabelNoCount} is not a weapon.",
                    item is Apparel ? "Use pawns.wear for apparel." : null);

            EquipHelpers.RequireReachable(pawn, item);

            if (!EquipmentUtility.CanEquip(item, pawn, out string reason, true))
                throw CommandException.Failed(
                    $"{pawn.LabelShort} cannot equip {item.LabelNoCount}: {reason}",
                    "A trait, ideology precept or bonded-weapon rule may forbid it.");

            string previous = pawn.equipment.Primary?.LabelNoCount ?? "nothing";

            var job = JobMaker.MakeJob(JobDefOf.Equip, item);
            job.playerForced = true;

            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                throw CommandException.Failed($"{pawn.LabelShort} would not take the equip order.");

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("item", Refs.Ref(item))
                .Set("previousWeapon", previous)
                .Set("note", "The pawn walks to the weapon first; the change is not instant.")
                .Set("summary", $"{pawn.LabelShort} ordered to equip {item.LabelNoCount} (was carrying {previous}).");
        }
    }

    public class PawnsWearCommand : CommandBase
    {
        public override string Name => "pawns.wear";
        public override string Description =>
            "Orders a pawn to pick up and wear a piece of apparel. Conflicting apparel is removed automatically.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            if (pawn.apparel == null)
                throw CommandException.Failed($"{pawn.LabelShort} cannot wear apparel.");

            var item = EquipHelpers.ResolveItem(args, pawn);

            if (!(item is Apparel apparel))
                throw CommandException.BadArgs($"{item.LabelNoCount} is not apparel.",
                    item.def.IsWeapon ? "Use pawns.equip for weapons." : null);

            EquipHelpers.RequireReachable(pawn, item);

            if (!ApparelUtility.HasPartsToWear(pawn, apparel.def))
                throw CommandException.Failed(
                    $"{pawn.LabelShort} does not have the body parts to wear {apparel.LabelNoCount}.");

            var job = JobMaker.MakeJob(JobDefOf.Wear, apparel);
            job.playerForced = true;

            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                throw CommandException.Failed($"{pawn.LabelShort} would not take the wear order.");

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("item", Refs.Ref(item))
                .Set("note", "The pawn walks to the apparel first; the change is not instant.")
                .Set("summary", $"{pawn.LabelShort} ordered to wear {apparel.LabelNoCount}.");
        }
    }

    public class PawnsUnequipCommand : CommandBase
    {
        public override string Name => "pawns.unequip";
        public override string Description =>
            "Makes a pawn drop their current weapon on the ground. Reversible: pawns.equip picks it back up.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);

            var primary = pawn.equipment?.Primary;
            if (primary == null)
                return JsonValue.NewObject()
                    .Set("pawn", Refs.Ref(pawn))
                    .Set("summary", $"{pawn.LabelShort} is not carrying a weapon.");

            if (!pawn.Spawned)
                throw CommandException.Failed($"{pawn.LabelShort} is not on a map.");

            string label = primary.LabelNoCount;

            if (!pawn.equipment.TryDropEquipment(primary, out ThingWithComps dropped, pawn.Position, false))
                throw CommandException.Failed($"{pawn.LabelShort} could not drop {label}.");

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("dropped", dropped != null ? Refs.Ref(dropped) : JsonValue.Null)
                .Set("summary", $"{pawn.LabelShort} dropped {label}.");
        }
    }
}
