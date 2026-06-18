using System.Collections.Generic;
using System.Linq;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public class PO3_RunnerConfiguration
    {
        public double StopGrace { get; internal set; }
    }

    public class PO3_StrategyRunner
    {
        public PO3_RunnerConfiguration Configuration { get; private set; }

        private List<StrategyCondition<PO3_RunnerConfiguration>> conditions;

        public static PO3_StrategyRunner Configure(AlgoBase strategyBase, FractalManager fractalManager, PO3_RunnerConfiguration config)
        {
            var runner = new PO3_StrategyRunner()
            {
                Configuration = config,

                conditions =
                [

                ],
            };

            return runner;
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