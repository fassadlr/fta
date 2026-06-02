using System;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS.Conditions
{
    internal sealed class ShortReversalCondition : Condition
    {
        internal ShortReversalCondition(AlgoBase owner, FractalManager fractalManager) : base(owner, fractalManager)
        {
        }

        internal override bool IsSatisfied(RunnerConfiguration config)
        {
            if (config.RangeInitiator == TradeRangeInitiator.None || config.RangeInitiator == TradeRangeInitiator.RangeLowEngaged)
                return false;

            // This equates to the open of the 14:50 candle
            var timeCondition = NinjaScriptBase.ToTime(Owner.Time[0]) > ValidFromTime;
            if (!timeCondition)
            {
                Owner.PrintDebug("[SHORT_REVERSAL_INVALID] timeCondition");
                return false;
            }

            var closeInsideCondition = Owner.Close[0] < config.RangeHigh;
            if (!closeInsideCondition)
            {
                Owner.PrintDebug("[SHORT_REVERSAL_INVALID] closeInsideCondition");
                return false;
            }

            var candleEngulfCondition = Owner.CandleBodyRange(0) > Owner.CandleBodyRange(1) && Owner.CloseLower();
            if (!candleEngulfCondition)
            {
                Owner.PrintDebug("[SHORT_REVERSAL_INVALID] candleEngulfCondition");
                return false;
            }

            // VWAP must be below the range low.
            //var vwapCondition = config.VWAP[0] <= config.RangeLow;
            //if (!vwapCondition)
            //{
            //    Owner.PrintDebug($"[SHORT_REVERSAL_INVALID] vwapCondition {Math.Round(Owner.Close[0] - config.VWAP[0])}");
            //    return false;
            //}

            if (!CandleBodyRangeInPoints(1))
            {
                Owner.PrintDebug("[SHORT_REVERSAL_INVALID] CandleBodyRangeInPoints");
                return false;
            }

            Owner.PrintDebug("[SHORT_REVERSAL_SATISFIED]");

            return true;
        }

        internal override TradeExecutionDetails Execute(RunnerConfiguration settings)
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
                return TradeExecutionDetails.Failed();
            }

            var riskReward = CalculateRiskReward(stopDistance, Owner.Close[0] - settings.RangeLow);
            if (riskReward < RunnerConfiguration.RiskRewardMinimum)
            {
                Owner.PrintDebug($"[RR_SUBOPTIMAL_SHORT] RR {riskReward}");
                return TradeExecutionDetails.Failed();
            }

            var takeProfit = settings.RangeLow;
            if (riskReward >= settings.MaxRiskReward)
            {
                riskReward = settings.MaxRiskReward;
                takeProfit = Owner.Close[0] - (stopDistance * settings.MaxRiskReward);
                Owner.PrintDebug($"[RR_ADJUST_SHORT] RR {riskReward}");
            }

            return new()
            {
                Bias = TradeBias.Short,
                Entry = entry,
                IsLimit = settings.EntryPercentage > 0,
                Log = "SHORT_ENTRY_REVERSAL",
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