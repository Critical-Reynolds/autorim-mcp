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
    /// Trading runs as a session: open with a trader, adjust quantities, look at the balance,
    /// then execute. Only the final execute is destructive — everything before it can be
    /// abandoned by closing the session, exactly like backing out of the trade dialog.
    /// </summary>
    internal static class TradeHelpers
    {
        public static List<ITrader> TradersOn(Map map)
        {
            var traders = new List<ITrader>();
            if (map == null) return traders;

            // Orbital ships and other passing traders.
            var ships = map.passingShipManager?.passingShips;
            if (ships != null)
                foreach (var ship in ships)
                    if (ship is ITrader trader) traders.Add(trader);

            // Visiting trade caravans: the trader is a pawn on the map.
            foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                if (pawn is ITrader pawnTrader && pawnTrader.TraderKind != null)
                    traders.Add(pawnTrader);

            return traders;
        }

        public static ITrader ResolveTrader(JsonValue args, Map map)
        {
            var traders = TradersOn(map);
            if (traders.Count == 0)
                throw CommandException.Failed("No trader is available right now.",
                    "Traders arrive with caravans or orbital ships. colony.letters shows arrivals.");

            if (!args.Has("trader"))
            {
                if (traders.Count == 1) return traders[0];

                var error = CommandException.BadArgs($"{traders.Count} traders are available; name one.");
                error.Payload = JsonValue.NewObject().Set("traders", Describe.ToArray(traders.Select(t => t.TraderName)));
                throw error;
            }

            string query = args.RequireString("trader");
            var matches = traders.Where(t =>
                (t.TraderName ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            if (matches.Count == 1) return matches[0];

            var failure = matches.Count == 0
                ? CommandException.NotFound($"No trader matches '{query}'.")
                : new CommandException(ErrorCode.Ambiguous, $"'{query}' matches {matches.Count} traders.",
                    "Use the exact trader name.");
            failure.Payload = JsonValue.NewObject().Set("traders", Describe.ToArray(traders.Select(t => t.TraderName)));
            throw failure;
        }

        /// <summary>
        /// Picks the negotiator. Social skill drives trade prices, so defaulting to the best
        /// negotiator available is what a player would do anyway.
        /// </summary>
        public static Pawn ResolveNegotiator(JsonValue args, Map map)
        {
            if (args.Has("negotiator")) return Refs.ResolvePawn(args, "negotiator");

            var best = map.mapPawns.FreeColonistsSpawned
                .Where(p => !p.Downed && !p.InMentalState && p.health.capacities.CapableOf(PawnCapacityDefOf.Talking))
                .OrderByDescending(p => p.skills?.GetSkill(SkillDefOf.Social)?.Level ?? 0)
                .FirstOrDefault();

            if (best == null)
                throw CommandException.Failed("No colonist is able to negotiate.",
                    "The negotiator must be conscious, able to talk, and not in a mental break.");

            return best;
        }

        public static void RequireActiveSession()
        {
            if (!TradeSession.Active || TradeSession.deal == null)
                throw CommandException.Failed("No trade session is open.",
                    "Call trade.open first.");
        }

        public static JsonValue DescribeTradeable(Tradeable tradeable)
        {
            return JsonValue.NewObject()
                .Set("label", tradeable.Label)
                .Set("defName", tradeable.ThingDef?.defName ?? "")
                .Set("traderHas", tradeable.CountHeldBy(Transactor.Trader))
                .Set("colonyHas", tradeable.CountHeldBy(Transactor.Colony))
                .Set("marketValue", Describe.Round(tradeable.BaseMarketValue))
                .Set("countToTransfer", tradeable.CountToTransfer);
        }

        /// <summary>
        /// Silver balance of the current deal. Positive means the colony receives silver.
        /// </summary>
        public static int SilverBalance()
        {
            var currency = TradeSession.deal?.CurrencyTradeable;
            return currency?.CountToTransfer ?? 0;
        }
    }

    public class TradeListTradersCommand : CommandBase
    {
        public override string Name => "trade.list_traders";
        public override string Description => "Traders you can deal with right now.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            var traders = TradeHelpers.TradersOn(map);

            var array = JsonValue.NewArray();
            foreach (var trader in traders)
            {
                array.Add(JsonValue.NewObject()
                    .Set("name", trader.TraderName ?? "")
                    .Set("kind", trader.TraderKind?.label ?? "")
                    .Set("faction", trader.Faction?.Name ?? "")
                    .Set("canTradeNow", trader.CanTradeNow));
            }

            return JsonValue.NewObject()
                .Set("count", array.Count)
                .Set("sessionActive", TradeSession.Active)
                .Set("traders", array);
        }
    }

    public class TradeOpenCommand : CommandBase
    {
        public override string Name => "trade.open";
        public override string Description =>
            "Opens a trade session. Picks your best negotiator unless one is named. Nothing is exchanged until trade.execute.";

        public override JsonValue Execute(JsonValue args)
        {
            var map = GameState.RequireMap();
            var trader = TradeHelpers.ResolveTrader(args, map);

            if (!trader.CanTradeNow)
                throw CommandException.Failed($"{trader.TraderName} cannot trade right now.");

            var negotiator = TradeHelpers.ResolveNegotiator(args, map);

            if (TradeSession.Active) TradeSession.Close();

            bool giftMode = args.OptBool("giftMode", false);
            TradeSession.SetupWith(trader, negotiator, giftMode);

            return JsonValue.NewObject()
                .Set("trader", trader.TraderName)
                .Set("negotiator", Refs.Ref(negotiator))
                .Set("socialSkill", negotiator.skills?.GetSkill(SkillDefOf.Social)?.Level ?? 0)
                .Set("giftMode", giftMode)
                .Set("tradeableCount", TradeSession.deal?.TradeableCount ?? 0)
                .Set("summary", $"Trading with {trader.TraderName}, negotiator {negotiator.LabelShort}.");
        }
    }

    public class TradeStockCommand : CommandBase
    {
        public override string Name => "trade.stock";
        public override string Description =>
            "Items in the open trade session. filter: trader (what they sell), colony (what you can sell), all.";

        public override JsonValue Execute(JsonValue args)
        {
            TradeHelpers.RequireActiveSession();

            string filter = args.OptString("filter", "all").ToLowerInvariant();
            string search = args.OptString("search");

            IEnumerable<Tradeable> tradeables = TradeSession.deal.AllTradeables
                .Where(t => t.TraderWillTrade && !t.IsCurrency);

            switch (filter)
            {
                case "trader": tradeables = tradeables.Where(t => t.CountHeldBy(Transactor.Trader) > 0); break;
                case "colony": tradeables = tradeables.Where(t => t.CountHeldBy(Transactor.Colony) > 0); break;
                case "all": break;
                default:
                    throw CommandException.BadArgs($"Unknown filter '{filter}'.", "Use trader, colony or all.");
            }

            if (!string.IsNullOrEmpty(search))
                tradeables = tradeables.Where(t =>
                    (t.Label ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);

            var list = tradeables.OrderByDescending(t => t.BaseMarketValue).ToList();
            args.ReadPaging(out int offset, out int limit, 40, 150);

            var page = Describe.Page(list, offset, limit, TradeHelpers.DescribeTradeable);
            page.Set("trader", TradeSession.trader?.TraderName ?? "");
            page.Set("silverBalance", TradeHelpers.SilverBalance());
            return page;
        }
    }

    public class TradeSetCommand : CommandBase
    {
        public override string Name => "trade.set";
        public override string Description =>
            "Sets how many of an item to trade. Positive buys from the trader, negative sells to them. Nothing moves until trade.execute.";

        public override JsonValue Execute(JsonValue args)
        {
            TradeHelpers.RequireActiveSession();

            string query = args.RequireString("item");
            int count = args.RequireInt("count");

            var matches = TradeSession.deal.AllTradeables
                .Where(t => !t.IsCurrency && t.TraderWillTrade)
                .Where(t => string.Equals(t.Label, query, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(t.ThingDef?.defName, query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
                matches = TradeSession.deal.AllTradeables
                    .Where(t => !t.IsCurrency && t.TraderWillTrade)
                    .Where(t => (t.Label ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

            if (matches.Count == 0)
                throw CommandException.NotFound($"'{query}' is not in this trade.",
                    "trade.stock lists what is available.");

            if (matches.Count > 1)
            {
                var error = new CommandException(ErrorCode.Ambiguous,
                    $"'{query}' matches {matches.Count} tradeable items.", "Use the exact label.");
                error.Payload = JsonValue.NewObject()
                    .Set("candidates", Describe.ToArray(matches.Take(8).Select(t => t.Label)));
                throw error;
            }

            var tradeable = matches[0];

            // AdjustTo applies the game's own limits, which account for more than raw stock:
            // reachability, forbidden items, and what the trader is actually willing to take.
            var report = tradeable.CanAdjustTo(count);
            if (!report.Accepted)
            {
                var error = CommandException.BadArgs(
                    $"Cannot trade {count} of '{tradeable.Label}': {report.Reason}");
                error.Payload = JsonValue.NewObject()
                    .Set("min", tradeable.GetMinimumToTransfer())
                    .Set("max", tradeable.GetMaximumToTransfer())
                    .Set("traderHas", tradeable.CountHeldBy(Transactor.Trader))
                    .Set("colonyHas", tradeable.CountHeldBy(Transactor.Colony));
                throw error;
            }

            tradeable.AdjustTo(count);
            TradeSession.deal.UpdateCurrencyCount();

            return JsonValue.NewObject()
                .Set("item", TradeHelpers.DescribeTradeable(tradeable))
                .Set("silverBalance", TradeHelpers.SilverBalance())
                .Set("summary",
                    $"{(count >= 0 ? "Buying" : "Selling")} {Math.Abs(count)} x {tradeable.Label}. Silver balance now {TradeHelpers.SilverBalance()}.");
        }
    }

    public class TradeEvaluateCommand : CommandBase
    {
        public override string Name => "trade.evaluate";
        public override string Description => "Summarises the pending deal: what moves each way and the silver balance.";

        public override JsonValue Execute(JsonValue args)
        {
            TradeHelpers.RequireActiveSession();

            var buying = JsonValue.NewArray();
            var selling = JsonValue.NewArray();

            foreach (var tradeable in TradeSession.deal.AllTradeables)
            {
                if (tradeable.IsCurrency || tradeable.CountToTransfer == 0) continue;

                var entry = JsonValue.NewObject()
                    .Set("label", tradeable.Label)
                    .Set("count", Math.Abs(tradeable.CountToTransfer));

                if (tradeable.CountToTransfer > 0) buying.Add(entry);
                else selling.Add(entry);
            }

            int balance = TradeHelpers.SilverBalance();

            return JsonValue.NewObject()
                .Set("trader", TradeSession.trader?.TraderName ?? "")
                .Set("buying", buying)
                .Set("selling", selling)
                .Set("silverBalance", balance)
                .Set("traderHasEnoughSilver", TradeSession.deal.DoesTraderHaveEnoughSilver())
                .Set("interpretation", balance >= 0
                    ? $"The colony would receive {balance} silver."
                    : $"The colony would pay {-balance} silver.");
        }
    }

    public class TradeExecuteCommand : CommandBase, IPreviewable
    {
        public override string Name => "trade.execute";
        public override SafetyTier Tier => SafetyTier.Destructive;
        public override string Description =>
            "Completes the trade. Goods and silver change hands immediately and cannot be undone; needs confirm.";

        public override JsonValue Execute(JsonValue args)
        {
            TradeHelpers.RequireActiveSession();

            bool actuallyTraded;
            bool ok = TradeSession.deal.TryExecute(out actuallyTraded);

            if (!ok || !actuallyTraded)
            {
                var reasons = TradeSession.deal.cannotSellReasons;
                var error = CommandException.Failed("The trade did not go through.",
                    "Check that the trader has enough silver and that everything is still reachable.");
                if (reasons != null && reasons.Count > 0)
                    error.Payload = JsonValue.NewObject().Set("reasons", Describe.ToArray(reasons));
                throw error;
            }

            string trader = TradeSession.trader?.TraderName ?? "trader";
            TradeSession.Close();

            return JsonValue.NewObject()
                .Set("traded", true)
                .Set("trader", trader)
                .Set("summary", $"Trade with {trader} completed.");
        }

        public JsonValue Preview(JsonValue args)
        {
            TradeHelpers.RequireActiveSession();

            var evaluate = new TradeEvaluateCommand().Execute(args);
            evaluate.Set("warning", "Executing exchanges the goods and silver immediately. This cannot be undone.");
            return evaluate;
        }
    }

    public class TradeCloseCommand : CommandBase
    {
        public override string Name => "trade.close";
        public override string Description => "Abandons the open trade session without exchanging anything.";

        public override JsonValue Execute(JsonValue args)
        {
            if (!TradeSession.Active)
                return JsonValue.NewObject().Set("summary", "No trade session was open.");

            string trader = TradeSession.trader?.TraderName ?? "trader";
            TradeSession.Close();

            return JsonValue.NewObject()
                .Set("closed", true)
                .Set("summary", $"Closed the trade session with {trader}. Nothing was exchanged.");
        }
    }
}
