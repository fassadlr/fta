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

        public static StrategyRunner Configure(AlgoBase strategyBase, FractalManager fractalManager, RunnerConfiguration settings)
        {
            var runner = new StrategyRunner()
            {
                Configuration = settings,

                longContinuation = new LongContinuation(strategyBase, fractalManager),
                shortContinuation = new ShortContinuation(strategyBase, fractalManager),
                longReversal = new LongReversalCondition(strategyBase, fractalManager),
                shortReversal = new ShortReversalCondition(strategyBase, fractalManager),

                longContination_VWAP_Retrace = new LongContinuationVWAP_Retrace(strategyBase, fractalManager)
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
            if (longReversal.IsSatisfied(Configuration))
            {
                return longReversal.Execute(Configuration);
            }

            if (shortReversal.IsSatisfied(Configuration))
            {
                return shortReversal.Execute(Configuration);
            }

            if (longContinuation.IsSatisfied(Configuration))
            {
                return longContinuation.Execute(Configuration);
            }

            if (shortContinuation.IsSatisfied(Configuration))
            {
                return shortContinuation.Execute(Configuration);
            }

            if (longContination_VWAP_Retrace.IsSatisfied(Configuration))
            {
                return longContination_VWAP_Retrace.Execute(Configuration);
            }

            return null;
        }
    }
}