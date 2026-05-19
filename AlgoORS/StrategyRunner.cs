using System.Collections.Generic;
using System.Linq;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS
{
    public enum TradeRangeInitiator
    {
        None,
        RangeHighEngaged,
        RangeLowEngaged,
        ContinueAboveHigh,
        ContinueBelowLow
    }

    internal abstract class Condition(AlgoORSv1 owner, FractalManager fractalManager)
    {
        protected readonly AlgoORSv1 Owner = owner;
        protected readonly FractalManager FractalManager = fractalManager;

        internal double RangeLow { get; set; }
        internal double RangeHigh { get; set; }
        internal int ValidFromTime { get; set; }
        internal TradeRangeInitiator RangeInitiator { get; set; }

        internal abstract bool IsSatisfied();

        internal bool CandleBodyPoints(int minPoints)
        {
            return Owner.Open[0] - Owner.Close[0] >= minPoints || Owner.Close[0] - Owner.Open[0] >= minPoints;
        }
    }

    interface ITimeCondition
    {
        int Time { get; }
        bool IsSatisfied(int currentTime);
        void Execute();
    }

    internal class StrategyRunner
    {
        private readonly List<ITimeCondition> timeConditions;

        public void Run()
        {
            // Run time conditions
            var startCondition = timeConditions.FirstOrDefault();
            if (startCondition != null) startCondition.Execute();

            var endCondition = timeConditions.FirstOrDefault();
            if (endCondition != null) endCondition.Execute();


            // Run entry conditions

            // Run trade management conditions
        }

        public void AddTimeCondition(ITimeCondition conditions)
        {
            timeConditions.Add(conditions);
        }
    }
}
