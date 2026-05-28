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

        internal override bool IsSatisfied(StrategySettings criteria)
        {
            if (criteria.RangeInitiator == TradeRangeInitiator.None || criteria.RangeInitiator == TradeRangeInitiator.RangeHighEngaged)
                return false;

            // This equates to the open of the 14:50 candle
            var timeCondition = NinjaScriptBase.ToTime(Owner.Time[0]) > ValidFromTime;
            if (!timeCondition)
            {
                Owner.PrintDebug("[LONG_REVERSAL_NOT_VALID] time condition");
                return false;
            }

            var closeInsideCondition = Owner.Close[0] > criteria.RangeLow;
            if (!closeInsideCondition)
            {
                Owner.PrintDebug("[LONG_REVERSAL_NOT_VALID] close inside condition");
                return false;
            }

            var candleEngulfCondition = CandleBodyRange(0) > CandleBodyRange(1) && CloseHigher();
            if (!candleEngulfCondition)
            {
                Owner.PrintDebug("[LONG_REVERSAL_NOT_VALID] candle engulf condition");
                return false;
            }

            if (!CandleBodyRangeInPoints(1))
            {
                Owner.PrintDebug("[LONG_REVERSAL_NOT_VALID] candle body range condition");
                return false;
            }

            Owner.PrintDebug("[LONG_REVERSAL_SATISFIED]");

            return true;
        }

        internal override TradeExecutionResult Execute(StrategySettings settings)
        {
            var stopLoss = FractalManager.RecentLow();
            var stopDistance = Owner.Close[0] - stopLoss.Price;
            double entry = 0;

            if (settings.EntryPercentage > 0)
            {
                entry = Math.Round(Owner.Close[0] - (stopDistance * settings.EntryPercentage), 2);
                stopDistance = entry - stopLoss.Price;
                Owner.PrintDebug($"[LIMIT_ADJUST_LONG] stop distance {stopDistance}, entry {entry}");
            }

            var takeProfit = settings.RangeHigh;
            var positionSize = Owner.CalculatePositionSize(stopDistance, settings.Risk);
            if (positionSize < 0.75)
            {
                Owner.PrintDebug($"[POSITION_SUBOPTIMAL_LONG] Position {positionSize}, Stop {stopLoss}");
                return TradeExecutionResult.Failed();
            }

            var rr = CalculateRiskReward(stopDistance, settings.RangeHigh - Owner.Close[0]);
            if (rr < 0.5)
            {
                Owner.PrintDebug($"[RR_SUBOPTIMAL_LONG] RR {rr}");
                return TradeExecutionResult.Failed();
            }

            if (rr >= settings.MaxRiskReward)
            {
                Owner.PrintDebug($"[RR_ADJUST_LONG] RR {rr} to {settings.MaxRiskReward}");
                rr = settings.MaxRiskReward;
                takeProfit = Owner.Close[0] + (stopDistance * settings.MaxRiskReward);
            }

            return EnterLongTrade("LONG_ENTRY_REVERSAL", settings.EntryPercentage > 0, settings.RangeInitiator, TradeType.Reversal, positionSize, rr, stopLoss.Price, takeProfit, entry);
        }
    }
}
