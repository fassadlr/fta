using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3.Conditions
{
    internal class LongContinuation : StrategyCondition<PO3_RunnerConfiguration>
    {
        internal LongContinuation(AlgoBase owner, FractalManager fractalManager) : base(owner, fractalManager)
        {
        }

        internal override bool IsSatisfied(PO3_RunnerConfiguration config)
        {
            // Is previous range covered

            // Has IMB formed

            Owner.PrintDebug("[LONG_CONTINUATION_SATISFIED]");

            return true;
        }

        internal override TradeExecutionDetails Execute(PO3_RunnerConfiguration settings)
        {
            var stopLoss = Owner.Low[0] - settings.StopGrace;
            var stopDistance = (Owner.Close[0] - Owner.Low[0]) + settings.StopGrace;
            var positionSize = Owner.CalculatePositionSize(stopDistance, settings.Risk);
            if (positionSize < 0.75)
            {
                Owner.PrintDebug($"[POSITION_SUBOPTIMAL_LONG] Position {positionSize}, Stop {stopLoss}");
                return TradeExecutionDetails.Failed();
            }

            var profitTarget = Owner.Close[0] + (stopDistance * settings.TPMultiplier);

            return new()
            {
                Bias = TradeBias.Long,
                IsLimit = false,
                Log = "LONG_ENTRY_CONTINUATION",
                PositionSize = positionSize,
                RangeInitiator = settings.RangeInitiator,
                RiskReward = 1,
                StopLoss = stopLoss,
                TakeProfit = profitTarget,
                Type = TradeType.Continuation,
            };
        }
    }
}