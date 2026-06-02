using System;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public enum MSSBias
    {
        Bullish,
        Bearish,
        Neutral
    }

    public enum RangeBias
    {
        Bullish,
        Bearish,
        Neutral
    }

    public class InvalidRange
    {
        private readonly double High;
        private readonly double Low;

        public InvalidRange(double high, double low)
        {
            High = high;
            Low = low;
        }

        public bool IsInPlay(double price)
        {
            return price >= Low && price <= High;
        }

        public override string ToString()
        {
            return $"{High} {Low}";
        }
    }

    public class DealingRangeTracker
    {
        public DealingRange DealingRange { get; set; }
        public string Action { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SharedBorderTrade
    {
        public int Trades { get; set; }
        public double Border { get; set; }
    }
}
