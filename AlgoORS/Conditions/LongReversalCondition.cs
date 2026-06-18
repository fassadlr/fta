using System;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS.Conditions
{
    internal sealed class LongReversalCondition : StrategyCondition<RunnerConfiguration_ORS>
    {
        internal LongReversalCondition(AlgoBase owner, FractalManager fractalManager) : base(owner, fractalManager)
        {
            ValidFromTime = 144500;
        }

        internal override bool IsSatisfied(RunnerConfiguration_ORS config)
        {
            if (config.RangeInitiator == TradeRangeInitiator.None || config.RangeInitiator == TradeRangeInitiator.RangeHighEngaged)
                return false;

            // This equates to the open of the 14:50 candle
            var timeCondition = NinjaScriptBase.ToTime(Owner.Time[0]) > ValidFromTime;
            if (!timeCondition)
            {
                Owner.PrintDebug("[LONG_REVERSAL_NOT_VALID] time condition");
                return false;
            }

            var closeInsideCondition = Owner.Close[0] > config.RangeLow;
            if (!closeInsideCondition)
            {
                Owner.PrintDebug("[LONG_REVERSAL_NOT_VALID] close inside condition");
                return false;
            }

            var candleEngulfCondition = Owner.CandleBodyRange(0) > Owner.CandleBodyRange(1) && Owner.CloseHigher();
            if (!candleEngulfCondition)
            {
                Owner.PrintDebug("[LONG_REVERSAL_NOT_VALID] candle engulf condition");
                return false;
            }

            // Cant long towards the VWAP.
            //var vwapCondition = config.VWAP[0] <= Owner.Close[0];
            //if (!vwapCondition)
            //{
            //    Owner.PrintDebug("[LONG_REVERSAL_INVALID] vwapCondition");
            //    return false;
            //}

            if (!CandleBodyRangeInPoints(1))
            {
                Owner.PrintDebug("[LONG_REVERSAL_NOT_VALID] candle body range condition");
                return false;
            }

            Owner.PrintDebug("[LONG_REVERSAL_SATISFIED]");

            return true;
        }

        internal override TradeDetails Execute(RunnerConfiguration_ORS settings)
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
                return TradeDetails.Failed();
            }

            var riskReward = Owner.CalculateRiskReward(stopDistance, settings.RangeHigh - Owner.Close[0]);
            if (riskReward < RunnerConfiguration_ORS.RiskRewardMinimum)
            {
                Owner.PrintDebug($"[RR_SUBOPTIMAL_LONG] RR {riskReward}");
                return TradeDetails.Failed();
            }

            if (riskReward >= settings.MaxRiskReward)
            {
                Owner.PrintDebug($"[RR_ADJUST_LONG] RR {riskReward} to {settings.MaxRiskReward}");
                riskReward = settings.MaxRiskReward;
                takeProfit = Owner.Close[0] + (stopDistance * settings.MaxRiskReward);
            }

            return new()
            {
                Bias = TradeBias.Long,
                Entry = entry,
                IsLimit = settings.EntryPercentage > 0,
                Log = "LONG_ENTRY_REVERSAL",
                PositionSize = positionSize,
                RangeInitiator = settings.RangeInitiator,
                RiskReward = riskReward,
                StopLoss = stopLoss.Price,
                TakeProfit = takeProfit,
                Type = TradeType.Reversal,
            };
        }
    }
}
