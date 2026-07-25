using System;
using System.Collections.Generic;
using System.Linq;
using AutoRim.Bridge;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AutoRim.Core
{
    /// <summary>
    /// Stable identity for things the caller refers to across turns.
    ///
    /// Everything is addressed by thingIDNumber, which survives save/reload, never by list
    /// position — a pawn's index in any list shifts the moment someone dies or joins. Every
    /// reference returned on the wire carries the id alongside a human label so the assistant
    /// can talk about "Ivy" while addressing 12345.
    /// </summary>
    public static class Refs
    {
        private const int MaxCandidates = 8;

        /// <summary>Compact {id,label} reference embedded in list responses.</summary>
        public static JsonValue Ref(Thing thing)
        {
            if (thing == null) return JsonValue.Null;
            return JsonValue.NewObject()
                .Set("id", thing.thingIDNumber)
                .Set("label", thing.LabelShort ?? thing.Label ?? thing.def?.label ?? "?");
        }

        public static JsonValue Cell(IntVec3 cell) =>
            JsonValue.NewObject().Set("x", cell.x).Set("z", cell.z);

        // ---- pawns --------------------------------------------------------------------------

        /// <summary>
        /// Every pawn a command may address: on any map, plus anyone out with a caravan.
        /// </summary>
        public static IEnumerable<Pawn> AddressablePawns()
        {
            foreach (var map in Find.Maps)
            {
                if (map?.mapPawns == null) continue;
                foreach (var pawn in map.mapPawns.AllPawns)
                    if (pawn != null) yield return pawn;
            }

            var caravans = Find.WorldObjects?.Caravans;
            if (caravans == null) yield break;

            foreach (var caravan in caravans)
            {
                if (caravan?.Faction != Faction.OfPlayer) continue;
                foreach (var pawn in caravan.PawnsListForReading)
                    if (pawn != null) yield return pawn;
            }
        }

        /// <summary>
        /// Resolves a pawn from an id number or a name. Refuses ambiguous names rather than
        /// picking one, because acting on the wrong pawn is the kind of mistake that is only
        /// noticed after the fact.
        /// </summary>
        public static Pawn ResolvePawn(JsonValue args, string argName = "pawn")
        {
            var node = args[argName];

            if (node.Type == JsonType.Number)
            {
                int id = node.AsInt();
                var byId = AddressablePawns().FirstOrDefault(p => p.thingIDNumber == id);
                if (byId == null)
                    throw CommandException.NotFound($"No pawn with id {id}.",
                        "Ids come from pawns.list; they change between colonies.");
                return byId;
            }

            string query = node.AsString();
            if (string.IsNullOrEmpty(query))
                throw CommandException.BadArgs($"Missing '{argName}' (pawn id or name).");

            // A bare number arriving as a string is still an id.
            if (int.TryParse(query, out int parsedId))
            {
                var byParsedId = AddressablePawns().FirstOrDefault(p => p.thingIDNumber == parsedId);
                if (byParsedId != null) return byParsedId;
            }

            return ResolvePawnByName(query, argName);
        }

        private static Pawn ResolvePawnByName(string query, string argName)
        {
            var pawns = AddressablePawns().ToList();
            string trimmed = query.Trim();

            var exact = pawns.Where(p =>
                Equals(p.LabelShort, trimmed) ||
                Equals(p.Name?.ToStringShort, trimmed) ||
                Equals(p.Name?.ToStringFull, trimmed)).ToList();

            if (exact.Count == 1) return exact[0];
            if (exact.Count > 1) throw Ambiguous(trimmed, exact, argName);

            var partial = pawns.Where(p =>
                Contains(p.LabelShort, trimmed) ||
                Contains(p.Name?.ToStringFull, trimmed)).ToList();

            if (partial.Count == 1) return partial[0];
            if (partial.Count > 1) throw Ambiguous(trimmed, partial, argName);

            var error = CommandException.NotFound($"No pawn matches '{trimmed}'.",
                "Call pawns.list to see who is in the colony.");
            error.Payload = JsonValue.NewObject().Set("candidates", DescribePawns(pawns.Take(MaxCandidates)));
            throw error;
        }

        private static CommandException Ambiguous(string query, List<Pawn> matches, string argName)
        {
            var error = new CommandException(ErrorCode.Ambiguous,
                $"'{query}' matches {matches.Count} pawns.",
                $"Resend '{argName}' as the numeric id of the one you mean.");
            error.Payload = JsonValue.NewObject().Set("candidates", DescribePawns(matches.Take(MaxCandidates)));
            return error;
        }

        private static JsonValue DescribePawns(IEnumerable<Pawn> pawns)
        {
            var array = JsonValue.NewArray();
            foreach (var pawn in pawns)
            {
                array.Add(JsonValue.NewObject()
                    .Set("id", pawn.thingIDNumber)
                    .Set("name", pawn.Name?.ToStringFull ?? pawn.LabelShort)
                    .Set("kind", DescribeKind(pawn)));
            }
            return array;
        }

        public static string DescribeKind(Pawn pawn)
        {
            if (pawn.IsColonyMech) return "mech";
            if (pawn.IsPrisoner) return "prisoner";
            if (pawn.IsSlave) return "slave";
            if (pawn.IsColonist) return "colonist";
            if (pawn.RaceProps != null && pawn.RaceProps.Animal)
                return pawn.Faction == Faction.OfPlayer ? "colony animal" : "animal";
            if (pawn.Faction != null && pawn.Faction.HostileTo(Faction.OfPlayer)) return "hostile";
            return "other";
        }

        // ---- things -------------------------------------------------------------------------

        /// <summary>Resolves a spawned thing by id, searching every loaded map.</summary>
        public static Thing ResolveThing(JsonValue args, string argName = "thing")
        {
            var node = args[argName];

            int id;
            if (node.Type == JsonType.Number) id = node.AsInt();
            else if (!int.TryParse(node.AsString(""), out id))
                throw CommandException.BadArgs($"'{argName}' must be a thing id (a number).",
                    "Ids come from map.things, pawns.list and similar reads.");

            foreach (var map in Find.Maps)
            {
                var match = map.listerThings?.AllThings?.FirstOrDefault(t => t.thingIDNumber == id);
                if (match != null) return match;
            }

            throw CommandException.NotFound($"No thing with id {id} on any loaded map.");
        }

        // ---- cells --------------------------------------------------------------------------

        /// <summary>
        /// Reads a map cell. Accepts {"x":10,"z":20} or [10,20]. RimWorld's ground plane is
        /// x/z; y is elevation and is always zero for map cells.
        /// </summary>
        public static IntVec3 RequireCell(JsonValue args, string argName, Map map)
        {
            var node = args[argName];
            int x, z;

            if (node.Type == JsonType.Object && node.Has("x") && node.Has("z"))
            {
                x = node["x"].AsInt();
                z = node["z"].AsInt();
            }
            else if (node.Type == JsonType.Array && node.Count >= 2)
            {
                x = node[0].AsInt();
                z = node[1].AsInt();
            }
            else
            {
                throw CommandException.BadArgs($"Missing or malformed '{argName}'.",
                    "Pass a cell as {\"x\":10,\"z\":20} or [10,20].");
            }

            var cell = new IntVec3(x, 0, z);
            if (!cell.InBounds(map))
                throw CommandException.BadArgs(
                    $"Cell ({x},{z}) is outside the map, which is {map.Size.x} by {map.Size.z}.");

            return cell;
        }

        private static bool Equals(string a, string b) =>
            a != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static bool Contains(string haystack, string needle) =>
            haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
