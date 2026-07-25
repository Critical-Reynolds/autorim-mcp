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
    public class WorkListTypesCommand : CommandBase
    {
        public override string Name => "work.list_types";
        public override string Description => "All work types in priority order, with the skills each one uses.";
        public override bool RequiresGame => false;

        public override JsonValue Execute(JsonValue args)
        {
            var array = JsonValue.NewArray();
            foreach (var workType in DefDatabase<WorkTypeDef>.AllDefsListForReading
                         .OrderByDescending(w => w.naturalPriority))
            {
                var entry = JsonValue.NewObject()
                    .Set("defName", workType.defName)
                    .Set("label", workType.labelShort ?? workType.label ?? "");

                if (workType.relevantSkills != null && workType.relevantSkills.Count > 0)
                    entry.Set("skills", Describe.ToArray(workType.relevantSkills.Select(s => s.label)));

                array.Add(entry);
            }

            return JsonValue.NewObject()
                .Set("manualPrioritiesEnabled", Current.Game?.playSettings?.useWorkPriorities ?? false)
                .Set("workTypes", array);
        }
    }

    public class WorkGetPrioritiesCommand : CommandBase
    {
        public override string Name => "work.get_priorities";
        public override string Description => "Work priorities for one pawn, or for every colonist when no pawn is given.";

        public override JsonValue Execute(JsonValue args)
        {
            bool manual = Current.Game?.playSettings?.useWorkPriorities ?? false;

            if (args.Has("pawn"))
            {
                var pawn = Refs.ResolvePawn(args);
                return JsonValue.NewObject()
                    .Set("manualPrioritiesEnabled", manual)
                    .Set("pawn", Refs.Ref(pawn))
                    .Set("priorities", PrioritiesOf(pawn));
            }

            var array = JsonValue.NewArray();
            foreach (var pawn in Refs.AddressablePawns().Where(p => p.IsFreeColonist && !p.Dead))
            {
                array.Add(JsonValue.NewObject()
                    .Set("pawn", Refs.Ref(pawn))
                    .Set("priorities", PrioritiesOf(pawn)));
            }

            return JsonValue.NewObject()
                .Set("manualPrioritiesEnabled", manual)
                .Set("note", "0 means the pawn will not do that work. 1 is highest, 4 is lowest.")
                .Set("colonists", array);
        }

        internal static JsonValue PrioritiesOf(Pawn pawn)
        {
            var result = JsonValue.NewObject();
            if (pawn.workSettings == null || !pawn.workSettings.EverWork) return result;

            foreach (var workType in DefDatabase<WorkTypeDef>.AllDefsListForReading)
            {
                if (pawn.WorkTypeIsDisabled(workType)) continue;
                result.Set(workType.defName, pawn.workSettings.GetPriority(workType));
            }
            return result;
        }
    }

    public class WorkSetPriorityCommand : CommandBase
    {
        public override string Name => "work.set_priority";
        public override string Description =>
            "Sets one work priority. 0 disables the work, 1 is highest, 4 is lowest.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            var workType = DefResolver.Resolve<WorkTypeDef>(args.RequireString("work"), "work");
            int priority = args.RequireIntInRange("priority", 0, 4);

            int previous = Apply(pawn, workType, priority, out int applied);

            var result = JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("work", workType.defName)
                .Set("from", previous)
                .Set("requested", priority)
                .Set("applied", applied);

            if (applied != priority)
            {
                // Never report a change that did not happen. With manual priorities off the
                // game stores every enabled work type as 3, so a request for 1 silently
                // becomes 3 - and a caller told "priority is now 1" would act on a fiction.
                result.Set("note",
                    "Manual work priorities are turned off, so RimWorld stores every enabled work type as 3. "
                    + "Turn on manual priorities in the Work tab if you want finer ordering.");
                result.Set("summary",
                    $"{pawn.LabelShort}: {workType.labelShort ?? workType.label} priority {previous} -> {applied} (requested {priority}).");
            }
            else
            {
                result.Set("summary",
                    $"{pawn.LabelShort}: {workType.labelShort ?? workType.label} priority {previous} -> {applied}");
            }

            return result;
        }

        /// <summary>
        /// Applies one priority change. Returns the previous value and reports, through
        /// <paramref name="applied"/>, what the game actually stored.
        ///
        /// The read-back matters: with manual priorities off RimWorld collapses every enabled
        /// work type to 3, so the requested and stored values differ. Reporting the request as
        /// though it were the outcome would have callers acting on a value the game never held.
        ///
        /// Work the pawn cannot do is rejected rather than silently ignored, which is the
        /// failure that leaves a player wondering why nobody is cooking.
        /// </summary>
        internal static int Apply(Pawn pawn, WorkTypeDef workType, int priority, out int applied)
        {
            if (pawn.workSettings == null || !pawn.workSettings.EverWork)
                throw CommandException.Failed($"{pawn.LabelShort} cannot be assigned work.");

            if (pawn.WorkTypeIsDisabled(workType))
                throw CommandException.Failed(
                    $"{pawn.LabelShort} cannot do {workType.labelShort ?? workType.label}.",
                    "A trait, backstory or missing capacity disables it. pawns.detail lists disabledWork.");

            int previous = pawn.workSettings.GetPriority(workType);
            pawn.workSettings.SetPriority(workType, priority);
            applied = pawn.workSettings.GetPriority(workType);
            return previous;
        }
    }

    /// <summary>
    /// Batch assignment. Setting a whole work plan one call at a time is slow and leaves the
    /// colony in a half-configured state if something fails partway.
    /// </summary>
    public class WorkSetBulkCommand : CommandBase
    {
        public override string Name => "work.set_bulk";
        public override string Description =>
            "Applies many priority changes at once. assignments: [{pawn, work, priority}, ...].";

        public override JsonValue Execute(JsonValue args)
        {
            var assignments = args["assignments"];
            if (assignments.Type != JsonType.Array || assignments.Count == 0)
                throw CommandException.BadArgs("'assignments' must be a non-empty array of {pawn, work, priority}.");

            var applied = JsonValue.NewArray();
            var failed = JsonValue.NewArray();

            foreach (var assignment in assignments.Items)
            {
                string who = assignment["pawn"].AsString("?");
                string what = assignment["work"].AsString("?");
                try
                {
                    var pawn = Refs.ResolvePawn(assignment);
                    var workType = DefResolver.Resolve<WorkTypeDef>(assignment.RequireString("work"), "work");
                    int priority = assignment.RequireIntInRange("priority", 0, 4);

                    int previous = WorkSetPriorityCommand.Apply(pawn, workType, priority, out int stored);

                    var entry = JsonValue.NewObject()
                        .Set("pawn", Refs.Ref(pawn))
                        .Set("work", workType.defName)
                        .Set("from", previous)
                        .Set("requested", priority)
                        .Set("applied", stored);

                    if (stored != priority) entry.Set("clamped", true);
                    applied.Add(entry);
                }
                catch (CommandException ex)
                {
                    // One bad row must not discard the rest; report it and continue.
                    failed.Add(JsonValue.NewObject()
                        .Set("pawn", who)
                        .Set("work", what)
                        .Set("code", ex.Code)
                        .Set("reason", ex.Message));
                }
            }

            return JsonValue.NewObject()
                .Set("appliedCount", applied.Count)
                .Set("failedCount", failed.Count)
                .Set("applied", applied)
                .Set("failed", failed)
                .Set("summary", $"Applied {applied.Count} work changes, {failed.Count} rejected.");
        }
    }

    public class WorkClearCommand : CommandBase
    {
        public override string Name => "work.clear";
        public override string Description => "Sets every work type for a pawn to 0, so they will do nothing until reassigned.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            if (pawn.workSettings == null || !pawn.workSettings.EverWork)
                throw CommandException.Failed($"{pawn.LabelShort} cannot be assigned work.");

            int cleared = 0;
            foreach (var workType in DefDatabase<WorkTypeDef>.AllDefsListForReading)
            {
                if (pawn.WorkTypeIsDisabled(workType)) continue;
                if (pawn.workSettings.GetPriority(workType) == 0) continue;
                pawn.workSettings.SetPriority(workType, 0);
                cleared++;
            }

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("cleared", cleared)
                .Set("summary", $"Cleared {cleared} work assignments for {pawn.LabelShort}.");
        }
    }
}
