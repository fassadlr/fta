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
            // Is previous range covered
            var previousRange = config.Ranges.Previous(Owner.Close[0], true);
            if (previousRange != null && previousRange.CoveredPercentage <= 90)
            {
                //Owner.PrintDebug("[LONG_CONTINUATION_RANGE_NOT_COVERED]");
                return false;
            }

            // Has IMB formed
            if (!Owner.ImbalanceFormed(TradeBias.Long, config.Ranges.Current(Owner.Close[0])))
            {
                return false;
            }

            Owner.PrintDebug("[LONG_CONTINUATION_SATISFIED]");

            return true;
        }

        internal override TradeDetails Execute(RunnerConfiguration_PO3 config)
        {
            var stopLoss = config.Ranges.Current(Owner.Close[0]).ExtensionLow;// Owner.Low[0] - config.StopGrace;
            var stopDistance = (Owner.Low[0] - stopLoss);// + config.StopGrace;
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
                Log = "LONG_ENTRY_CONTINUATION",
                PositionSize = positionSize,
                RiskReward = 1,
                StopLoss = stopLoss,
                TakeProfit = profitTarget,
                Type = TradeType.Continuation,
            };
        }
    }
}