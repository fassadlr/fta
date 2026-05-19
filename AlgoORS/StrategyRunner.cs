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

        private LongReversalCondition longReversal;
        private ShortReversalCondition shortReversal;

        public ExecutionCriteria Criteria { get; private set; }

        public static StrategyRunner Configure(AlgoBase strategyBase, FractalManager fractalManager, double entryPercentage, double maxRiskReward)
        {
            var runner = new StrategyRunner()
            {
                Criteria = new ExecutionCriteria()
                {
                    EntryPercentage = entryPercentage,
                    MaxRiskReward = maxRiskReward
                },

                longReversal = new LongReversalCondition(strategyBase, fractalManager),
                shortReversal = new ShortReversalCondition(strategyBase, fractalManager)
            };

            strategyBase.PrintDebug($"StrategyRunner configured with EntryPercentage: {entryPercentage}, MaxRiskReward: {maxRiskReward}");

            return runner;
        }

        public void UpdateRangeInitiator(TradeRangeInitiator rangeInitiator)
        {
            Criteria.RangeInitiator = rangeInitiator;
        }

        public void UpdateTradeRange(double rangeHigh, double rangeLow)
        {
            Criteria.RangeHigh = rangeHigh;
            Criteria.RangeLow = rangeLow;
        }

        public void Run()
        {
            if (longReversal.IsSatisfied(Criteria))
            {
                CurrentTrade = longReversal.Execute(Criteria);
            }

            if (shortReversal.IsSatisfied(Criteria))
            {
                CurrentTrade = shortReversal.Execute(Criteria);
            }

            // Run entry conditions

            // Run trade management conditions
        }
    }
}
