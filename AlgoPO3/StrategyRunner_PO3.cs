using System.Collections.Generic;
using System.Linq;
using NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3.Conditions;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public class RunnerConfiguration_PO3
    {
        public List<DealingRange> Ranges { get; internal set; }
        public int Risk { get; internal set; }
        public double StopGrace { get; internal set; }
        public RangeBias RangeBias { get; internal set; }
    }

    public class StrategyRunner_PO3
    {
        public RunnerConfiguration_PO3 Configuration { get; private set; }

        private List<StrategyCondition<RunnerConfiguration_PO3>> conditions;

        public static StrategyRunner_PO3 Configure(AlgoBase owner, FractalManager fractalManager, RunnerConfiguration_PO3 config)
        {
            var runner = new StrategyRunner_PO3()
            {
                Configuration = config,

                conditions =
                [
                    new LongContinuation(owner, fractalManager),
                    new LongReversal(owner, fractalManager),
                    new ShortReversal(owner, fractalManager),
                ],
            };

            return runner;
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