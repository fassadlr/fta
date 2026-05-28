using System;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS.Conditions
{
    internal class ShortContinuationVWAP_Retrace : Condition
    {
        private double lowestCloseSinceOpen = 0;
        private const int VWAPDistanceThreshold = 100;
        private const double VWAPRetracementThreshold = 0.25;

        internal ShortContinuationVWAP_Retrace(AlgoBase owner, FractalManager fractalManager)
            : base(owner, fractalManager)
        {
        }

        internal override bool IsSatisfied(RunnerConfiguration config)
        {
            if (Disabled) return false;

            var rangeCondition = config.RangeInitiator == TradeRangeInitiator.RangeLowEngaged;
            if (!rangeCondition)
                return false;

            // Track value from the open.
            if (lowestCloseSinceOpen == 0 || Owner.Low[0] < lowestCloseSinceOpen)
                lowestCloseSinceOpen = Owner.Low[0];

            // This equates to looking back at when the 14:45 candle opens.
            var timeCondition = NinjaScriptBase.ToTime(Owner.Time[0]) >= ValidFromTime;
            if (!timeCondition)
            {
                Owner.PrintDebug("[SHORT_CONTINUATION_VWAP_RETRACE_INVALID] timeCondition");
                return false;
            }

            // If this is false, this condition will be permanently disabled for the rest of the day.
            var openingCandlesCondition =
                Owner.BearishCandle(2) && Owner.Close[2] < config.RangeLow &&
                Owner.BearishCandle(1) && Owner.Close[1] < config.RangeLow &&
                Owner.BearishCandle(0) && Owner.Close[0] < config.RangeLow;

            if (!openingCandlesCondition)
            {
                Owner.PrintDebug("[SHORT_CONTINUATION_VWAP_RETRACE_INVALID] openingCandlesCondition - disabled");
                Disabled = true;

                return false;
            }

            // If this is false, this condition will be permanently disabled for the rest of the day.
            var openingVWAPCondition = Owner.Close[0] < config.VWAP[0];
            if (!openingVWAPCondition)
            {
                Owner.PrintDebug("[SHORT_CONTINUATION_VWAP_RETRACE_INVALID] openingVWAPCondition - disabled");
                Disabled = true;

                return false;
            }

            // All other strategies needs to wait for this condition to finish.
            ExecuteOnlyMe = true;

            var vwapDistanceCondition = config.VWAP[0] - Owner.Close[0] <= VWAPDistanceThreshold;
            if (!vwapDistanceCondition)
            {
                Owner.PrintDebug("[SHORT_CONTINUATION_VWAP_RETRACE_INVALID] vwapDistanceCondition");
            }

            var vwapRetracement = Math.Round((Owner.Low[0] - lowestCloseSinceOpen) / (config.VWAP[0] - lowestCloseSinceOpen), 2);
            var vwapRetracementCondition = vwapRetracement >= VWAPRetracementThreshold;
            if (!vwapRetracementCondition)
            {
                Owner.PrintDebug("[SHORT_CONTINUATION_VWAP_RETRACE_INVALID] vwapRetracementCondition");
            }

            if (!vwapDistanceCondition && !vwapRetracementCondition)
                return false;

            Owner.PrintDebug($"[SHORT_CONTINUATION_VWAP_RETRACE_SATISFIED] VWAP distance {vwapDistanceCondition} VWAP retracement {vwapRetracementCondition} - {vwapRetracement}");

            return true;
        }

        internal override TradeExecutionDetails Execute(RunnerConfiguration config)
        {
            // TODO FIX
            var stopLoss = Owner.Low[0] - config.StopGrace;
            var stopDistance = (Owner.Close[0] - Owner.Low[0]) + config.StopGrace;
            var positionSize = Owner.CalculatePositionSize(stopDistance, config.Risk);
            if (positionSize < 0.75)
            {
                Owner.PrintDebug($"[POSITION_SUBOPTIMAL_LONG] Position {positionSize}, Stop {stopLoss}");
                return TradeExecutionDetails.Failed();
            }

            var profitTarget = Owner.Close[0] + (stopDistance * config.TPMultiplier);

            ExecuteOnlyMe = false;

            return new()
            {
                Bias = TradeBias.Long,
                IsLimit = false,
                Log = "LONG_ENTRY_CONTINUATION",
                PositionSize = positionSize,
                RangeInitiator = config.RangeInitiator,
                RiskReward = 1,
                StopLoss = stopLoss,
                TakeProfit = profitTarget,
                Type = TradeType.Continuation,
            };
        }
    }
}
