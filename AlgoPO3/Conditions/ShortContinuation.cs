using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3.Conditions
{
    internal class ShortContinuation : StrategyCondition<RunnerConfiguration_PO3>
    {
        internal ShortContinuation(AlgoBase owner, FractalManager fractalManager) : base(owner, fractalManager)
        {
        }

        internal override bool IsSatisfied(RunnerConfiguration_PO3 config)
        {
            if (config.RangeBias != RangeBias.Bearish)
                return false;

            // Is previous range covered
            var previousRange = config.Ranges.Previous(Owner.Close[0], false);
            if (previousRange != null && previousRange.CoveredPercentage <= 90)
            {
                //Owner.PrintDebug("[SHORT_CONTINUATION_RANGE_NOT_COVERED]");
                return false;
            }

            // Has IMB formed
            if (!Owner.ImbalanceFormed(TradeBias.Short, config.Ranges.Current(Owner.Close[0])))
            {
                return false;
            }

            // Did the IMB break below the last fractal low
            if (Owner.Close[0] > FractalManager.Fractals[(1, FractalType.LowFiveBar)].Price)
            {
                Owner.PrintDebug("[SHORT_CONTINUATION_IMB_CLOSE_LOWER_FAIL]");
                return false;
            }

            Owner.PrintDebug("[SHORT_CONTINUATION_SATISFIED]");

            return true;
        }

        internal override TradeDetails Execute(RunnerConfiguration_PO3 config)
        {
            var stopLoss = config.Ranges.Current(Owner.Close[0]).ExtHigh;

            // TODO: use fractal stop?

            var stopDistance = stopLoss - Owner.High[0];
            var positionSize = Owner.CalculatePositionSize(stopDistance, config.Risk);
            if (positionSize < 1)
            {
                Owner.PrintDebug($"[POSITION_SUBOPTIMAL_SHORT_CONTINUATION] Position {positionSize}, Stop {stopLoss}");
                return TradeDetails.Failed();
            }

            var profitTarget = config.Ranges.Current(Owner.Close[0]).Eq;

            var riskReward = Owner.CalculateRiskReward(stopDistance, Owner.High[0] - profitTarget);
            if (riskReward < 0.5)
            {
                Owner.PrintDebug($"[RR_SUBOPTIMAL_SHORT_CONTINUATION] Risk-Reward {riskReward}");
                return TradeDetails.Failed();
            }

            return new()
            {
                Entry = Owner.High[0],
                Bias = TradeBias.Short,
                IsLimit = true,
                Log = "SHORT_CONTINUATION_ENTRY",
                PositionSize = positionSize,
                RiskReward = riskReward,
                OriginalStopLoss = stopLoss,
                StopLoss = stopLoss,
                TakeProfit = profitTarget,
                TradeMissedThreshold = profitTarget,
                BreakEvenThreshold = config.Ranges.Current(Owner.Close[0]).MB4,
                Type = TradeType.Continuation,
            };
        }
    }
}