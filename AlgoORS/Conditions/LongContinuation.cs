using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS.Conditions
{
    internal class LongContinuation : Condition
    {
        internal LongContinuation(AlgoBase owner, FractalManager fractalManager) : base(owner, fractalManager)
        {
        }

        internal override bool IsSatisfied(RunnerConfiguration settings)
        {
            if (settings.RangeInitiator == TradeRangeInitiator.None || settings.RangeInitiator == TradeRangeInitiator.RangeLowEngaged)
                return false;

            // This equates to looking back at when the 14:55 candle opens.
            var timeCondition = NinjaScriptBase.ToTime(Owner.Time[0]) >= Owner.AddMinutesToTime(ValidFromTime, 5);
            if (!timeCondition)
                return false;

            var candleCondition =
                Owner.BullishCandle(0) && Owner.Close[0] > settings.RangeHigh &&
                Owner.BullishCandle(1) && Owner.Close[1] > settings.RangeHigh;
            if (!candleCondition)
                return false;

            // Either candle's must be at least a given percentage of the candle range
            var candleBodyCondition = Owner.BodyCandle(0, settings.CandleBodyPercentage) || Owner.BodyCandle(1, settings.CandleBodyPercentage);
            if (!candleBodyCondition)
                return false;

            Owner.PrintDebug("[LONG_CONTINUATION_SATISFIED]");

            return true;
        }

        internal override TradeExecutionDetails Execute(RunnerConfiguration settings)
        {
            var stopLoss = Owner.Low[0] - settings.StopGrace;
            var stopDistance = (Owner.Close[0] - Owner.Low[0]) + settings.StopGrace;
            var positionSize = Owner.CalculatePositionSize(stopDistance, settings.Risk);
            if (positionSize < 0.75)
            {
                Owner.PrintDebug($"[POSITION_SUBOPTIMAL_LONG] Position {positionSize}, Stop {stopLoss}");
                return TradeExecutionDetails.Failed();
            }

            var profitTarget = Owner.Close[0] + (stopDistance * settings.TPMultiplier);

            return new()
            {
                Bias = TradeBias.Long,
                IsLimit = false,
                Log = "LONG_ENTRY_CONTINUATION",
                PositionSize = positionSize,
                RangeInitiator = settings.RangeInitiator,
                RiskReward = 1,
                StopLoss = stopLoss,
                TakeProfit = profitTarget,
                Type = TradeType.Continuation,
            };
        }
    }
}