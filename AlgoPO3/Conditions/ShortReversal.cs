using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3.Conditions
{
    internal class ShortReversal : StrategyCondition<RunnerConfiguration_PO3>
    {
        internal ShortReversal(AlgoBase owner, FractalManager fractalManager) : base(owner, fractalManager)
        {
        }

        internal override bool IsSatisfied(RunnerConfiguration_PO3 config)
        {
            if (config.RangeBias != RangeBias.Bearish)
                return false;

            // Is previous range covered or not
            var previousRange = config.Ranges.Previous(Owner.Close[0], false);
            if (previousRange != null && previousRange.CoveredPercentage >= 90)
            {
                Owner.PrintDebug("[SHORT_REVERSAL_RANGE_COVERED]");
                return false;
            }

            // Has 1min BOS occurred (5bar)
            if (!config.BearishBOSDetected)
            {
                Owner.PrintDebug("[SHORT_REVERSAL_BOS_NOT_DETECTED]");
                return false;
            }

            // Has IMB formed
            if (!Owner.ImbalanceFormed(TradeBias.Short, config.Ranges.Current(Owner.Close[0])))
            {
                Owner.PrintDebug("[SHORT_REVERSAL_IMB_NOT_FORMED]");
                return false;
            }

            Owner.PrintDebug("[SHORT_REVERSAL_SATISFIED]");

            return true;
        }

        internal override TradeDetails Execute(RunnerConfiguration_PO3 config)
        {
            var stopLoss = config.Ranges.Current(Owner.Close[0]).ExtHigh;

            if (FractalManager.Fractals.TryGetValue((1, FractalType.HighFiveBar), out var highFiveBarFractal))
            {
                if (highFiveBarFractal.Price > stopLoss && highFiveBarFractal.Price < config.Ranges.Current(highFiveBarFractal.Price).BR1)
                {
                    stopLoss = highFiveBarFractal.Price;
                    Owner.PrintDebug($"[SHORT_REVERSAL_FRACTAL_STOP] ext high: {config.Ranges.Current(Owner.Close[0]).ExtHigh}, fractal {highFiveBarFractal.Price}");
                }
            }

            var stopDistance = (stopLoss - Owner.High[0]);// + config.StopGrace;
            var positionSize = Owner.CalculatePositionSize(stopDistance, config.Risk);
            if (positionSize < 1)
            {
                Owner.PrintDebug($"[POSITION_SUBOPTIMAL_SHORT] Position {positionSize}, Stop {stopLoss}");
                return TradeDetails.Failed();
            }

            var profitTarget = config.Ranges.Current(Owner.Close[0]).Eq;

            return new()
            {
                Entry = Owner.High[0],
                Bias = TradeBias.Short,
                IsLimit = true,
                Log = "SHORT_ENTRY_REVERSAL",
                PositionSize = positionSize,
                RiskReward = 1,
                OriginalStopLoss = stopLoss,
                StopLoss = stopLoss,
                TakeProfit = profitTarget,
                TradeMissedThreshold = profitTarget,
                BreakEvenThreshold = config.Ranges.Current(Owner.Close[0]).MB4,
                Type = TradeType.Reversal,
            };
        }
    }
}