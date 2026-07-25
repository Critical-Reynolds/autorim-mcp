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
    public class HealthListSurgeriesCommand : CommandBase
    {
        public override string Name => "health.list_surgeries";
        public override string Description =>
            "Surgeries that can be performed on a pawn right now, with the body parts each applies to.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            string search = args.OptString("search");

            var array = JsonValue.NewArray();
            foreach (var recipe in HealthHelpers.AvailableSurgeries(pawn))
            {
                if (!string.IsNullOrEmpty(search) &&
                    (recipe.label ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var entry = JsonValue.NewObject()
                    .Set("defName", recipe.defName)
                    .Set("label", recipe.label ?? "")
                    .Set("targetsBodyPart", recipe.targetsBodyPart);

                if (recipe.targetsBodyPart)
                {
                    var parts = HealthHelpers.PartsFor(pawn, recipe).Take(12).ToList();
                    if (parts.Count > 0)
                        entry.Set("parts", Describe.ToArray(parts.Select(p => p.Label)));
                }

                if (recipe.surgerySuccessChanceFactor < 1f)
                    entry.Set("successFactor", Describe.Round(recipe.surgerySuccessChanceFactor, 2));

                array.Add(entry);
            }

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("count", array.Count)
                .Set("surgeries", array);
        }
    }

    public class HealthPendingSurgeriesCommand : CommandBase
    {
        public override string Name => "health.pending_surgeries";
        public override string Description => "Surgeries already queued on a pawn, in the order they will happen.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            var stack = pawn.BillStack;

            var array = JsonValue.NewArray();
            if (stack != null)
            {
                for (int i = 0; i < stack.Bills.Count; i++)
                {
                    var bill = stack.Bills[i];
                    var entry = JsonValue.NewObject()
                        .Set("index", i)
                        .Set("label", bill.LabelCap)
                        .Set("recipe", bill.recipe?.defName ?? "");

                    if (bill is Bill_Medical medical && medical.Part != null)
                        entry.Set("part", medical.Part.Label);

                    if (bill.suspended) entry.Set("suspended", true);
                    array.Add(entry);
                }
            }

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("count", array.Count)
                .Set("surgeries", array);
        }
    }

    /// <summary>
    /// Queuing surgery is destructive: amputations, organ removal and implant installation
    /// cannot be undone, and a failed operation can kill. The preview names the pawn, the
    /// operation and the exact body part before anything is queued.
    /// </summary>
    public class HealthAddSurgeryCommand : CommandBase, IPreviewable
    {
        public override string Name => "health.add_surgery";
        public override SafetyTier Tier => SafetyTier.Destructive;
        public override string Description =>
            "Queues a surgery on a pawn. Irreversible and can be fatal; needs confirm.";

        public override JsonValue Execute(JsonValue args) => Run(args, apply: true);

        public JsonValue Preview(JsonValue args) => Run(args, apply: false);

        private JsonValue Run(JsonValue args, bool apply)
        {
            var pawn = Refs.ResolvePawn(args);

            var available = HealthHelpers.AvailableSurgeries(pawn).ToList();
            if (available.Count == 0)
                throw CommandException.Failed($"No surgery is available for {pawn.LabelShort} right now.");

            var recipe = DefResolver.Resolve<RecipeDef>(args.RequireString("recipe"), "recipe",
                r => available.Contains(r));

            BodyPartRecord part = null;
            if (recipe.targetsBodyPart)
            {
                var parts = HealthHelpers.PartsFor(pawn, recipe).ToList();
                if (parts.Count == 0)
                    throw CommandException.Failed(
                        $"'{recipe.label}' has no valid body part on {pawn.LabelShort}.");

                string wanted = args.OptString("part");
                if (string.IsNullOrEmpty(wanted))
                {
                    if (parts.Count > 1)
                    {
                        var error = CommandException.BadArgs(
                            $"'{recipe.label}' needs a body part; {parts.Count} are valid.",
                            "Resend with 'part' set to one of the labels below.");
                        error.Payload = JsonValue.NewObject()
                            .Set("parts", Describe.ToArray(parts.Select(p => p.Label)));
                        throw error;
                    }
                    part = parts[0];
                }
                else
                {
                    part = parts.FirstOrDefault(p =>
                               string.Equals(p.Label, wanted, StringComparison.OrdinalIgnoreCase))
                           ?? parts.FirstOrDefault(p =>
                               (p.Label ?? "").IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (part == null)
                    {
                        var error = CommandException.NotFound(
                            $"No valid body part matching '{wanted}' for '{recipe.label}'.");
                        error.Payload = JsonValue.NewObject()
                            .Set("parts", Describe.ToArray(parts.Select(p => p.Label)));
                        throw error;
                    }
                }
            }

            string target = part != null ? $"{recipe.label} on {pawn.LabelShort}'s {part.Label}"
                                         : $"{recipe.label} on {pawn.LabelShort}";

            if (!apply)
            {
                return JsonValue.NewObject()
                    .Set("pawn", Refs.Ref(pawn))
                    .Set("recipe", recipe.defName)
                    .Set("part", part?.Label ?? JsonValue.Null)
                    .Set("successFactor", Describe.Round(recipe.surgerySuccessChanceFactor, 2))
                    .Set("wouldDo", target)
                    .Set("warning", "Surgery is irreversible and a failure can maim or kill the pawn.");
            }

            var bill = new Bill_Medical(recipe, null);
            pawn.BillStack.AddBill(bill);
            if (part != null) bill.Part = part;

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("recipe", recipe.defName)
                .Set("part", part?.Label ?? JsonValue.Null)
                .Set("queuedAt", pawn.BillStack.Bills.Count - 1)
                .Set("summary", $"Queued {target}.");
        }
    }

    public class HealthCancelSurgeryCommand : CommandBase
    {
        public override string Name => "health.cancel_surgery";
        public override string Description => "Removes a queued surgery by index. Safe: nothing has happened yet.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            var stack = pawn.BillStack;
            int index = args.RequireInt("index");

            if (stack == null || index < 0 || index >= stack.Bills.Count)
                throw CommandException.NotFound(
                    $"No queued surgery at index {index}; {pawn.LabelShort} has {stack?.Bills.Count ?? 0}.");

            var bill = stack.Bills[index];
            string label = bill.LabelCap;
            stack.Delete(bill);

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("cancelled", label)
                .Set("remaining", stack.Bills.Count)
                .Set("summary", $"Cancelled '{label}' for {pawn.LabelShort}.");
        }
    }

    internal static class HealthHelpers
    {
        public static IEnumerable<RecipeDef> AvailableSurgeries(Pawn pawn)
        {
            var recipes = pawn.def?.AllRecipes;
            if (recipes == null) yield break;

            foreach (var recipe in recipes)
            {
                if (!recipe.IsSurgery) continue;

                bool available;
                try
                {
                    available = recipe.AvailableOnNow(pawn, null);
                }
                catch (Exception)
                {
                    continue;
                }

                if (!available) continue;

                // A part-targeting recipe with nothing to target is not really available.
                if (recipe.targetsBodyPart && !PartsFor(pawn, recipe).Any()) continue;

                yield return recipe;
            }
        }

        public static IEnumerable<BodyPartRecord> PartsFor(Pawn pawn, RecipeDef recipe)
        {
            IEnumerable<BodyPartRecord> parts;
            try
            {
                parts = recipe.Worker?.GetPartsToApplyOn(pawn, recipe);
            }
            catch (Exception)
            {
                yield break;
            }

            if (parts == null) yield break;

            foreach (var part in parts)
            {
                bool ok;
                try
                {
                    ok = recipe.AvailableOnNow(pawn, part);
                }
                catch (Exception)
                {
                    continue;
                }

                if (ok) yield return part;
            }
        }
    }

    // ---- animals ---------------------------------------------------------------------------------

    public class AnimalsSetTrainingCommand : CommandBase
    {
        public override string Name => "animals.set_training";
        public override string Description =>
            "Turns a training goal on or off for an animal: Tameness, Obedience, Release or Rescue.";

        public override JsonValue Execute(JsonValue args)
        {
            var pawn = Refs.ResolvePawn(args);
            if (pawn.training == null)
                throw CommandException.Failed($"{pawn.LabelShort} cannot be trained.");

            var trainable = DefResolver.Resolve<TrainableDef>(args.RequireString("training"), "training");
            bool wanted = args.OptBool("wanted", true);

            var report = pawn.training.CanAssignToTrain(trainable);
            if (!report.Accepted)
                throw CommandException.Failed(
                    $"{pawn.LabelShort} cannot be trained in {trainable.label}: {report.Reason}");

            // Recursive so prerequisites come along: asking for Release implies Obedience.
            pawn.training.SetWantedRecursive(trainable, wanted);

            return JsonValue.NewObject()
                .Set("pawn", Refs.Ref(pawn))
                .Set("training", trainable.defName)
                .Set("wanted", pawn.training.GetWanted(trainable))
                .Set("learned", pawn.training.HasLearned(trainable))
                .Set("summary", $"{pawn.LabelShort}: {trainable.label} training {(wanted ? "requested" : "cancelled")}.");
        }
    }

    public class AnimalsSetMasterCommand : CommandBase
    {
        public override string Name => "animals.set_master";
        public override string Description => "Assigns or clears an animal's master. Pass master:null to clear.";

        public override JsonValue Execute(JsonValue args)
        {
            var animal = Refs.ResolvePawn(args, "animal");
            if (animal.playerSettings == null)
                throw CommandException.Failed($"{animal.LabelShort} cannot have a master.");

            string previous = animal.playerSettings.Master?.LabelShort ?? "none";

            if (!args.Has("master") || args["master"].IsNull || args["master"].AsString("") == "")
            {
                animal.playerSettings.Master = null;
                return JsonValue.NewObject()
                    .Set("animal", Refs.Ref(animal))
                    .Set("from", previous)
                    .Set("to", "none")
                    .Set("summary", $"{animal.LabelShort} no longer has a master.");
            }

            var master = Refs.ResolvePawn(args, "master");

            if (!TrainableUtility.CanBeMaster(master, animal, true))
                throw CommandException.Failed(
                    $"{master.LabelShort} cannot be master of {animal.LabelShort}.",
                    "The master needs the Handling work type enabled and enough animals skill.");

            animal.playerSettings.Master = master;

            return JsonValue.NewObject()
                .Set("animal", Refs.Ref(animal))
                .Set("from", previous)
                .Set("to", master.LabelShort)
                .Set("summary", $"{master.LabelShort} is now master of {animal.LabelShort}.");
        }
    }
}
