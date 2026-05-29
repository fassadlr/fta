using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS.Conditions
{
    internal class ShortContinuation : Condition
    {
        internal ShortContinuation(AlgoBase owner, FractalManager fractalManager) : base(owner, fractalManager)
        {
        }

        internal override bool IsSatisfied(RunnerConfiguration config)
        {
            var rangeCondition = config.RangeInitiator == TradeRangeInitiator.RangeLowEngaged;
            if (!rangeCondition)
                return false;

            // This equates to looking back at when the 14:55 candle opens.
            var timeCondition = NinjaScriptBase.ToTime(Owner.Time[0]) >= Owner.AddMinutesToTime(ValidFromTime, 5);
            if (!timeCondition)
            {
                Owner.PrintDebug("[SHORT_CONTINUATION_INVALID] time condition");
                return false;
            }

            var candleCondition =
                Owner.BearishCandle(0) && Owner.Close[0] < config.RangeLow &&
                Owner.BearishCandle(1) && Owner.Close[1] < config.RangeLow;
            if (!candleCondition)
            {
                Owner.PrintDebug("[SHORT_CONTINUATION_INVALID] candleCondition");
                return false;
            }

            // Either candle's must be at least a given percentage of the candle range
            var candleBodyCondition = Owner.BodyCandle(0, config.CandleBodyPercentage) || Owner.BodyCandle(1, config.CandleBodyPercentage);
            if (!candleBodyCondition)
            {
                Owner.PrintDebug("[SHORT_CONTINUATION_INVALID] candleBodyCondition");
                return false;
            }

            Owner.PrintDebug("[SHORT_CONTINUATION_SATISFIED]");

            return true;
        }

        internal override TradeExecutionDetails Execute(RunnerConfiguration settings)
        {
            var stopLoss = Owner.High[0] + settings.StopGrace;
            var stopDistance = Owner.High[0] - Owner.Close[0] + settings.StopGrace;
            var positionSize = Owner.CalculatePositionSize(stopDistance, settings.Risk);
            if (positionSize < 0.75)
            {
                Owner.PrintDebug($"[POSITION_SUBOPTIMAL_SHORT] Position {positionSize}, Stop {stopLoss}");
                return TradeExecutionDetails.Failed();
            }

            var profitTarget = Owner.Close[0] - (stopDistance * settings.TPMultiplier);

            return new()
            {
                Bias = TradeBias.Short,
                IsLimit = false,
                Log = "SHORT_ENTRY_CONTINUATION",
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
