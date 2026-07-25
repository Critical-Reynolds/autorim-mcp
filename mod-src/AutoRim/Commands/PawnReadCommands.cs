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
    public class PawnsListCommand : CommandBase
    {
        public override string Name => "pawns.list";
        public override string Description =>
            "Lists pawns one line each. filter: colonists (default), animals, prisoners, slaves, hostiles, all.";

        public override JsonValue Execute(JsonValue args)
        {
            string filter = args.OptString("filter", "colonists").ToLowerInvariant();
            var pawns = Select(filter).ToList();

            args.ReadPaging(out int offset, out int limit, 50, 200);

            var page = Describe.Page(pawns, offset, limit, Describe.PawnSummary);
            page.Set("filter", filter);
            return page;
        }

        private static IEnumerable<Pawn> Select(string filter)
        {
            var all = Refs.AddressablePawns().Where(p => !p.Dead);

            switch (filter)
            {
                case "colonists":
                    return all.Where(p => p.IsFreeColonist);
                case "animals":
                    return all.Where(p => p.RaceProps != null && p.RaceProps.Animal && p.Faction == Faction.OfPlayer);
                case "prisoners":
                    return all.Where(p => p.IsPrisoner);
                case "slaves":
                    return all.Where(p => p.IsSlave);
                case "hostiles":
                    return all.Where(p => p.HostileTo(Faction.OfPlayer));
                case "wild":
                    return all.Where(p => p.RaceProps != null && p.RaceProps.Animal && p.Faction == null);
                case "all":
                    return all;
                default:
                    throw CommandException.BadArgs($"Unknown filter '{filter}'.",
                        "Use colonists, animals, prisoners, slaves, hostiles, wild or all.");
            }
        }
    }

    public class PawnDetailCommand : CommandBase
    {
        public override string Name => "pawns.detail";
        public override string Description =>
            "Everything about one pawn: skills, traits, health, needs, gear, work priorities, schedule.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);

            var result = Describe.PawnSummary(pawn);
            result.Set("fullName", pawn.Name?.ToStringFull ?? pawn.LabelShort);
            result.Set("gender", pawn.gender.ToString().ToLowerInvariant());
            result.Set("kindDef", pawn.kindDef?.defName ?? "");

            if (pawn.Spawned)
            {
                result.Set("position", Refs.Cell(pawn.Position));
                result.Set("map", Find.Maps.IndexOf(pawn.Map));
            }

            if (pawn.story != null)
            {
                result.Set("childhood", pawn.story.Childhood?.title ?? "");
                result.Set("adulthood", pawn.story.Adulthood?.title ?? "");
            }

            result.Set("traits", Describe.Traits(pawn));
            result.Set("skills", Describe.Skills(pawn));
            result.Set("needs", Describe.Needs(pawn));
            result.Set("health", Health(pawn));
            result.Set("gear", Describe.Gear(pawn));

            var work = WorkPriorities(pawn);
            if (!work.IsNull) result.Set("work", work);

            var disabled = DisabledWork(pawn);
            if (disabled.Count > 0) result.Set("disabledWork", Describe.ToArray(disabled));

            if (pawn.playerSettings != null)
            {
                var settings = JsonValue.NewObject()
                    .Set("medicalCare", pawn.playerSettings.medCare.ToString());

                var area = pawn.playerSettings.AreaRestrictionInPawnCurrentMap;
                settings.Set("allowedArea", area?.Label ?? "unrestricted");

                var master = pawn.playerSettings.Master;
                if (master != null) settings.Set("master", Refs.Ref(master));

                result.Set("playerSettings", settings);
            }

            if (pawn.timetable?.times != null)
                result.Set("schedule", Describe.ToArray(pawn.timetable.times.Select(t => t?.defName ?? "?")));

            if (ModsConfig.IdeologyActive && pawn.Ideo != null)
                result.Set("ideo", pawn.Ideo.name);

            if (pawn.IsPrisoner && pawn.guest != null)
                result.Set("prisoner", JsonValue.NewObject()
                    .Set("interactionMode", pawn.guest.ExclusiveInteractionMode?.defName ?? "")
                    .Set("resistance", Describe.Round(pawn.guest.Resistance))
                    .Set("will", Describe.Round(pawn.guest.will))
                    .Set("recruitable", pawn.guest.Recruitable));

            if (pawn.training != null && pawn.RaceProps != null && pawn.RaceProps.Animal)
                result.Set("training", Training(pawn));

            return result;
        }

        private static JsonValue Health(Pawn pawn)
        {
            var result = JsonValue.NewObject()
                .Set("percent", pawn.health?.summaryHealth != null
                    ? Describe.Percent(pawn.health.summaryHealth.SummaryHealthPercent)
                    : 0)
                .Set("state", pawn.health?.State.ToString() ?? "unknown")
                .Set("bleedRate", Describe.Round(pawn.health?.hediffSet?.BleedRateTotal ?? 0f, 2))
                .Set("pain", Describe.Round(pawn.health?.hediffSet?.PainTotal ?? 0f, 2))
                .Set("hediffs", Describe.Hediffs(pawn));

            if (pawn.health?.capacities != null)
            {
                var capacities = JsonValue.NewObject();
                foreach (var def in DefDatabase<PawnCapacityDef>.AllDefsListForReading)
                {
                    if (!def.showOnHumanlikes && pawn.RaceProps != null && pawn.RaceProps.Humanlike) continue;
                    try
                    {
                        capacities.Set(def.defName, Describe.Percent(pawn.health.capacities.GetLevel(def)));
                    }
                    catch (Exception)
                    {
                        // Capacity evaluation can throw for exotic body types; skip that one.
                    }
                }
                result.Set("capacities", capacities);
            }

            return result;
        }

        private static JsonValue WorkPriorities(Pawn pawn)
        {
            if (pawn.workSettings == null || !pawn.workSettings.EverWork) return JsonValue.Null;

            var result = JsonValue.NewObject();
            foreach (var workType in DefDatabase<WorkTypeDef>.AllDefsListForReading)
            {
                if (pawn.WorkTypeIsDisabled(workType)) continue;
                result.Set(workType.defName, pawn.workSettings.GetPriority(workType));
            }
            return result;
        }

        private static List<string> DisabledWork(Pawn pawn)
        {
            var result = new List<string>();
            try
            {
                foreach (var workType in pawn.GetDisabledWorkTypes(true))
                    result.Add(workType.defName);
            }
            catch (Exception)
            {
            }
            return result;
        }

        private static JsonValue Training(Pawn pawn)
        {
            var result = JsonValue.NewObject();
            foreach (var trainable in DefDatabase<TrainableDef>.AllDefsListForReading)
            {
                try
                {
                    if (!pawn.training.CanAssignToTrain(trainable).Accepted) continue;
                    result.Set(trainable.defName, JsonValue.NewObject()
                        .Set("wanted", pawn.training.GetWanted(trainable))
                        .Set("learned", pawn.training.HasLearned(trainable)));
                }
                catch (Exception)
                {
                }
            }
            return result;
        }
    }
}
