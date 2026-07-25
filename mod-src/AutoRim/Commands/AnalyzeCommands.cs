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
    /// Ranks colonists for a job.
    ///
    /// Returns the whole ranking with the reasoning attached rather than a single winner, so
    /// the choice can be explained and argued with. A bare "assign Ivy" is not something a
    /// player can sanity-check; "Ivy, cooking 8 with a burning passion, and she is only on two
    /// other jobs" is.
    /// </summary>
    public class AnalyzeBestPawnCommand : CommandBase
    {
        public override string Name => "analyze.best_pawn_for";
        public override string Description =>
            "Ranks colonists for a work type, scoring skill, passion, health and current workload, with reasons.";

        public override JsonValue Execute(JsonValue args)
        {
            var workType = DefResolver.Resolve<WorkTypeDef>(args.RequireString("work"), "work");
            int limit = Math.Min(Math.Max(args.OptInt("limit", 5), 1), 20);
            bool includeIneligible = args.OptBool("includeIneligible", false);

            var colonists = Refs.AddressablePawns()
                .Where(p => p.IsFreeColonist && !p.Dead)
                .ToList();

            if (colonists.Count == 0)
                throw CommandException.Failed("There are no colonists to assign.");

            var eligible = new List<JsonValue>();
            var ineligible = JsonValue.NewArray();

            foreach (var pawn in colonists)
            {
                if (pawn.WorkTypeIsDisabled(workType))
                {
                    if (includeIneligible)
                        ineligible.Add(JsonValue.NewObject()
                            .Set("pawn", Refs.Ref(pawn))
                            .Set("reason", "incapable of this work"));
                    continue;
                }

                eligible.Add(ScorePawn(pawn, workType));
            }

            if (eligible.Count == 0)
            {
                var error = CommandException.Failed(
                    $"No colonist can do {workType.labelShort ?? workType.label}.",
                    "A trait, backstory or injury disables it for everyone available.");
                error.Payload = JsonValue.NewObject().Set("ineligible", ineligible);
                throw error;
            }

            var ranked = eligible
                .OrderByDescending(entry => entry["score"].AsDouble())
                .Take(limit)
                .ToList();

            var array = JsonValue.NewArray();
            foreach (var entry in ranked) array.Add(entry);

            var result = JsonValue.NewObject()
                .Set("work", workType.defName)
                .Set("workLabel", workType.labelShort ?? workType.label)
                .Set("candidateCount", eligible.Count)
                .Set("ranked", array)
                .Set("recommendation", ranked[0]["pawn"]["label"].AsString(""))
                .Set("note", "Score combines relevant skill, passion, health and how busy the pawn already is.");

            if (includeIneligible && ineligible.Count > 0) result.Set("ineligible", ineligible);

            return result;
        }

        private static JsonValue ScorePawn(Pawn pawn, WorkTypeDef workType)
        {
            var reasons = new List<string>();
            double score = 0;

            float skill = pawn.skills?.AverageOfRelevantSkillsFor(workType) ?? 0f;
            score += skill * 4;
            reasons.Add($"{workType.labelShort ?? workType.label} skill {skill:0.#}");

            var passion = pawn.skills?.MaxPassionOfRelevantSkillsFor(workType) ?? Passion.None;
            if (passion == Passion.Major) { score += 12; reasons.Add("burning passion"); }
            else if (passion == Passion.Minor) { score += 6; reasons.Add("interested"); }

            // Health matters twice over: an injured pawn works slower and is at more risk.
            float manipulation = Capacity(pawn, PawnCapacityDefOf.Manipulation);
            float moving = Capacity(pawn, PawnCapacityDefOf.Moving);
            float consciousness = Capacity(pawn, PawnCapacityDefOf.Consciousness);
            double capacityFactor = (manipulation + moving + consciousness) / 3.0;
            score *= capacityFactor;

            if (capacityFactor < 0.85)
                reasons.Add($"reduced capacity ({Describe.Percent((float)capacityFactor)}%)");

            if (pawn.Downed) { score -= 100; reasons.Add("downed"); }
            if (pawn.InMentalState) { score -= 50; reasons.Add("in a mental break"); }

            // Spreading work out beats piling everything on the single best colonist. The work
            // being asked about is excluded, or a pawn already doing this job would be
            // penalised for doing it.
            int currentLoad = CountHighPriorityWork(pawn, workType);
            score -= currentLoad * 2;
            if (currentLoad > 0) reasons.Add($"already on {currentLoad} other high-priority job(s)");

            int existing = pawn.workSettings != null && pawn.workSettings.EverWork
                ? pawn.workSettings.GetPriority(workType)
                : 0;
            if (existing > 0) reasons.Add($"currently priority {existing}");

            var concerns = Describe.Concerns(pawn);
            if (concerns.Count > 0)
            {
                score -= concerns.Count * 5;
                reasons.AddRange(concerns);
            }

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("score", Math.Round(score, 1))
                .Set("skill", Describe.Round(skill))
                .Set("passion", passion.ToString())
                .Set("currentPriority", existing)
                .Set("why", string.Join(", ", reasons.ToArray()));
        }

        private static float Capacity(Pawn pawn, PawnCapacityDef def)
        {
            try
            {
                return pawn.health?.capacities?.GetLevel(def) ?? 1f;
            }
            catch (Exception)
            {
                return 1f;
            }
        }

        private static int CountHighPriorityWork(Pawn pawn, WorkTypeDef exclude)
        {
            if (pawn.workSettings == null || !pawn.workSettings.EverWork) return 0;

            int count = 0;
            foreach (var workType in DefDatabase<WorkTypeDef>.AllDefsListForReading)
            {
                if (workType == exclude) continue;
                if (pawn.WorkTypeIsDisabled(workType)) continue;
                int priority = pawn.workSettings.GetPriority(workType);
                if (priority == 1 || priority == 2) count++;
            }
            return count;
        }
    }

    public class AnalyzeIdleCommand : CommandBase
    {
        public override string Name => "analyze.idle_pawns";
        public override string Description => "Colonists with nothing to do, and why that is likely happening.";

        public override JsonValue Execute(JsonValue args)
        {
            var idle = new List<Pawn>();
            foreach (var pawn in Refs.AddressablePawns().Where(p => p.IsFreeColonist && !p.Dead))
                if (pawn.mindState?.IsIdle ?? false) idle.Add(pawn);

            var array = JsonValue.NewArray();
            foreach (var pawn in idle)
            {
                var reasons = new List<string>();

                if (pawn.workSettings == null || !pawn.workSettings.EverWork)
                    reasons.Add("cannot be assigned work at all");
                else
                {
                    int assigned = DefDatabase<WorkTypeDef>.AllDefsListForReading
                        .Count(w => !pawn.WorkTypeIsDisabled(w) && pawn.workSettings.GetPriority(w) > 0);
                    if (assigned == 0) reasons.Add("no work types enabled");
                    else reasons.Add($"{assigned} work types enabled, but nothing available to do");
                }

                if (pawn.playerSettings?.AreaRestrictionInPawnCurrentMap != null)
                    reasons.Add($"restricted to area '{pawn.playerSettings.AreaRestrictionInPawnCurrentMap.Label}'");

                if (pawn.Drafted) reasons.Add("drafted");

                array.Add(JsonValue.NewObject()
                    .Set("pawn", Refs.Ref(pawn))
                    .Set("why", string.Join("; ", reasons.ToArray())));
            }

            return JsonValue.NewObject()
                .Set("idleCount", array.Count)
                .Set("idle", array)
                .Set("note", array.Count == 0
                    ? "Everyone has something to do."
                    : "Idle colonists usually mean missing designations, no bills queued, or an area restriction.");
        }
    }

    /// <summary>
    /// Looks for the specific gaps that quietly stall a colony: work nobody is assigned to,
    /// skills nobody has, and production that cannot run.
    /// </summary>
    public class AnalyzeBottlenecksCommand : CommandBase
    {
        public override string Name => "analyze.bottlenecks";
        public override string Description =>
            "Finds unassigned work types, missing skills, suspended bills and other quiet stalls.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            var colonists = map.mapPawns.FreeColonists.Where(p => !p.Dead).ToList();

            var problems = JsonValue.NewArray();

            // Work types nobody is assigned to.
            foreach (var workType in DefDatabase<WorkTypeDef>.AllDefsListForReading)
            {
                bool anyoneAssigned = colonists.Any(p =>
                    p.workSettings != null && p.workSettings.EverWork &&
                    !p.WorkTypeIsDisabled(workType) &&
                    p.workSettings.GetPriority(workType) > 0);

                if (anyoneAssigned) continue;

                bool anyoneCapable = colonists.Any(p => !p.WorkTypeIsDisabled(workType));

                problems.Add(JsonValue.NewObject()
                    .Set("kind", "unassigned_work")
                    .Set("detail", $"Nobody is assigned to {workType.labelShort ?? workType.label}.")
                    .Set("fixable", anyoneCapable)
                    .Set("hint", anyoneCapable
                        ? $"analyze.best_pawn_for with work={workType.defName} suggests who should take it."
                        : "No colonist is capable of this work."));
            }

            // Suspended or unworkable bills.
            foreach (var bench in BillHelpers.BillGiversOn(map))
            {
                var giver = (IBillGiver)bench;
                if (giver.BillStack == null) continue;

                foreach (var bill in giver.BillStack.Bills.Where(b => b.suspended))
                    problems.Add(JsonValue.NewObject()
                        .Set("kind", "suspended_bill")
                        .Set("detail", $"'{bill.LabelCap}' on {bench.LabelShort} is suspended.")
                        .Set("hint", "bills.set with suspended=false resumes it."));

                if (!giver.CurrentlyUsableForBills() && giver.BillStack.Count > 0)
                    problems.Add(JsonValue.NewObject()
                        .Set("kind", "unusable_bench")
                        .Set("detail", $"{bench.LabelShort} has bills but is not usable right now.")
                        .Set("hint", "It may be unpowered, unreachable, or missing fuel."));
            }

            // Research idle while benches exist.
            if (Find.ResearchManager?.GetProject() == null && (Find.ResearchManager?.AnyProjectIsAvailable ?? false))
                problems.Add(JsonValue.NewObject()
                    .Set("kind", "no_research")
                    .Set("detail", "No research project is selected, but projects are available.")
                    .Set("hint", "research.suggest ranks what to take next."));

            // Food.
            var foodProblem = CheckFood(map);
            if (!foodProblem.IsNull) problems.Add(foodProblem);

            // Power.
            if (map.powerNetManager != null)
            {
                float gain = map.powerNetManager.AllNetsListForReading.Sum(n => n.CurrentEnergyGainRate());
                if (gain < 0)
                    problems.Add(JsonValue.NewObject()
                        .Set("kind", "power_deficit")
                        .Set("detail", $"Power networks are draining at {gain:0.#} W overall.")
                        .Set("hint", "Batteries will run down. Add generation or cut consumption."));
            }

            return JsonValue.NewObject()
                .Set("problemCount", problems.Count)
                .Set("problems", problems)
                .Set("note", problems.Count == 0 ? "No obvious bottlenecks." : "");
        }

        private static JsonValue CheckFood(Map map)
        {
            float nutrition = 0f;
            foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
            {
                if (thing.def?.ingestible == null || !thing.def.IsNutritionGivingIngestible) continue;
                if (thing.IsForbidden(Faction.OfPlayer)) continue;
                nutrition += thing.def.GetStatValueAbstract(StatDefOf.Nutrition) * thing.stackCount;
            }

            int colonists = Math.Max(1, map.mapPawns.FreeColonistsCount);
            float days = nutrition / (colonists * 1.6f);

            if (days >= 3f) return JsonValue.Null;

            return JsonValue.NewObject()
                .Set("kind", "food_low")
                .Set("detail", $"About {days:0.#} days of food for {colonists} colonists.")
                .Set("hint", "Hunt, harvest, or queue meals. designate.hunt and bills.add both help.");
        }
    }

    public class AnalyzeThreatsCommand : CommandBase
    {
        public override string Name => "analyze.threats";
        public override string Description => "Hostiles on the map weighed against who you have able to fight.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();

            var hostiles = map.mapPawns.AllPawnsSpawned
                .Where(p => p.HostileTo(Faction.OfPlayer) && !p.Dead && !p.Downed)
                .ToList();

            var defenders = map.mapPawns.FreeColonistsSpawned
                .Where(p => !p.Downed && !p.InMentalState)
                .ToList();

            var armed = defenders.Where(p => p.equipment?.Primary != null).ToList();

            var byFaction = JsonValue.NewArray();
            foreach (var group in hostiles.GroupBy(p => p.Faction?.Name ?? "unaffiliated"))
            {
                byFaction.Add(JsonValue.NewObject()
                    .Set("faction", group.Key)
                    .Set("count", group.Count())
                    .Set("kinds", Describe.ToArray(
                        group.GroupBy(p => p.kindDef?.label ?? "?")
                             .Select(k => $"{k.Count()}x {k.Key}"))));
            }

            var result = JsonValue.NewObject()
                .Set("dangerRating", (map.dangerWatcher?.DangerRating ?? StoryDanger.None).ToString())
                .Set("hostileCount", hostiles.Count)
                .Set("hostilesByFaction", byFaction)
                .Set("ableColonists", defenders.Count)
                .Set("armedColonists", armed.Count);

            if (hostiles.Count > 0)
            {
                var nearest = hostiles
                    .OrderBy(h => defenders.Count == 0 ? 0 : defenders.Min(d => h.Position.DistanceTo(d.Position)))
                    .Take(5);

                var closest = JsonValue.NewArray();
                foreach (var hostile in nearest)
                    closest.Add(JsonValue.NewObject()
                        .Set("pawn", Refs.Ref(hostile))
                        .Set("kind", hostile.kindDef?.label ?? "")
                        .Set("pos", Refs.Cell(hostile.Position)));
                result.Set("closest", closest);

                result.Set("assessment", armed.Count == 0
                    ? "No armed colonists. Avoid engaging; consider retreating indoors and closing doors."
                    : armed.Count >= hostiles.Count
                        ? $"{armed.Count} armed against {hostiles.Count} hostile(s) — a defensible fight from cover."
                        : $"Outnumbered: {armed.Count} armed against {hostiles.Count} hostile(s). Fight from cover or fall back.");
            }
            else
            {
                result.Set("assessment", "No active hostiles on the map.");
            }

            return result;
        }
    }
}
