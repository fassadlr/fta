using System;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS
{
    internal sealed class LongReversalCondition : Condition
    {
        internal LongReversalCondition(AlgoBase owner, FractalManager fractalManager) : base(owner, fractalManager)
        {
            ValidFromTime = 144500;
        }

        internal override bool IsSatisfied(ExecutionCriteria criteria)
        {
            if (criteria.RangeInitiator == TradeRangeInitiator.None)
                return false;

            if (ValidFromTime <= NinjaScriptBase.ToTime(Owner.Time[0]))
                return false;

            if (criteria.RangeInitiator != TradeRangeInitiator.RangeLowEngaged)
                return false;

            if (Owner.Close[0] < criteria.RangeLow)
                return false;

            if (!CandleBodyRangeInPoints(1))
                return false;

            Owner.PrintDebug("[LONG_REVERSAL_SATISFIED]");

            return true;
        }

        internal override TradeExecutionResult Execute(ExecutionCriteria criteria)
        {
            var stopLoss = FractalManager.RecentLow();
            var stopDistance = Owner.Close[0] - stopLoss.Price;
            double entry = 0;

            if (criteria.EntryPercentage > 0)
            {
                entry = Math.Round(Owner.Close[0] - (stopDistance * criteria.EntryPercentage), 2);
                stopDistance = entry - stopLoss.Price;
                Owner.PrintDebug($"[LIMIT_ADJUST_LONG] stop distance {stopDistance}, entry {entry}");
            }

            var takeProfit = criteria.RangeHigh;
            var size = CalculatePositionSize(stopDistance);
            if (size < 0.75)
            {
                Owner.PrintDebug($"[POSITION_SUBOPTIMAL_LONG] Position {size}, Stop {stopLoss}");
                return TradeExecutionResult.Failed();
            }

            var rr = CalculateRiskReward(stopDistance, criteria.RangeHigh - Owner.Close[0]);
            if (rr < 0.5)
            {
                Owner.PrintDebug($"[RR_SUBOPTIMAL_LONG] RR {rr}");
                return TradeExecutionResult.Failed();
            }

            if (rr >= criteria.MaxRiskReward)
            {
                Owner.PrintDebug($"[RR_ADJUST_LONG] RR {rr} to {criteria.MaxRiskReward}");
                rr = criteria.MaxRiskReward;
                takeProfit = Owner.Close[0] + (stopDistance * criteria.MaxRiskReward);
            }

            return EnterLongTrade("LONG_ENTRY_REVERSAL", criteria.EntryPercentage > 0, criteria.RangeInitiator, TradeType.Reversal, (int)size, rr, stopLoss.Price, takeProfit, entry);
        }
    }
}
