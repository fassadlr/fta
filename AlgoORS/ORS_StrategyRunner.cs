using System.Collections.Generic;
using System.Linq;
using NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS.Conditions;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS
{
    public class ORS_StrategyRunner
    {
        public ORS_RunnerConfiguration Configuration { get; private set; }

        private List<StrategyCondition<ORS_RunnerConfiguration>> conditions;

        public static ORS_StrategyRunner Configure(AlgoBase strategyBase, FractalManager fractalManager, ORS_RunnerConfiguration config)
        {
            var runner = new ORS_StrategyRunner()
            {
                Configuration = config,

                conditions =
                [
                    new LongContinuation(strategyBase, fractalManager),
                    new ShortContinuation(strategyBase, fractalManager),
                    new LongReversalCondition(strategyBase, fractalManager),
                    new ShortReversalCondition(strategyBase, fractalManager),
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