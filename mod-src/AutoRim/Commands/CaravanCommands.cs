using System;
using System.Collections.Generic;
using System.Linq;
using AutoRim.Bridge;
using AutoRim.Core;
using AutoRim.Read;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AutoRim.Commands
{
    public class CaravanListCommand : CommandBase
    {
        public override string Name => "caravan.list";
        public override string Description => "Your caravans on the world map, who is in them and how loaded they are.";

        public override JsonValue Execute(JsonValue args)
        {
            var caravans = Find.WorldObjects?.Caravans?.Where(c => c.IsPlayerControlled).ToList()
                           ?? new List<Caravan>();

            var array = JsonValue.NewArray();
            foreach (var caravan in caravans)
            {
                var members = JsonValue.NewArray();
                foreach (var pawn in caravan.PawnsListForReading) members.Add(Refs.Ref(pawn));

                array.Add(JsonValue.NewObject()
                    .Set("id", caravan.ID)
                    .Set("name", caravan.Name ?? "")
                    .Set("tile", caravan.Tile.tileId)
                    .Set("pawnCount", caravan.PawnsListForReading.Count)
                    .Set("massUsage", Describe.Round(caravan.MassUsage))
                    .Set("massCapacity", Describe.Round(caravan.MassCapacity))
                    .Set("overloaded", caravan.ImmobilizedByMass)
                    .Set("members", members));
            }

            return JsonValue.NewObject().Set("count", array.Count).Set("caravans", array);
        }
    }

    public class CaravanSendableCommand : CommandBase
    {
        public override string Name => "caravan.sendable";
        public override string Description => "Pawns that could leave with a caravan right now, and nearby destinations.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();

            List<Pawn> sendable;
            try
            {
                sendable = CaravanFormingUtility.AllSendablePawns(map, false, false, false, false, false, -1);
            }
            catch (Exception ex)
            {
                ARLog.Exception("listing sendable pawns", ex);
                sendable = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed).ToList();
            }

            var pawns = JsonValue.NewArray();
            foreach (var pawn in sendable) pawns.Add(Describe.PawnSummary(pawn));

            var destinations = JsonValue.NewArray();
            var settlements = Find.WorldObjects?.Settlements ?? new List<Settlement>();
            foreach (var settlement in settlements.Take(40))
            {
                destinations.Add(JsonValue.NewObject()
                    .Set("name", settlement.Name ?? "")
                    .Set("tile", settlement.Tile.tileId)
                    .Set("faction", settlement.Faction?.Name ?? "")
                    .Set("hostile", settlement.Faction?.HostileTo(Faction.OfPlayer) ?? false)
                    .Set("hasTrader", settlement.TraderKind != null));
            }

            return JsonValue.NewObject()
                .Set("sendableCount", pawns.Count)
                .Set("sendable", pawns)
                .Set("destinations", destinations)
                .Set("note",
                    "caravan.form sends pawns immediately with whatever they carry. To haul a large cargo load, use the in-game Form Caravan dialog.");
        }
    }

    /// <summary>
    /// Sends pawns off the map as a caravan.
    ///
    /// This uses the immediate-departure path, so the pawns leave with what they are already
    /// carrying. Assembling a cargo manifest first is the game's multi-step loading flow and is
    /// not covered here; caravan.sendable says so in its output rather than leaving the caller
    /// to discover it.
    /// </summary>
    public class CaravanFormCommand : CommandBase, IPreviewable
    {
        public override string Name => "caravan.form";
        public override SafetyTier Tier => SafetyTier.Destructive;
        public override string Description =>
            "Sends pawns off the map as a caravan, immediately. They leave the colony undefended by that much; needs confirm.";

        public override JsonValue Execute(JsonValue args) => Run(args, true);
        public JsonValue Preview(JsonValue args) => Run(args, false);

        private JsonValue Run(JsonValue args, bool apply)
        {
            var map = GameState.RequireMap();

            var names = args.RequireStringList("pawns");
            var pawns = new List<Pawn>();
            foreach (string name in names)
            {
                var wrapper = JsonValue.NewObject().Set("pawn", name);
                var pawn = Refs.ResolvePawn(wrapper);

                if (!pawn.Spawned || pawn.Map != map)
                    throw CommandException.Failed($"{pawn.LabelShort} is not on the current map.");
                if (pawn.Downed)
                    throw CommandException.Failed($"{pawn.LabelShort} is downed and cannot walk out.");

                if (!pawns.Contains(pawn)) pawns.Add(pawn);
            }

            if (pawns.Count == 0)
                throw CommandException.BadArgs("No pawns to send.");

            var destination = ResolveDestination(args, map);

            PlanetTile exitTile;
            try
            {
                exitTile = CaravanExitMapUtility.BestExitTileToGoTo(destination, map);
                if (!exitTile.Valid) exitTile = CaravanExitMapUtility.RandomBestExitTileFrom(map);
            }
            catch (Exception)
            {
                exitTile = CaravanExitMapUtility.RandomBestExitTileFrom(map);
            }

            if (!exitTile.Valid)
                throw CommandException.Failed("No usable exit tile from this map.",
                    "The map may be enclosed, or the destination unreachable overland.");

            int remainingColonists = map.mapPawns.FreeColonistsSpawnedCount - pawns.Count(p => p.IsFreeColonist);

            if (!apply)
            {
                var members = JsonValue.NewArray();
                foreach (var pawn in pawns) members.Add(Describe.PawnSummary(pawn));

                return JsonValue.NewObject()
                    .Set("wouldSend", members)
                    .Set("destinationTile", destination.tileId)
                    .Set("exitTile", exitTile.tileId)
                    .Set("colonistsLeftBehind", remainingColonists)
                    .Set("warning", remainingColonists <= 0
                        ? "This would leave the colony with NO colonists on the map."
                        : $"The colony would be left with {remainingColonists} colonist(s). Pawns leave with only what they carry.");
            }

            var caravan = CaravanExitMapUtility.ExitMapAndCreateCaravan(
                pawns, Faction.OfPlayer, exitTile, destination, destination, true);

            if (caravan == null)
                throw CommandException.Failed("The caravan could not be created.");

            return JsonValue.NewObject()
                .Set("caravanId", caravan.ID)
                .Set("pawnCount", caravan.PawnsListForReading.Count)
                .Set("destinationTile", destination.tileId)
                .Set("colonistsLeftBehind", remainingColonists)
                .Set("summary", $"Caravan of {caravan.PawnsListForReading.Count} left for tile {destination.tileId}.");
        }

        private static PlanetTile ResolveDestination(JsonValue args, Map map)
        {
            if (args.Has("destinationTile"))
                return new PlanetTile(args.RequireInt("destinationTile"), map.Tile.Layer);

            if (args.Has("destination"))
            {
                string query = args.RequireString("destination");
                var settlements = Find.WorldObjects?.Settlements ?? new List<Settlement>();

                var matches = settlements
                    .Where(s => (s.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

                if (matches.Count == 1) return matches[0].Tile;

                var error = matches.Count == 0
                    ? CommandException.NotFound($"No settlement matches '{query}'.")
                    : new CommandException(ErrorCode.Ambiguous,
                        $"'{query}' matches {matches.Count} settlements.", "Use destinationTile instead.");
                error.Payload = JsonValue.NewObject().Set("settlements", Describe.ToArray(
                    settlements.Take(30).Select(s => $"{s.Name} (tile {s.Tile.tileId})")));
                throw error;
            }

            throw CommandException.BadArgs("Pass 'destination' (settlement name) or 'destinationTile'.",
                "caravan.sendable lists reachable destinations.");
        }
    }

    // ---- world -----------------------------------------------------------------------------------

    public class WorldFactionsCommand : CommandBase
    {
        public override string Name => "world.factions";
        public override string Description => "Known factions and how they feel about the colony.";

        public override JsonValue Execute(JsonValue args)
        {
            var factions = Find.FactionManager?.AllFactionsVisibleInViewOrder?.ToList() ?? new List<Faction>();
            var player = Faction.OfPlayer;

            var array = JsonValue.NewArray();
            foreach (var faction in factions)
            {
                if (faction == player) continue;

                array.Add(JsonValue.NewObject()
                    .Set("name", faction.Name ?? "")
                    .Set("type", faction.def?.label ?? "")
                    .Set("goodwill", faction.GoodwillWith(player))
                    .Set("relation", faction.RelationKindWith(player).ToString())
                    .Set("hostile", faction.HostileTo(player)));
            }

            return JsonValue.NewObject().Set("count", array.Count).Set("factions", array);
        }
    }

    public class WorldSettlementsCommand : CommandBase
    {
        public override string Name => "world.settlements";
        public override string Description => "Settlements on the world map, with faction, trader kind and tile.";

        public override JsonValue Execute(JsonValue args)
        {
            var settlements = Find.WorldObjects?.Settlements ?? new List<Settlement>();
            bool tradersOnly = args.OptBool("tradersOnly");

            var filtered = settlements
                .Where(s => !tradersOnly || s.TraderKind != null)
                .ToList();

            args.ReadPaging(out int offset, out int limit, 40, 150);

            return Describe.Page(filtered, offset, limit, s => JsonValue.NewObject()
                .Set("name", s.Name ?? "")
                .Set("tile", s.Tile.tileId)
                .Set("faction", s.Faction?.Name ?? "")
                .Set("hostile", s.Faction?.HostileTo(Faction.OfPlayer) ?? false)
                .Set("traderKind", s.TraderKind?.label ?? ""));
        }
    }

    public class WorldQuestsCommand : CommandBase
    {
        public override string Name => "world.quests";
        public override string Description => "Active quests and their state.";

        public override JsonValue Execute(JsonValue args)
        {
            var quests = Find.QuestManager?.QuestsListForReading ?? new List<Quest>();
            bool activeOnly = args.OptBool("activeOnly", true);

            var array = JsonValue.NewArray();
            foreach (var quest in quests)
            {
                if (activeOnly && quest.State != QuestState.Ongoing && quest.State != QuestState.NotYetAccepted)
                    continue;

                array.Add(JsonValue.NewObject()
                    .Set("id", quest.id)
                    .Set("name", quest.name ?? "")
                    .Set("state", quest.State.ToString())
                    .Set("accepted", quest.State != QuestState.NotYetAccepted)
                    .Set("challengeRating", quest.challengeRating));
            }

            return JsonValue.NewObject().Set("count", array.Count).Set("quests", array);
        }
    }

    // ---- ideology (DLC-gated) --------------------------------------------------------------------

    public class IdeologyListCommand : CommandBase
    {
        public override string Name => "ideology.list";
        public override string Description => "Ideoligions in play, their memes, precepts and roles. Requires the Ideology DLC.";

        public override JsonValue Execute(JsonValue args)
        {
            if (!ModsConfig.IdeologyActive)
                throw new CommandException(ErrorCode.DlcNotActive,
                    "The Ideology expansion is not active.",
                    "This command needs Ideology enabled.");

            var ideos = Find.IdeoManager?.IdeosListForReading ?? new List<Ideo>();
            var playerIdeo = Faction.OfPlayer?.ideos?.PrimaryIdeo;

            var array = JsonValue.NewArray();
            foreach (var ideo in ideos)
            {
                var entry = JsonValue.NewObject()
                    .Set("name", ideo.name ?? "")
                    .Set("isPlayerPrimary", ideo == playerIdeo)
                    .Set("memes", Describe.ToArray(ideo.memes.Select(m => m.label ?? m.defName)));

                if (ideo == playerIdeo || args.OptBool("full"))
                {
                    entry.Set("precepts", Describe.ToArray(
                        ideo.PreceptsListForReading.Select(p => p.def?.label ?? p.def?.defName ?? "")
                            .Where(s => !string.IsNullOrEmpty(s))
                            .Take(40)));

                    entry.Set("roles", Describe.ToArray(
                        ideo.RolesListForReading.Select(r => r.Label ?? r.def?.label ?? "")));
                }

                array.Add(entry);
            }

            return JsonValue.NewObject()
                .Set("count", array.Count)
                .Set("ideos", array);
        }
    }
}
