using System;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS.Conditions
{
    internal class LongContinuationVWAP_Retrace : Condition
    {
        private double highestCloseSinceOpen = 0;
        private const int VWAPDistanceThreshold = 100;
        private const double VWAPRetracementThreshold = 0.25;

        internal LongContinuationVWAP_Retrace(AlgoBase owner, FractalManager fractalManager)
            : base(owner, fractalManager)
        {
        }

        internal override bool IsSatisfied(RunnerConfiguration config)
        {
            if (Disabled) return false;

            var rangeCondition = config.RangeInitiator == TradeRangeInitiator.RangeHighEngaged;
            if (!rangeCondition)
                return false;

            // Track value from the open.
            if (highestCloseSinceOpen == 0 || Owner.High[0] > highestCloseSinceOpen)
                highestCloseSinceOpen = Owner.High[0];

            // This equates to looking back at when the 14:45 candle opens.
            var timeCondition = NinjaScriptBase.ToTime(Owner.Time[0]) >= ValidFromTime;
            if (!timeCondition)
            {
                Owner.PrintDebug("[LONG_CONTINUATION_VWAP_RETRACE_INVALID] timeCondition");
                return false;
            }

            // If this is false, this condition will be permanently disabled for the rest of the day.
            var openingCandlesCondition =
                Owner.BullishCandle(2) && Owner.Close[2] > config.RangeHigh &&
                Owner.BullishCandle(1) && Owner.Close[1] > config.RangeHigh &&
                Owner.BullishCandle(0) && Owner.Close[0] > config.RangeHigh;

            if (!openingCandlesCondition)
            {
                Owner.PrintDebug("[LONG_CONTINUATION_VWAP_RETRACE_INVALID] openingCandlesCondition - disabled");
                Disabled = true;

                return false;
            }

            // If this is false, this condition will be permanently disabled for the rest of the day.
            var openingVWAPCondition = Owner.Close[0] > config.VWAP[0];
            if (!openingVWAPCondition)
            {
                Owner.PrintDebug("[LONG_CONTINUATION_VWAP_RETRACE_INVALID] openingVWAPCondition - disabled");
                Disabled = true;

                return false;
            }

            // All other strategies needs to wait for this condition to finish.
            ExecuteOnlyMe = true;

            var vwapDistanceCondition = Owner.Close[0] - config.VWAP[0] <= VWAPDistanceThreshold;
            if (!vwapDistanceCondition)
            {
                Owner.PrintDebug("[LONG_CONTINUATION_VWAP_RETRACE_INVALID] vwapDistanceCondition");
            }

            var vwapRetracement = Math.Round((highestCloseSinceOpen - Owner.Low[0]) / (highestCloseSinceOpen - config.VWAP[0]), 2);
            var vwapRetracementCondition = vwapRetracement >= VWAPRetracementThreshold;
            if (!vwapRetracementCondition)
            {
                Owner.PrintDebug("[LONG_CONTINUATION_VWAP_RETRACE_INVALID] vwapRetracementCondition");
            }

            if (!vwapDistanceCondition && !vwapRetracementCondition)
                return false;

            Owner.PrintDebug($"[LONG_CONTINUATION_VWAP_RETRACE_SATISFIED] VWAP distance {vwapDistanceCondition} VWAP retracement {vwapRetracementCondition} - {vwapRetracement}");

            return true;
        }

        internal override TradeExecutionDetails Execute(RunnerConfiguration config)
        {
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
