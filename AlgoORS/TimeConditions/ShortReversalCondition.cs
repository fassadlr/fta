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

        internal override bool IsSatisfied(ExecutionCriteria criteria)
        {
            if (criteria.RangeInitiator == TradeRangeInitiator.None || criteria.RangeInitiator == TradeRangeInitiator.RangeHighEngaged)
                return false;

            if (ValidFromTime <= NinjaScriptBase.ToTime(Owner.Time[0]))
                return false;

            if (Owner.Close[0] > criteria.RangeHigh)
                return false;

            if (!CandleBodyRangeInPoints(1))
                return false;

            Owner.PrintDebug("[SHORT_REVERSAL_SATISFIED]");

            return true;
        }

        internal override TradeExecutionResult Execute(ExecutionCriteria criteria)
        {
            var stopLoss = FractalManager.RecentHigh();
            var stopDistance = stopLoss.Price - Owner.Close[0];
            double entry = 0;

            if (criteria.EntryPercentage > 0)
            {
                entry = Math.Round(Owner.Close[0] + (stopDistance * criteria.EntryPercentage), 2);
                stopDistance = Math.Round(stopLoss.Price - entry, 2);
                Owner.PrintDebug($"[LIMIT_ADJUST_SHORT] stop distance {stopDistance}, entry {entry}");
            }

            var size = CalculatePositionSize(stopDistance);
            if (size < 0.75)
            {
                Owner.PrintDebug($"[POSITION_SUBOPTIMAL_SHORT] Position {size}, Stop {stopLoss}");
                return TradeExecutionResult.Failed();
            }

            var rr = CalculateRiskReward(stopDistance, Owner.Close[0] - criteria.RangeLow);
            if (rr < 0.5)
            {
                Owner.PrintDebug($"[RR_SUBOPTIMAL_SHORT] RR {rr}");
                return TradeExecutionResult.Failed();
            }

            var takeProfit = criteria.RangeLow;
            if (rr >= criteria.MaxRiskReward)
            {
                rr = criteria.MaxRiskReward;
                takeProfit = Owner.Close[0] - (stopDistance * criteria.MaxRiskReward);
                Owner.PrintDebug($"[RR_ADJUST_SHORT] RR {rr}");
            }

            return EnterShortTrade("SHORT_ENTRY_REVERSAL", criteria.EntryPercentage > 0, criteria.RangeInitiator, TradeType.Reversal, (int)size, rr, stopLoss.Price, takeProfit, entry);
        }
    }
}