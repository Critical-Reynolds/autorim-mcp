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
    public class PrisonersListCommand : CommandBase
    {
        public override string Name => "prisoners.list";
        public override string Description => "Prisoners with their resistance, will, recruitability and current interaction mode.";

        public override JsonValue Execute(JsonValue args)
        {
            var prisoners = Refs.AddressablePawns().Where(p => p.IsPrisoner && !p.Dead).ToList();

            var array = JsonValue.NewArray();
            foreach (var pawn in prisoners)
            {
                var entry = Describe.PawnSummary(pawn);
                if (pawn.guest != null)
                {
                    entry.Set("interactionMode", pawn.guest.ExclusiveInteractionMode?.defName ?? "");
                    entry.Set("resistance", Describe.Round(pawn.guest.Resistance));
                    entry.Set("will", Describe.Round(pawn.guest.will));
                    entry.Set("recruitable", pawn.guest.Recruitable);
                    entry.Set("secure", pawn.guest.PrisonerIsSecure);
                }
                array.Add(entry);
            }

            return JsonValue.NewObject().Set("count", array.Count).Set("prisoners", array);
        }
    }

    public class PrisonersSetInteractionCommand : CommandBase
    {
        public override string Name => "prisoners.set_interaction";
        public override string Description =>
            "Sets how a prisoner is handled: maintain, recruit, reduce_resistance, convert, enslave, release. Execution has its own command.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            if (pawn.guest == null || !pawn.IsPrisoner)
                throw CommandException.Failed($"{pawn.LabelShort} is not a prisoner.");

            string requested = args.RequireString("mode").Trim().ToLowerInvariant();

            if (requested == "execution" || requested == "execute")
                throw CommandException.BadArgs(
                    "Execution is handled by prisoners.execute.",
                    "That command requires confirmation because it kills the prisoner.");

            if (requested == "release")
                throw CommandException.BadArgs(
                    "Releasing is handled by prisoners.release.",
                    "That command requires confirmation because the prisoner leaves for good.");

            var mode = PrisonerHelpers.ParseMode(requested);
            var previous = pawn.guest.ExclusiveInteractionMode;
            pawn.guest.SetExclusiveInteraction(mode);

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("from", previous?.defName ?? "none")
                .Set("to", mode.defName)
                .Set("summary", $"{pawn.LabelShort}: prisoner mode {previous?.defName ?? "none"} -> {mode.defName}.");
        }
    }

    public class PrisonersReleaseCommand : CommandBase, IPreviewable
    {
        public override string Name => "prisoners.release";
        public override SafetyTier Tier => SafetyTier.Destructive;
        public override string Description => "Releases a prisoner. They walk off the map and are gone; needs confirm.";

        public override JsonValue Execute(JsonValue args) => Run(args, true);
        public JsonValue Preview(JsonValue args) => Run(args, false);

        private JsonValue Run(JsonValue args, bool apply)
        {
            var pawn = PrisonerHelpers.RequirePrisoner(args);

            if (!apply)
                return JsonValue.NewObject()
                    .Set("pawn", Refs.Ref(pawn))
                    .Set("wouldDo", $"Release {pawn.LabelShort}, who would leave the map permanently.")
                    .Set("resistance", Describe.Round(pawn.guest.Resistance))
                    .Set("warning", "You lose any chance of recruiting them, and they take their gear.");

            pawn.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.Release);

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("summary", $"{pawn.LabelShort} marked for release.");
        }
    }

    public class PrisonersExecuteCommand : CommandBase, IPreviewable
    {
        public override string Name => "prisoners.execute";
        public override SafetyTier Tier => SafetyTier.Destructive;
        public override string Description =>
            "Marks a prisoner for execution. They will be killed, and the colony takes a mood hit; needs confirm.";

        public override JsonValue Execute(JsonValue args) => Run(args, true);
        public JsonValue Preview(JsonValue args) => Run(args, false);

        private JsonValue Run(JsonValue args, bool apply)
        {
            var pawn = PrisonerHelpers.RequirePrisoner(args);

            if (!apply)
                return JsonValue.NewObject()
                    .Set("pawn", Refs.Ref(pawn))
                    .Set("wouldDo", $"Execute {pawn.LabelShort}. A colonist will kill them.")
                    .Set("warning",
                        "This kills the prisoner. Most colonists get a lasting mood penalty, and it can conflict with your ideology.");

            pawn.guest.SetExclusiveInteraction(PrisonerInteractionModeDefOf.Execution);

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("summary", $"{pawn.LabelShort} marked for EXECUTION.");
        }
    }

    internal static class PrisonerHelpers
    {
        public static Pawn RequirePrisoner(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            if (pawn.guest == null || !pawn.IsPrisoner)
                throw CommandException.Failed($"{pawn.LabelShort} is not a prisoner.");
            return pawn;
        }

        public static PrisonerInteractionModeDef ParseMode(string requested)
        {
            switch (requested)
            {
                case "maintain": case "maintainonly": case "none":
                    return PrisonerInteractionModeDefOf.MaintainOnly;
                case "recruit": case "attemptrecruit":
                    return PrisonerInteractionModeDefOf.AttemptRecruit;
                case "reduce_resistance": case "resistance":
                    return PrisonerInteractionModeDefOf.ReduceResistance;
                case "convert":
                    return PrisonerInteractionModeDefOf.Convert;
                case "enslave":
                    return PrisonerInteractionModeDefOf.Enslave;
                case "reduce_will": case "will":
                    return PrisonerInteractionModeDefOf.ReduceWill;
                default:
                {
                    // Fall back to a def lookup so DLC and modded modes still work.
                    var def = DefResolver.ResolveOrNull<PrisonerInteractionModeDef>(requested);
                    if (def != null) return def;

                    var error = CommandException.BadArgs($"Unknown prisoner mode '{requested}'.");
                    error.Payload = JsonValue.NewObject().Set("available", Describe.ToArray(
                        DefDatabase<PrisonerInteractionModeDef>.AllDefsListForReading.Select(d => d.defName)));
                    throw error;
                }
            }
        }
    }
}
