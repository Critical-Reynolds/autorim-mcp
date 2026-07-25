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
    public class ResearchCurrentCommand : CommandBase
    {
        public override string Name => "research.current";
        public override string Description => "The active research project and how far along it is.";

        public override JsonValue Execute(JsonValue args)
        {
            var manager = Find.ResearchManager;
            var current = manager?.GetProject();

            if (current == null)
                return JsonValue.NewObject()
                    .Set("current", JsonValue.Null)
                    .Set("anyAvailable", manager?.AnyProjectIsAvailable ?? false)
                    .Set("note", "No project selected. research.list shows what can be started.");

            return JsonValue.NewObject()
                .Set("current", ResearchListCommand.DescribeProject(current, manager))
                .Set("benchesOnMap", CountBenches());
        }

        private static int CountBenches()
        {
            var map = Find.CurrentMap;
            if (map == null) return 0;
            return map.listerBuildings.allBuildingsColonist
                .Count(b => b.def?.building != null && b.def.thingClass != null &&
                            b.def.defName.IndexOf("ResearchBench", StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    public class ResearchListCommand : CommandBase
    {
        public override string Name => "research.list";
        public override string Description =>
            "Lists research projects. filter: available (default), finished, locked, all.";

        public override JsonValue Execute(JsonValue args)
        {
            var manager = Find.ResearchManager;
            string filter = args.OptString("filter", "available").ToLowerInvariant();
            string search = args.OptString("search");

            IEnumerable<ResearchProjectDef> projects = DefDatabase<ResearchProjectDef>.AllDefsListForReading;

            switch (filter)
            {
                case "available":
                    projects = projects.Where(p => !p.IsFinished && p.CanStartNow);
                    break;
                case "finished":
                    projects = projects.Where(p => p.IsFinished);
                    break;
                case "locked":
                    projects = projects.Where(p => !p.IsFinished && !p.CanStartNow);
                    break;
                case "all":
                    break;
                default:
                    throw CommandException.BadArgs($"Unknown filter '{filter}'.",
                        "Use available, finished, locked or all.");
            }

            if (!string.IsNullOrEmpty(search))
                projects = projects.Where(p =>
                    (p.label ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.defName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);

            var ordered = projects
                .OrderBy(p => p.techLevel)
                .ThenBy(p => p.baseCost)
                .ToList();

            args.ReadPaging(out int offset, out int limit, 40, 200);

            var page = Describe.Page(ordered, offset, limit, p => DescribeProject(p, manager));
            page.Set("filter", filter);
            return page;
        }

        internal static JsonValue DescribeProject(ResearchProjectDef project, ResearchManager manager)
        {
            var result = JsonValue.NewObject()
                .Set("defName", project.defName)
                .Set("label", project.label ?? "")
                .Set("techLevel", project.techLevel.ToString())
                .Set("cost", (int)project.baseCost);

            if (project.IsFinished)
            {
                result.Set("finished", true);
                return result;
            }

            result.Set("progressPercent", Describe.Percent(project.ProgressPercent));
            result.Set("canStartNow", project.CanStartNow);

            if (manager != null && manager.IsCurrentProject(project)) result.Set("isCurrent", true);

            if (!project.PrerequisitesCompleted && project.prerequisites != null)
            {
                var missing = project.prerequisites
                    .Where(p => !p.IsFinished)
                    .Select(p => p.label ?? p.defName)
                    .ToList();
                if (missing.Count > 0) result.Set("missingPrerequisites", Describe.ToArray(missing));
            }

            if (project.TechprintCount > 0)
            {
                result.Set("techprints", JsonValue.NewObject()
                    .Set("required", project.TechprintCount)
                    .Set("applied", manager?.GetTechprints(project) ?? 0));
            }

            return result;
        }
    }

    public class ResearchSetCommand : CommandBase
    {
        public override string Name => "research.set_current";
        public override string Description => "Starts (or switches to) a research project. Progress on the old one is kept.";

        public override JsonValue Execute(JsonValue args)
        {
            var project = DefResolver.Resolve<ResearchProjectDef>(args.RequireString("project"), "project");
            var manager = Find.ResearchManager;

            if (project.IsFinished)
                throw CommandException.Failed($"'{project.label}' is already researched.");

            if (!project.CanStartNow)
            {
                var missing = project.prerequisites?.Where(p => !p.IsFinished).Select(p => p.label).ToList()
                              ?? new List<string>();

                var error = CommandException.Failed(
                    $"'{project.label}' cannot be started yet.",
                    missing.Count > 0
                        ? "Finish the missing prerequisites first."
                        : "It may need techprints, or belong to inactive content.");
                error.Payload = JsonValue.NewObject()
                    .Set("missingPrerequisites", Describe.ToArray(missing))
                    .Set("techprintsRequired", project.TechprintCount)
                    .Set("techprintsApplied", manager?.GetTechprints(project) ?? 0);
                throw error;
            }

            var previous = manager.GetProject();
            manager.SetCurrentProject(project);

            return JsonValue.NewObject()
                .Set("previous", previous?.label ?? "none")
                .Set("current", ResearchListCommand.DescribeProject(project, manager))
                .Set("summary", $"Research set to '{project.label}'.");
        }
    }

    public class ResearchStopCommand : CommandBase
    {
        public override string Name => "research.stop";
        public override string Description => "Clears the active research project. Progress made so far is retained.";

        public override JsonValue Execute(JsonValue args)
        {
            var manager = Find.ResearchManager;
            var current = manager?.GetProject();

            if (current == null)
                return JsonValue.NewObject().Set("summary", "No research was active.");

            manager.StopProject(current);

            return JsonValue.NewObject()
                .Set("stopped", current.label)
                .Set("summary", $"Stopped researching '{current.label}'. Progress is kept.");
        }
    }

    /// <summary>
    /// Ranks what to research next. Weighs cost, how much it unlocks, and whether the colony
    /// still lacks the basics, so the answer is defensible rather than arbitrary.
    /// </summary>
    public class ResearchSuggestCommand : CommandBase
    {
        public override string Name => "research.suggest";
        public override string Description => "Suggests what to research next, ranked, with the reasoning shown.";

        public override JsonValue Execute(JsonValue args)
        {
            var manager = Find.ResearchManager;
            int limit = Math.Min(Math.Max(args.OptInt("limit", 5), 1), 20);

            var candidates = DefDatabase<ResearchProjectDef>.AllDefsListForReading
                .Where(p => !p.IsFinished && p.CanStartNow)
                .ToList();

            if (candidates.Count == 0)
                return JsonValue.NewObject()
                    .Set("suggestions", JsonValue.NewArray())
                    .Set("note", "Nothing can be started right now.");

            var unlockCounts = CountUnlocks();

            var scored = candidates
                .Select(project =>
                {
                    unlockCounts.TryGetValue(project, out int unlocks);
                    int cheapness = (int)Math.Max(0, 2000 - project.baseCost) / 100;
                    int progress = Describe.Percent(project.ProgressPercent) / 10;
                    int score = unlocks * 3 + cheapness + progress;

                    var reasons = new List<string>();
                    if (unlocks > 0) reasons.Add($"unlocks {unlocks} thing(s)");
                    if (project.ProgressPercent > 0.01f)
                        reasons.Add($"already {Describe.Percent(project.ProgressPercent)}% done");
                    if (project.baseCost <= 500) reasons.Add("cheap");

                    return new { project, score, unlocks, reasons };
                })
                .OrderByDescending(x => x.score)
                .Take(limit)
                .ToList();

            var array = JsonValue.NewArray();
            foreach (var entry in scored)
            {
                var item = ResearchListCommand.DescribeProject(entry.project, manager);
                item.Set("score", entry.score);
                item.Set("unlocks", entry.unlocks);
                item.Set("why", entry.reasons.Count > 0
                    ? string.Join(", ", entry.reasons.ToArray())
                    : "available now");
                array.Add(item);
            }

            return JsonValue.NewObject()
                .Set("current", manager?.GetProject()?.label ?? "none")
                .Set("suggestions", array)
                .Set("note", "Ranked by what each unlocks, how cheap it is, and existing progress.");
        }

        private static Dictionary<ResearchProjectDef, int> CountUnlocks()
        {
            var counts = new Dictionary<ResearchProjectDef, int>();

            foreach (var thing in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (thing.researchPrerequisites == null) continue;
                foreach (var project in thing.researchPrerequisites)
                {
                    counts.TryGetValue(project, out int existing);
                    counts[project] = existing + 1;
                }
            }

            foreach (var recipe in DefDatabase<RecipeDef>.AllDefsListForReading)
            {
                if (recipe.researchPrerequisite == null) continue;
                counts.TryGetValue(recipe.researchPrerequisite, out int existing);
                counts[recipe.researchPrerequisite] = existing + 1;
            }

            return counts;
        }
    }
}
