using System.Collections.Generic;
using System.Linq;
using NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS.Conditions;
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
        private LongContinuation longContinuation;
        private ShortContinuation shortContinuation;
        private LongReversalCondition longReversal;
        private ShortReversalCondition shortReversal;
        private LongContinuationVWAP_Retrace longContination_VWAP_Retrace;

        public RunnerConfiguration Configuration { get; private set; }

        private List<Condition> conditions;

        public static StrategyRunner Configure(AlgoBase strategyBase, FractalManager fractalManager, RunnerConfiguration settings)
        {
            var runner = new StrategyRunner()
            {
                Configuration = settings,

                conditions =
                [
                    new LongContinuation(strategyBase, fractalManager),
                    new ShortContinuation(strategyBase, fractalManager),
                    new LongReversalCondition(strategyBase, fractalManager),
                    new ShortReversalCondition(strategyBase, fractalManager),
                    new LongContinuationVWAP_Retrace(strategyBase, fractalManager)
                ],
            };

            return runner;
        }

        public void UpdateRangeInitiator(TradeRangeInitiator rangeInitiator)
        {
            Configuration.RangeInitiator = rangeInitiator;
        }

        public void UpdateTradeRange(double rangeHigh, double rangeLow)
        {
            Configuration.RangeHigh = rangeHigh;
            Configuration.RangeLow = rangeLow;
        }

        public TradeExecutionDetails Run()
        {
            var onlyMe = conditions.FirstOrDefault(c => c.ExecuteOnlyMe);
            if (onlyMe != null && onlyMe.IsSatisfied(Configuration))
            {
                return onlyMe.Execute(Configuration);
            }

            var satisfiedCondition = conditions.FirstOrDefault(c => c.IsSatisfied(Configuration));
            if (satisfiedCondition != null)
            {
                return satisfiedCondition.Execute(Configuration);
            }

            return null;
        }
    }
}