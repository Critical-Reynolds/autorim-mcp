using System;
using System.Linq;
using AutoRim.Bridge;
using AutoRim.Core;
using AutoRim.Read;
using RimWorld;
using Verse;
using Verse.AI;

namespace AutoRim.Commands
{
    public class JobsCurrentCommand : CommandBase
    {
        public override string Name => "jobs.current";
        public override string Description => "What every colonist is doing right now, and who is idle.";

        public override JsonValue Execute(JsonValue args)
        {
            var array = JsonValue.NewArray();
            int idle = 0;

            foreach (var pawn in Refs.AddressablePawns().Where(p => p.IsFreeColonist && !p.Dead))
            {
                string job = Describe.CurrentJob(pawn);
                bool isIdle = pawn.mindState?.IsIdle ?? false;
                if (isIdle) idle++;

                var entry = JsonValue.NewObject()
                    .Set("pawn", Refs.Ref(pawn))
                    .Set("job", job ?? "nothing")
                    .Set("idle", isIdle);

                if (pawn.Drafted) entry.Set("drafted", true);
                int queued = pawn.jobs?.jobQueue?.Count ?? 0;
                if (queued > 0) entry.Set("queued", queued);

                array.Add(entry);
            }

            return JsonValue.NewObject()
                .Set("idleCount", idle)
                .Set("pawns", array);
        }
    }

    public class JobsDraftCommand : CommandBase
    {
        public override string Name => "jobs.draft";
        public override string Description => "Drafts or undrafts a pawn. Pass drafted:false to undraft.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            bool drafted = args.OptBool("drafted", true);

            if (pawn.drafter == null)
                throw CommandException.Failed($"{pawn.LabelShort} cannot be drafted.",
                    "Only colonists under your control have a draft toggle.");

            if (pawn.Downed)
                throw CommandException.Failed($"{pawn.LabelShort} is downed and cannot be drafted.");

