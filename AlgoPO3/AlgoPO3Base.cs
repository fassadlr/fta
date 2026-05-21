using System.ComponentModel.DataAnnotations;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public abstract class AlgoPO3Base : AlgoBase
    {
        [NinjaScriptProperty]
        [Display(Name = "Session Start", Order = 5, GroupName = "Parameters")]
        public int SessionStart { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session End", Order = 6, GroupName = "Parameters")]
        public int SessionEnd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PO3", Description = "PO3", Order = 7, GroupName = "Parameters")]
        public int PO3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Skip Hours", Order = 8, GroupName = "Parameters")]
        public string SkipHours { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Skip Months", Order = 9, GroupName = "Parameters")]
        public string SkipMonths { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Block End Hours", Order = 10, GroupName = "Parameters")]
        public string BlockEndHours { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Edge Hours", Order = 11, GroupName = "Parameters")]
        public string EdgeHours { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Edge Multiplier", Order = 12, GroupName = "Parameters")]
        public double EdgeHourMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Include CPI Week", Order = 14, GroupName = "Parameters")]
        public bool IncludeCPIWeek { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Include NFP Week", Order = 15, GroupName = "Parameters")]
        public bool IncludeNFPWeek { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Include No Events Week", Order = 16, GroupName = "Parameters")]
        public bool IncludeNoEventsWeek { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Include FOMC Week", Order = 17, GroupName = "Parameters")]
        public bool IncludeFOMCWeek { get; set; }

        internal bool BearishBOSDetected = false;
        internal bool BullishBOSDetected = false;

        internal int[] BlockEndHoursParsed;
        internal int[] EdgeHoursParsed;
        internal int[] SkipHoursParsed;

        internal InvalidRange InvalidRange { get; set; }
        internal SharedBorderTrade SharedBorderTrade { get; set; }

        internal TradeSessionState TradingSession;

        internal int AdjustSessionEnd()
        {
            var adjustSessionEnd = SessionEnd;
            if (IsDSTOverlap())
                adjustSessionEnd = SessionEnd - 1;

            return adjustSessionEnd;
        }

        internal void DetectFractalFiveBar()
        {
            // Need at least 5 bars
            if (CurrentBar < 4)
                return;

            // --- FRACTAL HIGH (strict) ---
            double h0 = High[0];
            double h1 = High[1];
            double h2 = High[2]; // center
            double h3 = High[3];
            double h4 = High[4];

            bool isFractalHigh =
                h2 > h0 &&
                h2 > h1 &&
                h2 > h3 &&
                h2 > h4;

            if (isFractalHigh)
            {
                LastFractalFiveBarHigh = h2;
                LastFractalFiveBarHighTime = Time[2];
                PrintDebug($"New Fractal High {LastFractalFiveBarHigh} at {LastFractalFiveBarHighTime}");
            }

            // --- FRACTAL LOW (strict) ---
            double l0 = Low[0];
            double l1 = Low[1];
            double l2 = Low[2]; // center
            double l3 = Low[3];
            double l4 = Low[4];

            bool isFractalLow =
                l2 < l0 &&
                l2 < l1 &&
                l2 < l3 &&
                l2 < l4;

            if (isFractalLow)
            {
                LastFractalFiveBarLow = l2;
                LastFractalFiveBarLowTime = Time[2];
                PrintDebug($"New Fractal Low {LastFractalFiveBarLow} at {LastFractalFiveBarLowTime}");
            }
        }

        internal bool DetectAndCheckInvalidRange()
        {
            if (InvalidRange == null)
                return false;

            if (InvalidRange.IsInPlay(Close[0]))
            {
                PrintDebug($"[RANGE_INVALID_IN_PLAY] Skipping as {Close[0]} is in invalid range.");
                return true;
            }

            // Did we leave the invalid range?
            if (!InvalidRange.IsInPlay(Close[0]))
            {
                PrintDebug($"[RANGE_INVALID_CLEARED] Price {Close[0]} cleared the invalid range, resetting.");

                InvalidRange = null;
                SharedBorderTrade = null;

                return false;
            }

            return false;
        }
    }
}
