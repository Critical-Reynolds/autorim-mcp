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
    /// Workbench bills: what gets made, how many, and by whom. This is where most of a
    /// colony's production actually gets decided.
    /// </summary>
    internal static class BillHelpers
    {
        /// <summary>
        /// Production benches only.
        ///
        /// Pawns and corpses also implement IBillGiver — that is how surgery is queued — so a
        /// naive interface check lists every colonist and pet as a "workbench". Medical bills
        /// belong to the health commands; this is strictly the crafting side.
        /// </summary>
        public static IEnumerable<Thing> BillGiversOn(Map map)
        {
            return map.listerThings.AllThings
                .Where(t => t is IBillGiver)
                .Where(t => !(t is Pawn) && !(t is Corpse))
                .Where(t => t.Faction == Faction.OfPlayer);
        }

        public static Thing ResolveBench(JsonValue args, Map map, string argName = "bench")
        {
            var node = args[argName];

            if (node.Type == JsonType.Number || int.TryParse(node.AsString(""), out _))
            {
                int id = node.Type == JsonType.Number ? node.AsInt() : int.Parse(node.AsString());
                var byId = BillGiversOn(map).FirstOrDefault(t => t.thingIDNumber == id);
                if (byId == null)
                    throw CommandException.NotFound($"No workbench with id {id}.",
                        "bills.list_workbenches shows what is available.");
                return byId;
            }

            string query = node.AsString();
            if (string.IsNullOrEmpty(query))
                throw CommandException.BadArgs($"Missing '{argName}' (workbench id or name).");

            var matches = BillGiversOn(map)
                .Where(t => (t.def.label ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            t.def.defName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (matches.Count == 1) return matches[0];

            if (matches.Count == 0)
                throw CommandException.NotFound($"No workbench matches '{query}'.",
                    "bills.list_workbenches shows what is available.");

            var error = new CommandException(ErrorCode.Ambiguous,
                $"'{query}' matches {matches.Count} workbenches.",
                "Resend using the numeric id of the one you mean.");
            var candidates = JsonValue.NewArray();
            foreach (var match in matches.Take(8))
                candidates.Add(JsonValue.NewObject()
                    .Set("id", match.thingIDNumber)
                    .Set("label", match.LabelShort)
                    .Set("pos", Refs.Cell(match.Position)));
            error.Payload = JsonValue.NewObject().Set("candidates", candidates);
            throw error;
        }

        public static JsonValue DescribeBill(Bill bill, int index)
        {
            var result = JsonValue.NewObject()
                .Set("index", index)
                .Set("label", bill.LabelCap)
                .Set("recipe", bill.recipe?.defName ?? "");

            if (bill.suspended) result.Set("suspended", true);

            if (bill is Bill_Production production)
            {
                result.Set("repeatMode", production.repeatMode?.defName ?? "");
                if (production.repeatMode == BillRepeatModeDefOf.RepeatCount)
                    result.Set("repeatCount", production.repeatCount);
                else if (production.repeatMode == BillRepeatModeDefOf.TargetCount)
                    result.Set("targetCount", production.targetCount);

                if (production.PawnRestriction != null)
                    result.Set("restrictedTo", Refs.Ref(production.PawnRestriction));
                if (production.paused) result.Set("paused", true);
            }

            return result;
        }

        public static BillStack StackOf(Thing bench)
        {
            var giver = bench as IBillGiver;
            if (giver?.BillStack == null)
                throw CommandException.Failed($"{bench.LabelShort} does not accept bills.");
            return giver.BillStack;
        }

        public static Bill BillAt(BillStack stack, int index)
        {
            if (index < 0 || index >= stack.Bills.Count)
                throw CommandException.NotFound(
                    $"No bill at index {index}; this bench has {stack.Bills.Count}.",
                    "bills.list shows the current indexes.");
            return stack.Bills[index];
        }
    }

    public class BillsListWorkbenchesCommand : CommandBase
    {
        public override string Name => "bills.list_workbenches";
        public override string Description => "Every workbench and other bill-taking building the colony owns.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.ResolveMap(args.Has("map") ? args.OptInt("map", 0) : (int?)null);

            var benches = BillHelpers.BillGiversOn(map).ToList();
            args.ReadPaging(out int offset, out int limit, 50, 200);

            return Describe.Page(benches, offset, limit, bench => JsonValue.NewObject()
                .Set("id", bench.thingIDNumber)
                .Set("label", bench.LabelShort)
                .Set("defName", bench.def.defName)
                .Set("pos", Refs.Cell(bench.Position))
                .Set("billCount", ((IBillGiver)bench).BillStack?.Count ?? 0)
                .Set("usable", ((IBillGiver)bench).CurrentlyUsableForBills()));
        }
    }

    public class BillsListCommand : CommandBase
    {
        public override string Name => "bills.list";
        public override string Description => "Bills queued on one workbench, in the order they are worked.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            var bench = BillHelpers.ResolveBench(args, map);
            var stack = BillHelpers.StackOf(bench);

            var array = JsonValue.NewArray();
            for (int i = 0; i < stack.Bills.Count; i++)
                array.Add(BillHelpers.DescribeBill(stack.Bills[i], i));

            return JsonValue.NewObject()
                .Set("bench", Refs.Ref(bench))
                .Set("count", stack.Bills.Count)
                .Set("maxCount", BillStack.MaxCount)
                .Set("bills", array);
        }
    }

    public class BillsAddCommand : CommandBase
    {
        public override string Name => "bills.add";
        public override string Description =>
            "Adds a bill to a workbench. repeat: forever, count (with repeatCount), or target (with targetCount).";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            var bench = BillHelpers.ResolveBench(args, map);
            var stack = BillHelpers.StackOf(bench);

            if (stack.Count >= BillStack.MaxCount)
                throw CommandException.Failed(
                    $"{bench.LabelShort} already has the maximum of {BillStack.MaxCount} bills.");

            var recipe = DefResolver.Resolve<RecipeDef>(args.RequireString("recipe"), "recipe");

            // Refuse a recipe this bench cannot run, rather than adding a bill that silently
            // never gets worked.
            var available = bench.def.AllRecipes ?? new List<RecipeDef>();
            if (!available.Contains(recipe))
            {
                var error = CommandException.Failed(
                    $"{bench.LabelShort} cannot make '{recipe.label}'.",
                    "The candidates below are what this bench can produce.");
                error.Payload = JsonValue.NewObject()
                    .Set("availableRecipes", DefResolver.Describe(available.Take(30)));
                throw error;
            }

            if (recipe.researchPrerequisite != null && !recipe.researchPrerequisite.IsFinished)
                throw CommandException.Failed(
                    $"'{recipe.label}' needs the '{recipe.researchPrerequisite.label}' research first.");

            var bill = BillUtility.MakeNewBill(recipe, null);
            stack.AddBill(bill);

            if (bill is Bill_Production production)
            {
                ApplyRepeat(production, args);

                if (args.Has("worker"))
                {
                    var worker = Refs.ResolvePawn(args, "worker");
                    production.SetPawnRestriction(worker);
                }
            }

            int index = stack.Bills.Count - 1;

            return JsonValue.NewObject()
                .Set("bench", Refs.Ref(bench))
                .Set("bill", BillHelpers.DescribeBill(bill, index))
                .Set("summary", $"Added '{bill.LabelCap}' to {bench.LabelShort}.");
        }

        internal static void ApplyRepeat(Bill_Production production, JsonValue args)
        {
            string repeat = args.OptString("repeat");
            if (string.IsNullOrEmpty(repeat)) return;

            switch (repeat.ToLowerInvariant())
            {
                case "forever":
                    production.repeatMode = BillRepeatModeDefOf.Forever;
                    break;

                case "count":
                case "repeat":
                    production.repeatMode = BillRepeatModeDefOf.RepeatCount;
                    production.repeatCount = Math.Max(1, args.OptInt("repeatCount", 10));
                    break;

                case "target":
                case "until":
                    production.repeatMode = BillRepeatModeDefOf.TargetCount;
                    production.targetCount = Math.Max(1, args.OptInt("targetCount", 20));
                    break;

                default:
                    throw CommandException.BadArgs($"Unknown repeat mode '{repeat}'.",
                        "Use forever, count (with repeatCount), or target (with targetCount).");
            }
        }
    }

    public class BillsSetCommand : CommandBase
    {
        public override string Name => "bills.set";
        public override string Description =>
            "Changes an existing bill: repeat mode, counts, worker restriction, suspended state.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            var bench = BillHelpers.ResolveBench(args, map);
            var stack = BillHelpers.StackOf(bench);
            var bill = BillHelpers.BillAt(stack, args.RequireInt("index"));

            var changes = new List<string>();

            if (args.Has("suspended"))
            {
                bill.suspended = args.OptBool("suspended");
                changes.Add(bill.suspended ? "suspended" : "resumed");
            }

            if (bill is Bill_Production production)
            {
                if (args.Has("repeat"))
                {
                    BillsAddCommand.ApplyRepeat(production, args);
                    changes.Add($"repeat={production.repeatMode?.defName}");
                }
                else
                {
                    // Allow adjusting the count without restating the mode.
                    if (args.Has("repeatCount"))
                    {
                        production.repeatCount = Math.Max(1, args.RequireInt("repeatCount"));
                        changes.Add($"repeatCount={production.repeatCount}");
                    }
                    if (args.Has("targetCount"))
                    {
                        production.targetCount = Math.Max(1, args.RequireInt("targetCount"));
                        changes.Add($"targetCount={production.targetCount}");
                    }
                }

                if (args.Has("worker"))
                {
                    if (args["worker"].IsNull || args["worker"].AsString("") == "")
                    {
                        production.SetAnyPawnRestriction();
                        changes.Add("worker=anyone");
                    }
                    else
                    {
                        var worker = Refs.ResolvePawn(args, "worker");
                        production.SetPawnRestriction(worker);
                        changes.Add($"worker={worker.LabelShort}");
                    }
                }
            }

            if (changes.Count == 0)
                throw CommandException.BadArgs("Nothing to change.",
                    "Pass one of: repeat, repeatCount, targetCount, worker, suspended.");

            int index = stack.IndexOf(bill);
            return JsonValue.NewObject()
                .Set("bench", Refs.Ref(bench))
                .Set("bill", BillHelpers.DescribeBill(bill, index))
                .Set("changes", Describe.ToArray(changes))
                .Set("summary", $"Updated '{bill.LabelCap}': {string.Join(", ", changes.ToArray())}.");
        }
    }

    public class BillsRemoveCommand : CommandBase
    {
        public override string Name => "bills.remove";
        public override string Description => "Deletes a bill from a workbench by index.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            var bench = BillHelpers.ResolveBench(args, map);
            var stack = BillHelpers.StackOf(bench);
            var bill = BillHelpers.BillAt(stack, args.RequireInt("index"));

            string label = bill.LabelCap;
            stack.Delete(bill);

            return JsonValue.NewObject()
                .Set("bench", Refs.Ref(bench))
                .Set("removed", label)
                .Set("remaining", stack.Bills.Count)
                .Set("summary", $"Removed '{label}' from {bench.LabelShort}.");
        }
    }

    public class BillsReorderCommand : CommandBase
    {
        public override string Name => "bills.reorder";
        public override string Description => "Moves a bill up or down the queue. offset -1 moves it one place earlier.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            var bench = BillHelpers.ResolveBench(args, map);
            var stack = BillHelpers.StackOf(bench);
            var bill = BillHelpers.BillAt(stack, args.RequireInt("index"));

            int offset = args.RequireInt("offset");
            stack.Reorder(bill, offset);

            var array = JsonValue.NewArray();
            for (int i = 0; i < stack.Bills.Count; i++)
                array.Add(BillHelpers.DescribeBill(stack.Bills[i], i));

            return JsonValue.NewObject()
                .Set("bench", Refs.Ref(bench))
                .Set("bills", array)
                .Set("summary", $"Moved '{bill.LabelCap}' by {offset}.");
        }
    }
}
