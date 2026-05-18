using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaTrader.NinjaScript.Strategies.FTA.Shared
{
    public class TradeState
    {
        public bool MSSDetectedBullish { get; set; }
        public bool MSSDetectedBearish { get; set; }

        public bool MSSConfirmed { get; set; }
        public bool RangeBreakConfirmed { get; set; }

        public bool Confirmed { get { return MSSConfirmed && RangeBreakConfirmed; } }

        public void Reset()
        {
            MSSDetectedBearish = false;
            MSSDetectedBullish = false;

            MSSConfirmed = false;
            RangeBreakConfirmed = false;
        }

        public override string ToString()
        {
            return $"MSSDetectedBullish: {MSSDetectedBullish}, MSSDetectedBearish: {MSSDetectedBearish}, MSSConfirmed: {MSSConfirmed}, RangeBreakConfirmed: {RangeBreakConfirmed}";
        }
    }

    public class DailyAtr
    {
        public double Atr { get; private set; }
        private double prevClose = 0;
        private readonly Queue<double> trQueue = new();
        private readonly int period = 14;

        public void Calculate(double high, double low, double close)
        {

            // Calculate True Range
            double tr = Math.Max(high - low,
                        Math.Max(Math.Abs(high - prevClose),
                                 Math.Abs(low - prevClose)));

            prevClose = close;

            // Build rolling ATR for current session only
            trQueue.Enqueue(tr);

            if (trQueue.Count > period)
                trQueue.Dequeue();

            // Calculate ATR
            if (trQueue.Count == period)
                Atr = Math.Round(trQueue.Average());
        }
    }

    public enum FractalType
    {
        HighFive = 0,
        LowFive,
        HighThree,
        LowThree,
        LowLookback,
        HighLookback,
    }

    public class MultiFractalManager(AlgoBase owner)
    {
        public Dictionary<(int, FractalType), (double Price, DateTime PriceTime)> Fractals = [];

        private readonly AlgoBase owner = owner;

        public void DetectFractalThreeBar(int timeFrame, double upperBound = 0, double lowerBound = 0)
        {
            // Need at least 3 bars
            if (owner.CurrentBar < 2)
                return;

            // --- FRACTAL HIGH LOGIC ---
            double h0 = owner.High[0];
            double h1 = owner.High[1];
            double h2 = owner.High[2];

            bool isFractalHigh =
                h1 > h0 &&
                h1 > h2;

            if (isFractalHigh)
            {
                if (upperBound > 0 && h1 > upperBound)
                    Fractals[(timeFrame, FractalType.HighThree)] = (h1, owner.Time[1]);

                if (upperBound == 0)
                    Fractals[(timeFrame, FractalType.HighThree)] = (h1, owner.Time[1]);

                //owner.PrintDebug($"[FRACTAL_3B_HIGH] {timeFrame}m upperBound {upperBound} h1 {h1} time {owner.Time[1]}");
            }

            // --- FRACTAL LOW LOGIC ---
            double l0 = owner.Low[0];
            double l1 = owner.Low[1];
            double l2 = owner.Low[2];

            bool isFractalLow =
                l1 < l0 &&
                l1 < l2;

            if (isFractalLow)
            {
                if (lowerBound > 0 && l1 < lowerBound)
                    Fractals[(timeFrame, FractalType.LowThree)] = (l1, owner.Time[1]);

                if (lowerBound == 0)
                    Fractals[(timeFrame, FractalType.LowThree)] = (l1, owner.Time[1]);

                //owner.PrintDebug($"[FRACTAL_3B_LOW] {timeFrame}m lowerBound {lowerBound} l1 {l1} time {owner.Time[1]}");
            }
        }

        public void DetectFractalFiveBar(int timeFrame)
        {
            // Need at least 5 bars
            if (owner.CurrentBar < 4)
                return;

            // --- FRACTAL HIGH (strict) ---
            double h0 = owner.High[0];
            double h1 = owner.High[1];
            double h2 = owner.High[2]; // center
            double h3 = owner.High[3];
            double h4 = owner.High[4];

            bool isFractalHigh =
                h2 > h0 &&
                h2 > h1 &&
                h2 > h3 &&
                h2 > h4;

            if (isFractalHigh)
            {
                Fractals[(timeFrame, FractalType.HighFive)] = (h2, owner.Time[2]);
                //owner.PrintDebug($"[FRACTAL_5B_HIGH] {timeFrame}m {h2} at {owner.Time[2]}");
            }

            // --- FRACTAL LOW (strict) ---
            double l0 = owner.Low[0];
            double l1 = owner.Low[1];
            double l2 = owner.Low[2]; // center
            double l3 = owner.Low[3];
            double l4 = owner.Low[4];

            bool isFractalLow =
                l2 < l0 &&
                l2 < l1 &&
                l2 < l3 &&
                l2 < l4;

            if (isFractalLow)
            {
                Fractals[(timeFrame, FractalType.LowFive)] = (l2, owner.Time[2]);
                //owner.PrintDebug($"[FRACTAL_5B_LOW] {timeFrame}m {l2} at {owner.Time[2]}");
            }
        }

        /// <summary>
        /// Is there a fractal that is not yet printed due to delayed bar processing.
        /// </summary>
        /// <param name="timeFrame"></param>
        public void DetectDelayedFiveBar(int timeFrame)
        {
            double l0 = owner.Lows[2][0];
            double l1 = owner.Lows[2][1];
            double l2 = owner.Lows[2][2]; // center
            double l3 = owner.Lows[2][3];

            bool isFractalLow =
                l2 < l0 &&
                l2 < l1 &&
                l2 < l3;

            if (isFractalLow)
            {
                Fractals[(timeFrame, FractalType.LowFive)] = (l2, owner.Times[2][2]);
                owner.PrintDebug($"[FRACTAL_5B_LOW_DELAYED] {timeFrame}m {l2} at {owner.Times[2][2]}");
            }

            // --- FRACTAL HIGH (strict) ---
            double h0 = owner.Highs[2][0];
            double h1 = owner.Highs[2][1];
            double h2 = owner.Highs[2][2]; // center
            double h3 = owner.Highs[2][3];

            bool isFractalHigh =
                h2 > h0 &&
                h2 > h1 &&
                h2 > h3;

            if (isFractalHigh)
            {
                Fractals[(timeFrame, FractalType.HighFive)] = (h2, owner.Times[2][2]);
                owner.PrintDebug($"[FRACTAL_5B_HIGH_DELAYED] {timeFrame}m {h2} at {owner.Times[2][2]}");
            }
        }

        public void DetectViaLookback(int timeFrame)
        {
            double low = owner.Low[0];
            DateTime lowTime = owner.Time[0];

            for (int i = 1; i <= 5; i++)
            {
                if (owner.Low[i] < low)
                {
                    low = owner.Low[i];
                    lowTime = owner.Time[i];
                }
            }

            Fractals[(timeFrame, FractalType.LowLookback)] = (low, lowTime);

            double high = owner.High[0];
            DateTime highTime = owner.Time[0];

            for (int i = 1; i <= 5; i++)
            {
                if (owner.High[i] > high)
                {
                    high = owner.High[i];
                    highTime = owner.Time[i];
                }
            }

            Fractals[(timeFrame, FractalType.HighLookback)] = (high, highTime);
        }

        public (double Price, DateTime Time) RecentLow()
        {
            var lowest = new List<(double Price, DateTime Time)>
            {
                Fractals[(15, FractalType.LowFive)],
                Fractals[(15, FractalType.LowThree)],
                Fractals[(5, FractalType.LowThree)],
                Fractals[(5, FractalType.LowFive)],
                Fractals[(15, FractalType.LowLookback)],
                (owner.Low[0], owner.Time[0])
            }
            .OrderBy(x => x.Price)
            .First();

            //PrintDebug($"{fractalManager.Fractals[(15, FractalType.HighFive)]}");
            //PrintDebug($"{fractalManager.Fractals[(15, FractalType.HighThree)]}");
            //PrintDebug($"{fractalManager.Fractals[(5, FractalType.HighThree)]}");
            //PrintDebug($"{fractalManager.Fractals[(5, FractalType.HighFive)]}");
            //PrintDebug($"{(High[0], Time[0])}");

            return lowest;
        }

        public (double Price, DateTime Time) RecentHigh()
        {
            var highest = new List<(double Price, DateTime Time)>() {
                Fractals[(15, FractalType.HighFive)],
                Fractals[(15, FractalType.HighThree)],
                Fractals[(5, FractalType.HighThree)],
                Fractals[(5, FractalType.HighFive)],
                Fractals[(15, FractalType.HighLookback)],
                (owner.High[0], owner.Time[0])
            }
            .OrderByDescending(x => x.Price)
            .First();

            return highest;
        }
    }

    public class DailyRangeTracker
    {
        public double LowOfDay { get; set; }
        public DateTime LowOfDayTime { get; set; }

        public double HighOfDay { get; set; }
        public DateTime HighOfDayTime { get; set; }

        public void Update(double high, double low, DateTime time)
        {
            if (high > HighOfDay || HighOfDay == 0)
            {
                HighOfDay = high;
                HighOfDayTime = time;
            }

            if (low < LowOfDay || LowOfDay == 0)
            {
                LowOfDay = low;
                LowOfDayTime = time;
            }
        }

        public override string ToString()
        {
            return $"LOD: {LowOfDay} HOD: {HighOfDay}";
        }
    }
}