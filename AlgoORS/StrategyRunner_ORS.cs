using System.Collections.Generic;
using System.Linq;
using NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS.Conditions;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS
{
    public class StrategyRunner_ORS
    {
        public RunnerConfiguration_ORS Configuration { get; private set; }

        private List<StrategyCondition<RunnerConfiguration_ORS>> conditions;

        public static StrategyRunner_ORS Configure(AlgoBase strategyBase, FractalManager fractalManager, RunnerConfiguration_ORS config)
        {
            var runner = new StrategyRunner_ORS()
            {
                Configuration = config,

                conditions =
                [
                    new LongContinuation(strategyBase, fractalManager),
                    new ShortContinuation(strategyBase, fractalManager),
                    //new LongReversalCondition(strategyBase, fractalManager),
                    //new ShortReversalCondition(strategyBase, fractalManager),
                    new LongContinuationVWAP_Retrace(strategyBase, fractalManager),
                    new ShortContinuationVWAP_Retrace(strategyBase, fractalManager)
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

        public TradeDetails Run()
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