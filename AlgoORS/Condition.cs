using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS
{
    public enum TradeRangeInitiator
    {
        None,
        RangeHighEngaged,
        RangeLowEngaged,
        ContinueAboveHigh,
        ContinueBelowLow
    }

    public class RunnerConfiguration
    {
        public double EntryPercentage { get; set; }
        public double MaxRiskReward { get; set; }
        public double RangeHigh { get; set; }
        public double RangeLow { get; set; }
        public TradeRangeInitiator RangeInitiator { get; set; }
        public double CandleBodyPercentage { get; set; }
        public double StopGrace { get; internal set; }
        public double TPMultiplier { get; internal set; }
        public int Risk { get; internal set; }
        public OrderFlowVWAP VWAP { get; internal set; }

        public const int VWAPDistanceThreshold = 100;
        public const double VWAPRetracementThreshold = 0.25;
        public const double RiskRewardMinimum = 0.5;
    }

    internal abstract class Condition(AlgoBase owner, FractalManager fractalManager)
    {
        protected readonly AlgoBase Owner = owner;
        protected readonly FractalManager FractalManager = fractalManager;

        internal int ValidFromTime { get; set; } = 144500;

        internal bool Disabled { get; set; }
        internal bool ExecuteOnlyMe { get; set; }

        internal abstract bool IsSatisfied(RunnerConfiguration settings);
        internal abstract TradeExecutionDetails Execute(RunnerConfiguration settings);

        internal bool CandleBodyRangeInPoints(int minPoints)
        {
            return Owner.Open[0] - Owner.Close[0] >= minPoints || Owner.Close[0] - Owner.Open[0] >= minPoints;
        }
    }
}
