using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public abstract class AlgoPO3Base : AlgoBase
    {
        [NinjaScriptProperty]
        [Display(Name = "Position Size", Order = 10, GroupName = "Parameters")]
        public int PositionSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "PO3", Description = "PO3", Order = 11, GroupName = "Parameters")]
        public int PO3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Skip Months", Order = 12, GroupName = "Parameters")]
        public string SkipMonths { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Edge Multiplier", Order = 13, GroupName = "Parameters")]
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

        internal TradeDetails CurrentTrade;
        internal Order PendingOrder { get; set; }

        internal InvalidRange InvalidRange { get; set; }
        internal SharedBorderTrade SharedBorderTrade { get; set; }

        internal TradeSessionState TradingSession;

        internal void CancelLimitOrder()
        {
            if (PendingOrder == null)
                return;

            CancelOrder(PendingOrder);

            PendingOrder = null;
            CurrentTrade = null;

            PrintDebug("[CANCEL_LIMIT_ORDER]");
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
