using System;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS.Conditions
{
    internal class ShortContinuationVWAP_Retrace : Condition
    {
        private double lowestCloseSinceOpen = 0;
        private bool openingCandlesCondition;

        internal ShortContinuationVWAP_Retrace(AlgoBase owner, FractalManager fractalManager)
            : base(owner, fractalManager)
        {
        }

        internal override bool IsSatisfied(RunnerConfiguration config)
        {
            if (Disabled) return false;

            // Track value from the open.
            if (lowestCloseSinceOpen == 0 || Owner.Low[0] < lowestCloseSinceOpen)
                lowestCloseSinceOpen = Owner.Low[0];

            var rangeCondition = config.RangeInitiator == TradeRangeInitiator.RangeLowEngaged;
            if (!rangeCondition)
                return false;

            // This equates to looking back at when the 14:45 candle opens.
            var timeCondition = NinjaScriptBase.ToTime(Owner.Time[0]) >= ValidFromTime;
            if (!timeCondition)
            {
                Owner.PrintDebug("[SHORT_CONTINUATION_VWAP_RETRACE_INVALID] timeCondition");
                return false;
            }

            // If this is false, this condition will be permanently disabled for the rest of the day.
            if (!openingCandlesCondition)
            {
                openingCandlesCondition =
                    Owner.BearishCandle(2) && Owner.Close[2] < config.RangeLow &&
                    Owner.BearishCandle(1) && Owner.Close[1] < config.RangeLow &&
                    Owner.BearishCandle(0) && Owner.Close[0] < config.RangeLow;
            }

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

            // All other strategies need to wait for this condition to finish.
            ExecuteOnlyMe = true;

            var vwapDistance = Math.Round(config.VWAP[0] - Owner.Close[0], 2);
            var vwapDistanceCondition = vwapDistance <= RunnerConfiguration.VWAPDistanceThreshold;
            if (!vwapDistanceCondition)
            {
                Owner.PrintDebug($"[SHORT_CONTINUATION_VWAP_RETRACE_INVALID] vwapDistanceCondition {vwapDistance}");
                if (NinjaScriptBase.ToTime(Owner.Time[0]) == ValidFromTime)
                {
                    Owner.PrintDebug($"[SHORT_CONTINUATION_VWAP_RETRACE_INVALID] first candle vwapDistanceCondition {vwapDistance}");
                    return false;
                }
            }

            var vwapRetracement = Math.Round((Owner.High[0] - lowestCloseSinceOpen) / (config.VWAP[0] - lowestCloseSinceOpen), 2);
            var vwapRetracementCondition = vwapRetracement >= RunnerConfiguration.VWAPRetracementThreshold;
            if (!vwapRetracementCondition)
            {
                Owner.PrintDebug($"[SHORT_CONTINUATION_VWAP_RETRACE_INVALID] vwapRetracementCondition - lowestCloseSinceOpen: {lowestCloseSinceOpen}");
            }

            if (!vwapDistanceCondition && !vwapRetracementCondition)
                return false;

            Owner.PrintDebug($"[SHORT_CONTINUATION_VWAP_RETRACE_SATISFIED] VWAP distance {vwapDistanceCondition} distance {vwapDistance} VWAP retracement {vwapRetracementCondition} - {vwapRetracement} - lowestCloseSinceOpen: {lowestCloseSinceOpen}");

            return true;
        }

        internal override TradeExecutionDetails Execute(RunnerConfiguration config)
        {
            var stopLoss = Owner.High[0] + config.StopGrace;
            var stopDistance = Owner.High[0] - Owner.Close[0] + config.StopGrace;
            var positionSize = Owner.CalculatePositionSize(stopDistance, config.Risk);
            if (positionSize < 0.75)
            {
                Owner.PrintDebug($"[POSITION_SUBOPTIMAL_SHORT] Position {positionSize}, Stop {stopLoss}");
                return TradeExecutionDetails.Failed();
            }

            var profitTarget = Owner.Close[0] - (stopDistance * config.TPMultiplier);

            ExecuteOnlyMe = false;

            return new()
            {
                Bias = TradeBias.Short,
                IsLimit = false,
                Log = "SHORT_CONTINUATION_VWAP_RETRACE",
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