            bool was = pawn.drafter.Drafted;
            pawn.drafter.Drafted = drafted;

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("wasDrafted", was)
                .Set("drafted", pawn.drafter.Drafted)
                .Set("summary", $"{pawn.LabelShort} {(pawn.drafter.Drafted ? "drafted" : "undrafted")}.");
        }
    }

    public class JobsMoveToCommand : CommandBase
    {
        public override string Name => "jobs.move_to";
        public override string Description => "Orders a pawn to walk to a cell. Drafts them first unless draft:false.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            RequireOnMap(pawn);

            var cell = Refs.RequireCell(args, "cell", pawn.Map);

            if (!cell.Standable(pawn.Map))
                throw CommandException.Failed($"Cell ({cell.x},{cell.z}) is not standable.",
                    "Use map.region to find open ground nearby.");

            if (!pawn.CanReach(cell, PathEndMode.OnCell, Danger.Deadly))
                throw CommandException.Failed($"{pawn.LabelShort} cannot reach ({cell.x},{cell.z}).",
                    "Something impassable is in the way, or the cell is walled off.");

            if (args.OptBool("draft", true) && pawn.drafter != null && !pawn.Drafted)
                pawn.drafter.Drafted = true;

            var job = JobMaker.MakeJob(JobDefOf.Goto, cell);
            job.playerForced = true;

            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                throw CommandException.Failed($"{pawn.LabelShort} refused the move order.");

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("cell", Refs.Cell(cell))
                .Set("summary", $"{pawn.LabelShort} ordered to ({cell.x},{cell.z}).");
        }

        internal static void RequireOnMap(Pawn pawn)
        {
            if (!pawn.Spawned || pawn.Map == null)
                throw CommandException.Failed($"{pawn.LabelShort} is not on a map.",
                    "They may be in a caravan or a transport pod.");
        }
    }

    public class JobsAttackCommand : CommandBase
    {
        public override string Name => "jobs.attack";
        public override string Description => "Orders a drafted pawn to attack a target by id. Drafts them first if needed.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            JobsMoveToCommand.RequireOnMap(pawn);

            var target = Refs.ResolveThing(args, "target");
            if (target.Map != pawn.Map)
                throw CommandException.Failed("Target is on a different map.");

            if (pawn.drafter != null && !pawn.Drafted) pawn.drafter.Drafted = true;

            bool melee = args.OptBool("melee", false) || pawn.equipment?.Primary == null ||
                         pawn.equipment.Primary.def.IsMeleeWeapon;

            var jobDef = melee ? JobDefOf.AttackMelee : JobDefOf.AttackStatic;
            var job = JobMaker.MakeJob(jobDef, target);
            job.playerForced = true;
            if (!melee) job.endIfCantShootTargetFromCurPos = true;

            if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                throw CommandException.Failed($"{pawn.LabelShort} could not take the attack order.");

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("target", Refs.Ref(target))
                .Set("mode", melee ? "melee" : "ranged")
                .Set("summary", $"{pawn.LabelShort} attacking {target.LabelShort}.");
        }
    }

    public class JobsStopCommand : CommandBase
    {
        public override string Name => "jobs.stop";
        public override string Description => "Cancels a pawn's current job and anything queued behind it.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            if (pawn.jobs == null) throw CommandException.Failed($"{pawn.LabelShort} has no job tracker.");

            string previous = Describe.CurrentJob(pawn);

            pawn.jobs.ClearQueuedJobs();
            if (pawn.jobs.curJob != null)
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced);

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("stopped", previous ?? "nothing")
                .Set("summary", $"{pawn.LabelShort} stopped ({previous ?? "was idle"}).");
        }
    }

    /// <summary>
    /// Forces a specific piece of work, the equivalent of right-clicking a target and choosing
    /// "prioritise". This is how you say "go mine that vein now" rather than adjusting
    /// priorities and hoping.
    /// </summary>
    public class JobsPrioritizeCommand : CommandBase
    {
        public override string Name => "jobs.prioritize";
        public override string Description =>
            "Forces a pawn to work on a specific target now, like right-click prioritise in game.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            JobsMoveToCommand.RequireOnMap(pawn);

            var target = Refs.ResolveThing(args, "target");
            if (target.Map != pawn.Map)
                throw CommandException.Failed("Target is on a different map.");

            if (pawn.Drafted && pawn.drafter != null) pawn.drafter.Drafted = false;

            // Ask every work giver the pawn is capable of whether it has something to do with
            // this target, and take the first that does. This mirrors how the game builds its
            // right-click menu, so the result matches what a player would see.
            foreach (var workType in DefDatabase<WorkTypeDef>.AllDefsListForReading)
            {
                if (pawn.WorkTypeIsDisabled(workType)) continue;

                foreach (var giverDef in workType.workGiversByPriority)
                {
                    if (!(giverDef.Worker is WorkGiver_Scanner scanner)) continue;

                    Job job = null;
                    try
                    {
                        if (scanner.PotentialWorkThingRequest.Accepts(target) ||
                            (scanner.PotentialWorkThingsGlobal(pawn)?.Contains(target) ?? false))
                        {
                            if (scanner.HasJobOnThing(pawn, target, false))
                                job = scanner.JobOnThing(pawn, target, false);
                        }
                    }
                    catch (Exception)
                    {
                        continue; // a work giver that throws on this target simply is not the one
                    }

                    if (job == null) continue;

                    job.playerForced = true;
                    if (!pawn.jobs.TryTakeOrderedJobPrioritizedWork(job, scanner, target.Position))
                        continue;

                    return JsonValue.NewObject()
                        .Set("pawn", Refs.Ref(pawn))
                        .Set("target", Refs.Ref(target))
                        .Set("work", workType.defName)
                        .Set("job", job.def?.defName ?? "")
                        .Set("summary", $"{pawn.LabelShort} prioritised {workType.labelShort ?? workType.label} on {target.LabelShort}.");
                }
            }

            throw CommandException.Failed(
                $"{pawn.LabelShort} has no work to do on {target.LabelShort}.",
                "The target may need a designation first (for example designate.mine or designate.hunt), or the pawn may lack the required work type.");
        }
    }
}
