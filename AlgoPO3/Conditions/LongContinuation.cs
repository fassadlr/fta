using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3.Conditions
{
    internal class LongContinuation : StrategyCondition<RunnerConfiguration_PO3>
    {
        internal LongContinuation(AlgoBase owner, FractalManager fractalManager) : base(owner, fractalManager)
        {
        }

        internal override bool IsSatisfied(RunnerConfiguration_PO3 config)
        {
            if (config.RangeBias != RangeBias.Bullish)
                return false;

            // Is previous range covered
            var previousRange = config.Ranges.Previous(Owner.Close[0], true);
            if (previousRange != null && previousRange.CoveredPercentage <= 90)
            {
                Owner.PrintDebug("[LONG_CONTINUATION_RANGE_NOT_COVERED]");
                return false;
            }

            // Has IMB formed
            if (!Owner.ImbalanceFormed(TradeBias.Long, config.Ranges.Current(Owner.Close[0])))
            {
                Owner.PrintDebug("[LONG_CONTINUATION_IMB_MISSING]");
                return false;
            }

            // Did the IMB break above the last fractal high
            if (Owner.Close[0] < FractalManager.Fractals[(1, FractalType.HighFiveBar)].Price)
            {
                Owner.PrintDebug("[LONG_CONTINUATION_IMB_CLOSE_HIGHER_FAIL]");
                return false;
            }

            Owner.PrintDebug("[LONG_CONTINUATION_SATISFIED]");

            return true;
        }

        internal override TradeDetails Execute(RunnerConfiguration_PO3 config)
        {
            var stopLoss = config.Ranges.Current(Owner.Close[0]).ExtLow;

            // TODO: use fractal stop?

            var stopDistance = Owner.Low[0] - stopLoss;
            var positionSize = Owner.CalculatePositionSize(stopDistance, config.Risk);
            if (positionSize < 1)
            {
                Owner.PrintDebug($"[POSITION_SUBOPTIMAL_LONG__CONTINUATION] Position {positionSize}, Stop {stopLoss}");
                return TradeDetails.Failed();
            }

            var profitTarget = config.Ranges.Current(Owner.Close[0]).Eq;

            var riskReward = Owner.CalculateRiskReward(stopDistance, profitTarget - Owner.Low[0]);
            if (riskReward < 0.5)
            {
                Owner.PrintDebug($"[RR_SUBOPTIMAL_LONG_CONTINUATION] Risk-Reward {riskReward}");
                return TradeDetails.Failed();
            }

            return new()
            {
                Entry = Owner.Low[0],
                Bias = TradeBias.Long,
                IsLimit = true,
                Log = "LONG_ENTRY_CONTINUATION",
                PositionSize = positionSize,
                RiskReward = 1,
                OriginalStopLoss = stopLoss,
                StopLoss = stopLoss,
                TakeProfit = profitTarget,
                TradeMissedThreshold = profitTarget,
                BreakEvenThreshold = config.Ranges.Current(Owner.Close[0]).MB1,
                Type = TradeType.Continuation,
            };
        }
    }
}