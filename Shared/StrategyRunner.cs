using System.Collections.Generic;
using System.Linq;

namespace NinjaTrader.Custom.Strategies.Fassa
{
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
