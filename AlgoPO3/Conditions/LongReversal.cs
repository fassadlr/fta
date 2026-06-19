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

            // Has 1min BOS occurred
            //if (!Owner.BOSOccurred(TradeBias.Short, config.Ranges.Current(Owner.Close[0])))
            //{
            //    return false;
            //}

            // Has IMB formed
            if (!Owner.ImbalanceFormed(TradeBias.Long, config.Ranges.Current(Owner.Close[0])))
            {
                Owner.PrintDebug("[LONG_REVERSAL_IMB_NOT_FORMED]");
                return false;
            }

            Owner.PrintDebug("[LONG_REVERSAL_SATISFIED]");

            return true;
        }

        internal override TradeDetails Execute(RunnerConfiguration_PO3 config)
        {
            var stopLoss = config.Ranges.Current(Owner.Close[0]).ExtensionLow;// Owner.Low[0] - config.StopGrace;
            var stopDistance = Owner.Low[0] - stopLoss;// + config.StopGrace;
            var positionSize = Owner.CalculatePositionSize(stopDistance, config.Risk);
            if (positionSize < 1)
            {
                Owner.PrintDebug($"[POSITION_SUBOPTIMAL_LONG] Position {positionSize}, Stop {stopLoss}");
                return TradeDetails.Failed();
            }

            var profitTarget = config.Ranges.Current(Owner.Close[0]).Eq;

            return new()
            {
                Entry = Owner.Low[0],
                Bias = TradeBias.Long,
                IsLimit = true,
                Log = "LONG_ENTRY_REVERSAL",
                PositionSize = positionSize,
                RiskReward = 1,
                StopLoss = stopLoss,
                TakeProfit = profitTarget,
                TradeMissedThreshold = profitTarget,
                Type = TradeType.Reversal,
            };
        }
    }
}