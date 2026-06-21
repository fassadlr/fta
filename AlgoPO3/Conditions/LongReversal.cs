using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3.Conditions
{
    internal class LongReversal : StrategyCondition<RunnerConfiguration_PO3>
    {
        internal LongReversal(AlgoBase owner, FractalManager fractalManager) : base(owner, fractalManager)
        {
        }

        internal override bool IsSatisfied(RunnerConfiguration_PO3 config)
        {
            if (config.RangeBias != RangeBias.Bullish)
                return false;

            // Is previous range covered or not
            var previousRange = config.Ranges.Previous(Owner.Close[0], true);
            if (previousRange != null && previousRange.CoveredPercentage >= 90)
            {
                Owner.PrintDebug("[LONG_REVERSAL_RANGE_COVERED]");
                return false;
            }

            // Has 1min BOS occurred (5bar)
            if (!config.BullishBOSDetected)
            {
                Owner.PrintDebug("[LONG_REVERSAL_BOS_NOT_DETECTED]");
                return false;
            }

            // Has IMB formed
            if (!Owner.ImbalanceFormed(TradeBias.Long, config.Ranges.Current(Owner.Close[0])))
            {
                //Owner.PrintDebug("[LONG_REVERSAL_IMB_NOT_FORMED]");
                return false;
            }

            Owner.PrintDebug("[LONG_REVERSAL_SATISFIED]");

            return true;
        }

        internal override TradeDetails Execute(RunnerConfiguration_PO3 config)
        {
            var stopLoss = config.Ranges.Current(Owner.Close[0]).ExtLow;

            if (FractalManager.Fractals.TryGetValue((1, FractalType.LowFiveBar), out var lowFiveBarFractal))
            {
                if (lowFiveBarFractal.Price < stopLoss && lowFiveBarFractal.Price > config.Ranges.Current(lowFiveBarFractal.Price).BR4)
                {
                    stopLoss = lowFiveBarFractal.Price;
                    Owner.PrintDebug($"[LONG_REVERSAL_FRACTAL_STOP] ext low: {config.Ranges.Current(Owner.Close[0]).ExtLow}, fractal {lowFiveBarFractal.Price}");
                }
            }

            var stopDistance = Owner.Low[0] - stopLoss;// + config.StopGrace;
            var positionSize = Owner.CalculatePositionSize(stopDistance, config.Risk);
            if (positionSize < 1)
            {
                Owner.PrintDebug($"[POSITION_SUBOPTIMAL_LONG_REVERSAL] Position {positionSize}, Stop {stopLoss}");
                return TradeDetails.Failed();
            }

            var profitTarget = config.Ranges.Current(Owner.Close[0]).Eq;

            var riskReward = Owner.CalculateRiskReward(stopDistance, profitTarget - Owner.Low[0]);
            if (riskReward < 0.5)
            {
                Owner.PrintDebug($"[RR_SUBOPTIMAL_LONG_REVERSAL] Risk-Reward {riskReward}");
                return TradeDetails.Failed();
            }

            return new()
            {
                Entry = Owner.Low[0],
                Bias = TradeBias.Long,
                IsLimit = true,
                Log = "LONG_REVERSAL_ENTRY",
                PositionSize = positionSize,
                RiskReward = riskReward,
                OriginalStopLoss = stopLoss,
                StopLoss = stopLoss,
                TakeProfit = profitTarget,
                BreakEvenThreshold = config.Ranges.Current(Owner.Close[0]).MB1,
                TradeMissedThreshold = profitTarget,
                Type = TradeType.Reversal,
            };
        }
    }
}