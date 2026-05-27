using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS
{
    public enum TradeType
    {
        Reversal,
        Continuation,
    }

    public class StrategyRunner
    {
        public TradeExecutionResult CurrentTrade { get; private set; }
        public Order LimitOrder { get; set; }

        private LongContinuation longContinuation;
        private ShortContinuation shortContinuation;
        private LongReversalCondition longReversal;
        private ShortReversalCondition shortReversal;

        public StrategySettings Settings { get; private set; }

        public static StrategyRunner Configure(AlgoBase strategyBase, FractalManager fractalManager, StrategySettings settings)
        {
            var runner = new StrategyRunner()
            {
                Settings = settings,

                longContinuation = new LongContinuation(strategyBase, fractalManager),
                shortContinuation = new ShortContinuation(strategyBase, fractalManager),
                longReversal = new LongReversalCondition(strategyBase, fractalManager),
                shortReversal = new ShortReversalCondition(strategyBase, fractalManager)
            };

            return runner;
        }

        public void UpdateRangeInitiator(TradeRangeInitiator rangeInitiator)
        {
            Settings.RangeInitiator = rangeInitiator;
        }

        public void UpdateTradeRange(double rangeHigh, double rangeLow)
        {
            Settings.RangeHigh = rangeHigh;
            Settings.RangeLow = rangeLow;
        }

        public void Run()
        {
            if (longReversal.IsSatisfied(Settings))
            {
                CurrentTrade = longReversal.Execute(Settings);
            }

            if (shortReversal.IsSatisfied(Settings))
            {
                CurrentTrade = shortReversal.Execute(Settings);
            }

            if (longContinuation.IsSatisfied(Settings))
            {
                CurrentTrade = longContinuation.Execute(Settings);
            }

            if (shortContinuation.IsSatisfied(Settings))
            {
                CurrentTrade = shortContinuation.Execute(Settings);
            }

            // Run entry conditions

            // Run trade management conditions
        }
    }
}
