using NinjaTrader.NinjaScript.Indicators;

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

    public class ORS_RunnerConfiguration
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
}
