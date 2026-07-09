using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public abstract class AlgoPO3Base : AlgoBase
    {
        [NinjaScriptProperty]
        [Display(Name = "PO3", Description = "PO3", Order = 10, GroupName = "Parameters")]
        public int PO3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Skip Months", Order = 11, GroupName = "Parameters")]
        public string SkipMonths { get; set; }
        internal HashSet<int> SkipMonthsSet { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Edge Multiplier", Order = 12, GroupName = "Parameters")]
        public double EdgeHourMultiplier { get; set; }

        internal int[] BlockEndHoursParsed;
        internal int[] EdgeHoursParsed;
        internal int[] SkipHoursParsed;

        internal RangeBias RangeBias { get; set; }
        internal TradeBias TradeBias { get; set; }

        internal DealingRange CurrentRange { get; set; }
        internal DealingRange PreviousRange { get; set; }

        internal List<DealingRange> Ranges { get; set; }

        internal InvalidRange InvalidRange { get; set; }
        internal SharedBorderTrade SharedBorderTrade { get; set; }

        internal TradeSessionState TradingSession;

        internal void CancelLimitOrder()
        {
            if (CurrentTrade?.PendingOrder == null)
                return;

            CancelOrder(CurrentTrade?.PendingOrder);

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

        internal DealingRange CreateDealingRange()
        {
            var newDealingRange = new DealingRange(PO3, Close[0]);
            Ranges.Add(newDealingRange);
            PrintDebug($"Creating new range: {newDealingRange}");
            return newDealingRange;
        }
    }
}
