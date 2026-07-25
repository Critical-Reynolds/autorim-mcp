using System;
using System.Collections.Generic;
using System.Linq;
using AutoRim.Bridge;
using AutoRim.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoRim.Read
{
    /// <summary>
    /// Shared serializers.
    ///
    /// Compactness is a feature, not an optimisation. A full colony dump runs to tens of
    /// thousands of tokens and crowds out the actual conversation, so summaries stay terse
    /// and detail is only produced when a caller asks for one pawn by id.
    /// </summary>
    public static class Describe
    {
        public static int Percent(float zeroToOne) => (int)Math.Round(Mathf01(zeroToOne) * 100f);

        private static float Mathf01(float value) => value < 0f ? 0f : (value > 1f ? 1f : value);

        public static double Round(float value, int digits = 1) => Math.Round(value, digits);

        // ---- pawns --------------------------------------------------------------------------

        /// <summary>One line per pawn: enough to decide who to look at, nothing more.</summary>
        public static JsonValue PawnSummary(Pawn pawn)
        {
            var result = JsonValue.NewObject()
                .Set("id", pawn.thingIDNumber)
                .Set("name", pawn.LabelShort ?? pawn.Name?.ToStringShort ?? "?")
                .Set("kind", Refs.DescribeKind(pawn));

            if (pawn.RaceProps != null && !pawn.RaceProps.Animal)
                result.Set("age", pawn.ageTracker?.AgeBiologicalYears ?? 0);

            if (pawn.needs?.mood != null) result.Set("mood", Percent(pawn.needs.mood.CurLevelPercentage));
            if (pawn.health?.summaryHealth != null)
                result.Set("health", Percent(pawn.health.summaryHealth.SummaryHealthPercent));

            string job = CurrentJob(pawn);
            if (!string.IsNullOrEmpty(job)) result.Set("job", job);

            if (pawn.Drafted) result.Set("drafted", true);
            if (pawn.Downed) result.Set("downed", true);
            if (pawn.Dead) result.Set("dead", true);

            if (pawn.InMentalState)
                result.Set("mentalState", pawn.MentalState?.def?.label ?? "unknown");

            var flags = Concerns(pawn);
            if (flags.Count > 0) result.Set("concerns", ToArray(flags));

            return result;
        }

        /// <summary>
        /// The things a player would actually want flagged. Kept short on purpose: a summary
        /// that flags everything flags nothing.
        /// </summary>
        public static List<string> Concerns(Pawn pawn)
        {
            var concerns = new List<string>();
            if (pawn.Dead) return concerns;

            if (pawn.health?.hediffSet != null)
            {
                if (pawn.health.hediffSet.BleedRateTotal > 0.1f) concerns.Add("bleeding");
                if (pawn.health.hediffSet.PainTotal > 0.4f) concerns.Add("in pain");

                bool needsTending = pawn.health.hediffSet.hediffs.Any(h => h.TendableNow());
                if (needsTending) concerns.Add("needs tending");

                bool lifeThreatening = pawn.health.hediffSet.hediffs.Any(h =>
                    h.def != null && h.def.lethalSeverity > 0f && h.Severity > h.def.lethalSeverity * 0.6f);
                if (lifeThreatening) concerns.Add("life-threatening condition");
            }

            if (pawn.needs?.food != null && pawn.needs.food.CurLevelPercentage < 0.15f) concerns.Add("starving");
            if (pawn.needs?.rest != null && pawn.needs.rest.CurLevelPercentage < 0.15f) concerns.Add("exhausted");
            if (pawn.needs?.mood != null && pawn.needs.mood.CurLevelPercentage < 0.25f) concerns.Add("mood critical");

            return concerns;
        }

        public static string CurrentJob(Pawn pawn)
        {
            if (pawn.jobs == null) return null;
            try
            {
                if (pawn.jobs.curDriver != null)
                {
                    string report = pawn.jobs.curDriver.GetReport();
                    if (!string.IsNullOrEmpty(report)) return report.TrimEnd('.');
                }
                return pawn.jobs.curJob?.def?.reportString;
            }
            catch (Exception)
            {
                // A few job drivers throw while building their report if state is half-torn-down.
                return pawn.jobs.curJob?.def?.defName;
            }
        }

        public static JsonValue Skills(Pawn pawn)
        {
            var array = JsonValue.NewArray();
            if (pawn.skills?.skills == null) return array;

            foreach (var skill in pawn.skills.skills.OrderByDescending(s => s.Level))
            {
                var entry = JsonValue.NewObject()
                    .Set("skill", skill.def.label)
                    .Set("level", skill.Level);

                if (skill.TotallyDisabled) entry.Set("disabled", true);
                if (skill.passion != Passion.None)
                    entry.Set("passion", skill.passion == Passion.Major ? "burning" : "interested");

                array.Add(entry);
            }
            return array;
        }

        public static JsonValue Traits(Pawn pawn)
        {
            var array = JsonValue.NewArray();
            if (pawn.story?.traits?.allTraits == null) return array;
            foreach (var trait in pawn.story.traits.allTraits)
                array.Add(trait.LabelCap);
            return array;
        }

        public static JsonValue Needs(Pawn pawn)
        {
            var result = JsonValue.NewObject();
            if (pawn.needs?.AllNeeds == null) return result;
            foreach (var need in pawn.needs.AllNeeds)
            {
                if (need?.def == null) continue;
                result.Set(need.def.defName, Percent(need.CurLevelPercentage));
            }
            return result;
        }

        public static JsonValue Hediffs(Pawn pawn)
        {
            var array = JsonValue.NewArray();
            if (pawn.health?.hediffSet?.hediffs == null) return array;

            foreach (var hediff in pawn.health.hediffSet.hediffs)
            {
                var entry = JsonValue.NewObject()
                    .Set("label", hediff.LabelCap)
                    .Set("defName", hediff.def?.defName ?? "");

                if (hediff.Part != null) entry.Set("part", hediff.Part.Label);
                if (hediff.Severity > 0f) entry.Set("severity", Round(hediff.Severity, 2));
                if (hediff.TendableNow()) entry.Set("tendable", true);

                array.Add(entry);
            }
            return array;
        }

        public static JsonValue Gear(Pawn pawn)
        {
            var result = JsonValue.NewObject();

            var primary = pawn.equipment?.Primary;
            if (primary != null) result.Set("weapon", primary.LabelCap);

            var worn = pawn.apparel?.WornApparel;
            if (worn != null && worn.Count > 0)
            {
                var apparel = JsonValue.NewArray();
                foreach (var item in worn) apparel.Add(item.LabelCap);
                result.Set("apparel", apparel);
            }

            return result;
        }

        // ---- things -------------------------------------------------------------------------

        public static JsonValue ThingSummary(Thing thing)
        {
            var result = JsonValue.NewObject()
                .Set("id", thing.thingIDNumber)
                .Set("label", thing.LabelShort ?? thing.Label)
                .Set("defName", thing.def?.defName ?? "")
                .Set("pos", Refs.Cell(thing.Position));

            if (thing.stackCount > 1) result.Set("count", thing.stackCount);
            if (thing.Stuff != null) result.Set("stuff", thing.Stuff.label);
            if (thing.IsForbidden(Faction.OfPlayer)) result.Set("forbidden", true);

            return result;
        }

        // ---- helpers ------------------------------------------------------------------------

        public static JsonValue ToArray(IEnumerable<string> values)
        {
            var array = JsonValue.NewArray();
            foreach (var value in values) array.Add(value);
            return array;
        }

        /// <summary>
        /// Applies paging and reports the untruncated total, so a caller always knows when
        /// they are looking at part of a list.
        /// </summary>
        public static JsonValue Page<T>(IReadOnlyList<T> source, int offset, int limit, Func<T, JsonValue> project)
        {
            var items = JsonValue.NewArray();
            int end = Math.Min(source.Count, offset + limit);
            for (int i = offset; i < end; i++) items.Add(project(source[i]));

            var result = JsonValue.NewObject()
                .Set("items", items)
                .Set("totalCount", source.Count);

            if (end < source.Count || offset > 0)
            {
                result.Set("offset", offset);
                result.Set("returned", items.Count);
                result.Set("truncated", end < source.Count);
            }

            return result;
        }
    }
}
