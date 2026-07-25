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
    /// The default read. Everything a player glances at before deciding what to do, in about
    /// as many words as fit on one screen.
    /// </summary>
    public class ColonySnapshotCommand : CommandBase
    {
        public override string Name => "colony.snapshot";
        public override string Description => "Compact overview: date, weather, threat, colonists, key resources, research, power.";

        /// <summary>Resources worth surfacing unprompted; everything else is available via colony.resources.</summary>
        private static readonly string[] KeyResources =
        {
            "Silver", "Steel", "WoodLog", "ComponentIndustrial", "ComponentSpacer",
            "MedicineHerbal", "MedicineIndustrial", "MedicineUltratech",
            "Chemfuel", "Uranium", "Plasteel", "Gold"
        };

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.ResolveMap(args.Has("map") ? args.OptInt("map", 0) : (int?)null);

            var result = JsonValue.NewObject()
                .Set("map", Find.Maps.IndexOf(map))
                .Set("mapCount", Find.Maps.Count)
                .Set("time", Time(map))
                .Set("weather", Weather(map))
                .Set("threat", Threat(map))
                .Set("wealth", Wealth(map))
                .Set("colonists", Colonists(map))
                .Set("food", Food(map))
                .Set("resources", Resources(map))
                .Set("research", Research())
                .Set("power", Power(map));

            var alerts = ActiveAlertLabels();
            if (alerts.Count > 0) result.Set("alerts", Describe.ToArray(alerts));

            var conditions = map.gameConditionManager?.ActiveConditions;
            if (conditions != null && conditions.Count > 0)
                result.Set("conditions", Describe.ToArray(conditions.Select(c => c.LabelCap.ToString())));

            return result;
        }

        private static JsonValue Time(Map map)
        {
            var ticks = Find.TickManager;
            return JsonValue.NewObject()
                .Set("year", GenLocalDate.Year(map))
                .Set("season", GenLocalDate.Season(map).ToString())
                .Set("dayOfSeason", GenLocalDate.DayOfSeason(map) + 1)
                .Set("hour", GenLocalDate.HourOfDay(map))
                .Set("paused", ticks.Paused)
                .Set("speed", ticks.CurTimeSpeed.ToString().ToLowerInvariant());
        }

        private static JsonValue Weather(Map map)
        {
            return JsonValue.NewObject()
                .Set("current", map.weatherManager?.curWeather?.label ?? "unknown")
                .Set("outdoorTempC", Describe.Round(map.mapTemperature?.OutdoorTemp ?? 0f));
        }

        private static JsonValue Threat(Map map)
        {
            var danger = map.dangerWatcher?.DangerRating ?? StoryDanger.None;

            var hostiles = map.mapPawns.AllPawnsSpawned
                .Where(p => p.HostileTo(Faction.OfPlayer) && !p.Dead && !p.Downed)
                .ToList();

            var result = JsonValue.NewObject()
                .Set("rating", danger.ToString().ToLowerInvariant())
                .Set("hostileCount", hostiles.Count);

            if (hostiles.Count > 0)
            {
                var factions = hostiles
                    .Select(p => p.Faction?.Name)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct()
                    .ToList();
                if (factions.Count > 0) result.Set("hostileFactions", Describe.ToArray(factions));
            }

            return result;
        }

        private static JsonValue Wealth(Map map)
        {
            var watcher = map.wealthWatcher;
            if (watcher == null) return JsonValue.Null;
            return JsonValue.NewObject()
                .Set("total", (int)watcher.WealthTotal)
                .Set("items", (int)watcher.WealthItems)
                .Set("buildings", (int)watcher.WealthBuildings)
                .Set("pawns", (int)watcher.WealthPawns);
        }

        private static JsonValue Colonists(Map map)
        {
            var pawns = map.mapPawns.FreeColonists
                .Concat(map.mapPawns.SlavesAndPrisonersOfColonySpawned)
                .Distinct()
                .ToList();

            var array = JsonValue.NewArray();
            foreach (var pawn in pawns) array.Add(Describe.PawnSummary(pawn));

            return JsonValue.NewObject()
                .Set("count", map.mapPawns.FreeColonistsCount)
                .Set("prisoners", map.mapPawns.PrisonersOfColonyCount)
                .Set("animals", map.mapPawns.SpawnedColonyAnimals?.Count ?? 0)
                .Set("list", array);
        }

        /// <summary>
        /// Nutrition on hand, which is what actually answers "are we going to starve" — a raw
        /// meal count says nothing about the crop in the ground or the meat in the freezer.
        /// </summary>
        private static JsonValue Food(Map map)
        {
            float nutrition = 0f;
            int meals = 0;

            foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
            {
                if (thing.def?.ingestible == null) continue;
                if (!thing.def.IsNutritionGivingIngestible) continue;
                if (thing.IsForbidden(Faction.OfPlayer)) continue;

                nutrition += thing.def.GetStatValueAbstract(StatDefOf.Nutrition) * thing.stackCount;
                if (thing.def.IsIngestible && thing.def.ingestible.preferability >= FoodPreferability.MealAwful)
                    meals += thing.stackCount;
            }

            int colonists = Math.Max(1, map.mapPawns.FreeColonistsCount);

            return JsonValue.NewObject()
                .Set("totalNutrition", Describe.Round(nutrition))
                .Set("meals", meals)
                // A colonist eats roughly 1.6 nutrition per day.
                .Set("daysOfFood", Describe.Round(nutrition / (colonists * 1.6f)));
        }

        private static JsonValue Resources(Map map)
        {
            var result = JsonValue.NewObject();
            var counter = map.resourceCounter;
            if (counter == null) return result;

            foreach (string defName in KeyResources)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (def == null) continue;
                int count = counter.GetCount(def);
                if (count > 0) result.Set(def.label, count);
            }

            return result;
        }

        private static JsonValue Research()
        {
            var manager = Find.ResearchManager;
            var current = manager?.GetProject();

            if (current == null)
                return JsonValue.NewObject()
                    .Set("current", JsonValue.Null)
                    .Set("note", "No research project selected.");

            return JsonValue.NewObject()
                .Set("current", current.label)
                .Set("defName", current.defName)
                .Set("progressPercent", Describe.Percent(manager.GetProgress(current) / Math.Max(1f, current.baseCost)));
        }

        private static JsonValue Power(Map map)
        {
            var manager = map.powerNetManager;
            if (manager == null) return JsonValue.Null;

            float gain = 0f, stored = 0f;
            foreach (var net in manager.AllNetsListForReading)
            {
                gain += net.CurrentEnergyGainRate();
                stored += net.CurrentStoredEnergy();
            }

            return JsonValue.NewObject()
                .Set("netGainRate", Describe.Round(gain))
                .Set("stored", Describe.Round(stored))
                .Set("nets", manager.AllNetsListForReading.Count);
        }

        internal static List<string> ActiveAlertLabels()
        {
            var labels = new List<string>();
            foreach (var alert in AlertsReader.Active())
                labels.Add(AlertsReader.SafeLabel(alert));
            return labels;
        }
    }

    public class ColonyAlertsCommand : CommandBase
    {
        public override string Name => "colony.alerts";
        public override string Description => "Active alerts with explanations - the things the game wants you to deal with.";

        public override JsonValue Execute(JsonValue args)
        {
            var array = JsonValue.NewArray();

            foreach (var alert in AlertsReader.Active())
            {
                array.Add(JsonValue.NewObject()
                    .Set("label", AlertsReader.SafeLabel(alert))
                    .Set("priority", alert.Priority.ToString().ToLowerInvariant())
                    .Set("explanation", AlertsReader.SafeExplanation(alert)));
            }

            return JsonValue.NewObject().Set("count", array.Count).Set("alerts", array);
        }
    }

    public class ColonyLettersCommand : CommandBase
    {
        public override string Name => "colony.letters";
        public override string Description => "Unread letters in the corner of the screen - events awaiting a decision.";

        public override JsonValue Execute(JsonValue args)
        {
            var letters = Find.LetterStack?.LettersListForReading ?? new List<Letter>();
            var array = JsonValue.NewArray();

            foreach (var letter in letters)
            {
                array.Add(JsonValue.NewObject()
                    .Set("id", letter.ID)
                    .Set("label", letter.Label.ToString())
                    .Set("type", letter.def?.defName ?? ""));
            }

            return JsonValue.NewObject().Set("count", array.Count).Set("letters", array);
        }
    }

    public class ColonyResourcesCommand : CommandBase
    {
        public override string Name => "colony.resources";
        public override string Description => "Everything the colony has stockpiled, optionally filtered by name.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.ResolveMap(args.Has("map") ? args.OptInt("map", 0) : (int?)null);
            string filter = args.OptString("filter");

            var counter = map.resourceCounter;
            if (counter == null) throw CommandException.Failed("This map has no resource counter.");

            var entries = counter.AllCountedAmounts
                .Where(kv => kv.Value > 0 && kv.Key != null)
                .Where(kv => string.IsNullOrEmpty(filter) ||
                             kv.Key.label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             kv.Key.defName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(kv => kv.Value)
                .ToList();

            args.ReadPaging(out int offset, out int limit, 60, 300);

            return Describe.Page(entries, offset, limit, kv => JsonValue.NewObject()
                .Set("defName", kv.Key.defName)
                .Set("label", kv.Key.label)
                .Set("count", kv.Value));
        }
    }

    public class ColonyPowerCommand : CommandBase
    {
        public override string Name => "colony.power";
        public override string Description => "Per-network power generation, consumption and stored charge.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.ResolveMap(args.Has("map") ? args.OptInt("map", 0) : (int?)null);
            var manager = map.powerNetManager;
            if (manager == null) throw CommandException.Failed("This map has no power networks.");

            var nets = JsonValue.NewArray();
            int index = 0;
            foreach (var net in manager.AllNetsListForReading)
            {
                nets.Add(JsonValue.NewObject()
                    .Set("index", index++)
                    .Set("gainRate", Describe.Round(net.CurrentEnergyGainRate()))
                    .Set("stored", Describe.Round(net.CurrentStoredEnergy()))
                    .Set("generators", net.powerComps?.Count ?? 0)
                    .Set("batteries", net.batteryComps?.Count ?? 0));
            }

            return JsonValue.NewObject().Set("netCount", nets.Count).Set("nets", nets);
        }
    }
}
