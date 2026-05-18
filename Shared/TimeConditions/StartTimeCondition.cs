namespace NinjaTrader.Custom.Strategies.Fassa.TimeConditions
{
    internal class StartTimeCondition : ITimeCondition
    {
        public int Time { get; private set; }
		
		public void Execute()
		{
		}
		
        public bool IsSatisfied(int currentTime)
        {
            return Time >= currentTime;
        }

        public static StartTimeCondition Create(int time)
        {
            return new StartTimeCondition() { Time = time };
        }
    }
}
