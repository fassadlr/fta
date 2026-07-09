using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS;

namespace NinjaTrader.NinjaScript.Strategies.FTA.Shared
{
    public enum TradeType
    {
        Reversal,
        Continuation,
    }

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

    public class TradeDetails
    {
        public TradeBias Bias { get; set; }
        public TradeRangeInitiator? RangeInitiator { get; set; }
        public TradeType Type { get; set; }
        public double Entry { get; set; } = 0;
        public DateTime EntryTime { get; set; }
        public Order PendingOrder { get; set; }
        public double OriginalStopLoss { get; set; }
        public double StopLoss { get; set; } = 0;
        public double TakeProfit { get; set; } = 0;
        public double ProfitLoss { get; set; } = 0;
        public double MaxDrawdown { get; set; } = 0;
        public double DrawdownStopPercentage { get; set; } = 0;

        public bool TradeDetailsSubOptimal { get; set; }
        public bool IsLimit { get; internal set; }
        public string Log { get; internal set; }
        public double RiskReward { get; internal set; }
        public int PositionSize { get; internal set; }

        public OrderType OrderType { get; set; }
        public double VWAPDistance { get; set; } = 0;
        public string VWAPLocation { get; internal set; }
        public int Quantity { get; internal set; }
        public double OpeningPriceDistance { get; internal set; }
        public double TradeMissedThreshold { get; internal set; }
        public double Atr { get; internal set; }
        public double BreakEvenThreshold { get; internal set; }

        public double DeltaMomentum { get; internal set; }

        public double CvdSlopeFast { get; internal set; }
        public double CvdRSquaredFast { get; internal set; }

        public double CvdSlopeSlow { get; internal set; }
        public double CvdRSquaredSlow { get; internal set; }

        public double CvdAcceleration { get; internal set; }

        public long BarVolumeDelta { get; internal set; }
        public double BarVolume { get; internal set; }
        public double CVD_EMA_Distance { get; internal set; }

        public override string ToString()
        {
            return string.Join(",",
                EntryTime.ToString("dd/MM/yyyy HH:mm"),
                EscapeCsv(Bias.ToString()),
                EscapeCsv(Type.ToString()),
                ProfitLoss >= 0 ? "W" : "L"
            );
        }

        private static string EscapeCsv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }

            return value;
        }

        internal static TradeDetails Failed()
        {
            return new TradeDetails() { TradeDetailsSubOptimal = true };
        }
    }

    public class DailyAtr
    {
        public double Atr { get; private set; }
        private double prevClose = 0;
        public readonly Queue<double> trQueue = new();
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
        HighFiveBar = 0,
        LowFiveBar,
        HighThreeBar,
        LowThreeBar,
        LowLookback,
        HighLookback,
    }

    public class FractalManager(AlgoBase owner)
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
                    Fractals[(timeFrame, FractalType.HighThreeBar)] = (h1, owner.Time[1]);

                if (upperBound == 0)
                    Fractals[(timeFrame, FractalType.HighThreeBar)] = (h1, owner.Time[1]);

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
                    Fractals[(timeFrame, FractalType.LowThreeBar)] = (l1, owner.Time[1]);

                if (lowerBound == 0)
                    Fractals[(timeFrame, FractalType.LowThreeBar)] = (l1, owner.Time[1]);

                //owner.PrintDebug($"[FRACTAL_3B_LOW] {timeFrame}m lowerBound {lowerBound} l1 {l1} time {owner.Time[1]}");
            }
        }

        public void DetectFractalThreeBarWithPlatos(int timeFrame)
        {
            // Need at least 3 bars
            if (owner.CurrentBar < 2)
                return;

            // --- FRACTAL HIGH LOGIC ---
            double h0 = owner.High[0];
            double h1 = owner.High[1];
            double h2 = owner.High[2];

            // Must be >= both neighbors (allows equal highs)
            bool isPeakShape = h1 >= h0 && h1 >= h2;

            // At least one side must be strictly lower (prevents flat triple-equal highs)
            bool hasStrictEdge = h1 > h0 || h1 > h2;

            if (isPeakShape && hasStrictEdge)
            {
                Fractals[(timeFrame, FractalType.HighThreeBar)] = (h1, owner.Time[1]);
                //owner.PrintDebug($"[FRACTAL_{timeFrame}M_HIGH] {h1} time {owner.Time[1]}");
            }

            // --- FRACTAL LOW LOGIC ---
            double l0 = owner.Low[0];
            double l1 = owner.Low[1];
            double l2 = owner.Low[2];

            // Must be <= both neighbors (allows equal lows)
            bool isValleyShape = l1 <= l0 && l1 <= l2;

            // At least one side must be strictly higher (prevents flat triple-equal lows)
            bool hasStrictEdgeLow = l1 < l0 || l1 < l2;

            if (isValleyShape && hasStrictEdgeLow)
            {
                Fractals[(timeFrame, FractalType.LowThreeBar)] = (l1, owner.Time[1]);
                //owner.PrintDebug($"[FRACTAL_{timeFrame}M_LOW] {l1} time {owner.Time[1]}");
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
                Fractals[(timeFrame, FractalType.HighFiveBar)] = (h2, owner.Time[2]);
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
                Fractals[(timeFrame, FractalType.LowFiveBar)] = (l2, owner.Time[2]);
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
                Fractals[(timeFrame, FractalType.LowFiveBar)] = (l2, owner.Times[2][2]);
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
                Fractals[(timeFrame, FractalType.HighFiveBar)] = (h2, owner.Times[2][2]);
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
                Fractals.TryGetValue((15, FractalType.LowFiveBar), out var lowFiveBar) ? lowFiveBar : (0, DateTime.MinValue),
                Fractals.TryGetValue((15, FractalType.LowThreeBar), out var lowThreeBar) ? lowThreeBar : (0, DateTime.MinValue),
                Fractals.TryGetValue((5, FractalType.LowThreeBar), out var lowThreeBar5) ? lowThreeBar5 : (0, DateTime.MinValue),
                Fractals.TryGetValue((5, FractalType.LowFiveBar), out var lowFiveBar5) ? lowFiveBar5 : (0, DateTime.MinValue),
                Fractals.TryGetValue((15, FractalType.LowLookback), out var lowLookback) ? lowLookback : (0, DateTime.MinValue),
                (owner.Low[0], owner.Time[0])
            }
            .Where(x => x.Price > 0)
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
                Fractals.TryGetValue((15, FractalType.HighFiveBar), out var highFiveBar) ? highFiveBar : (0, DateTime.MinValue),
                Fractals.TryGetValue((15, FractalType.HighThreeBar), out var highThreeBar) ? highThreeBar : (0, DateTime.MinValue),
                Fractals.TryGetValue((5, FractalType.HighThreeBar), out var highThreeBar5) ? highThreeBar5 : (0, DateTime.MinValue),
                Fractals.TryGetValue((5, FractalType.HighFiveBar), out var highFiveBar5) ? highFiveBar5 : (0, DateTime.MinValue),
                Fractals.TryGetValue((15, FractalType.HighLookback), out var highLookback) ? highLookback : (0, DateTime.MinValue),
                (owner.High[0], owner.Time[0])
            }
            .Where(x => x.Price > 0)
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