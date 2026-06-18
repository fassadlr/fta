namespace NinjaTrader.NinjaScript.Strategies.FTA.Shared
{
    internal abstract class StrategyCondition<TConfig>(AlgoBase owner, FractalManager fractalManager)
    {
        protected readonly AlgoBase Owner = owner;
        protected readonly FractalManager FractalManager = fractalManager;

        internal int ValidFromTime { get; set; }

        internal bool Disabled { get; set; }
        internal bool ExecuteOnlyMe { get; set; }

        internal abstract bool IsSatisfied(TConfig config);
        internal abstract TradeExecutionDetails Execute(TConfig config);

        internal bool CandleBodyRangeInPoints(int minPoints)
        {
            return Owner.Open[0] - Owner.Close[0] >= minPoints || Owner.Close[0] - Owner.Open[0] >= minPoints;
        }
    }
}
