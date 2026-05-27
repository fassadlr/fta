using System;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS
{
    internal sealed class ShortReversalCondition : Condition
    {
        internal ShortReversalCondition(AlgoBase owner, FractalManager fractalManager) : base(owner, fractalManager)
        {
            ValidFromTime = 144500;
        }

        internal override bool IsSatisfied(StrategySettings criteria)
        {
            if (criteria.RangeInitiator == TradeRangeInitiator.None || criteria.RangeInitiator == TradeRangeInitiator.RangeLowEngaged)
                return false;

            // This equates to the open of the 14:50 candle
            var timeCondition = NinjaScriptBase.ToTime(Owner.Time[0]) > ValidFromTime;
            if (!timeCondition)
                return false;

            var closeInsideCondition = Owner.Close[0] < criteria.RangeHigh;
            if (!closeInsideCondition)
                return false;

            var candleEngulfCondition = CandleBodyRange(0) > CandleBodyRange(1) && CloseLower();
            if (!candleEngulfCondition)
                return false;

            if (!CandleBodyRangeInPoints(1))
                return false;

            Owner.PrintDebug("[SHORT_REVERSAL_SATISFIED]");

            return true;
        }

        internal override TradeExecutionResult Execute(StrategySettings settings)
        {
            var stopLoss = FractalManager.RecentHigh();
            var stopDistance = stopLoss.Price - Owner.Close[0];
            double entry = 0;

            if (settings.EntryPercentage > 0)
            {
                entry = Math.Round(Owner.Close[0] + (stopDistance * settings.EntryPercentage), 2);
                stopDistance = Math.Round(stopLoss.Price - entry, 2);
                Owner.PrintDebug($"[LIMIT_ADJUST_SHORT] stop distance {stopDistance}, entry {entry}");
            }

            var positionSize = Owner.CalculatePositionSize(stopDistance, settings.Risk);
            if (positionSize < 0.75)
            {
                Owner.PrintDebug($"[POSITION_SUBOPTIMAL_SHORT] Position {positionSize}, Stop {stopLoss}");
                return TradeExecutionResult.Failed();
            }

            var rr = CalculateRiskReward(stopDistance, Owner.Close[0] - settings.RangeLow);
            if (rr < 0.5)
            {
                Owner.PrintDebug($"[RR_SUBOPTIMAL_SHORT] RR {rr}");
                return TradeExecutionResult.Failed();
            }

            var takeProfit = settings.RangeLow;
            if (rr >= settings.MaxRiskReward)
            {
                rr = settings.MaxRiskReward;
                takeProfit = Owner.Close[0] - (stopDistance * settings.MaxRiskReward);
                Owner.PrintDebug($"[RR_ADJUST_SHORT] RR {rr}");
            }

            return EnterShortTrade("SHORT_ENTRY_REVERSAL", settings.EntryPercentage > 0, settings.RangeInitiator, TradeType.Reversal, positionSize, rr, stopLoss.Price, takeProfit, entry);
        }
    }
}