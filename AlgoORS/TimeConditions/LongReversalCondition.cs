using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS
{
    internal sealed class LongReversalCondition : Condition
    {
        internal LongReversalCondition(AlgoORSv1 owner, FractalManager fractalManager) : base(owner, fractalManager)
        {
            ValidFromTime = 144500;
        }

        internal override bool IsSatisfied()
        {
            if (RangeInitiator == TradeRangeInitiator.None)
                return false;

            if (ValidFromTime <= NinjaScriptBase.ToTime(Owner.Time[0]))
                return false;

            if (RangeInitiator == TradeRangeInitiator.RangeLowEngaged)
                return Owner.Close[0] > RangeLow && CandleBodyPoints(1);

            return false;
        }
    }
}
