using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

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
        public double EdgeMultiplier { get; set; }

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

        internal TradeSessionState TradingSession { get; set; }

        internal CVD_TradeType_Enum CVD_TradeType { get; set; }

        internal void CancelLimitOrder()
        {
            if (CurrentTrade == null)
                return;

            if (CurrentTrade.PendingOrder != null)
            {
                CancelOrder(CurrentTrade.PendingOrder);
                PrintDebug("[CANCEL_LIMIT_ORDER]");
            }

            CurrentTrade = null;
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

        internal void UpdateCVDTradeType(bool isWin)
        {
            if (isWin)
            {
                if (CVD_TradeType == CVD_TradeType_Enum.Continuation)
                    CVD_TradeType = CVD_TradeType_Enum.Normal;
                else
                    CVD_TradeType = CVD_TradeType_Enum.Continuation;
            }
            else
                CVD_TradeType = CVD_TradeType_Enum.Normal;
        }

        internal void ProcessVirtualTrade()
        {
            if (CurrentTrade == null || (CurrentTrade?.Virtual == false)) return;

            if (!ImportTickAndCvdEnabled)
            {
                ProcessVirtualTradeInternal(Close[0]);
                return;
            }

            // Process the 1min bar's ticks via the imported data.
            long nextBarTicks = Time[0].AddMinutes(1).Ticks;
            //PrintDebug($"{CurrentTrade.VirtualTickIndex} {new DateTime(TickDataSeriesImport[CurrentTrade.VirtualTickIndex].Ticks)} {new DateTime(nextBarTicks)}");
            while (CurrentTrade.Virtual)
            {
                var (NewIndex, Tick) = GetNextTick(CurrentTrade.VirtualTickIndex, nextBarTicks);

                if (Tick == null)
                    break;

                CurrentTrade.VirtualTickIndex = NewIndex;

                ProcessVirtualTradeInternal(Tick.Value.Price);
            }
        }

        private void ProcessVirtualTradeInternal(double tickPrice)
        {
            if (!CurrentTrade.VirtualOpen && (CurrentTrade.OrderType == OrderType.Limit || CurrentTrade.OrderType == OrderType.StopMarket) && tickPrice == CurrentTrade.Entry)
            {
                CurrentTrade.VirtualOpen = true;
                PrintInfo($"[VIRTUAL_POSITION] {CurrentTrade.Bias} triggered at {tickPrice}");
                return;
            }

            if (!CurrentTrade.VirtualOpen) return;

            var contractTickValue = Instrument.MasterInstrument.PointValue * CurrentTrade.PositionSize;

            if (CurrentTrade.Bias == TradeBias.Short &&
                (tickPrice <= CurrentTrade.TakeProfit || tickPrice >= CurrentTrade.StopLoss))
            {
                CurrentTrade.Virtual = false;
                CurrentTrade.VirtualOpen = false;

                UpdateCVDTradeType(tickPrice <= CurrentTrade.TakeProfit);

                if (tickPrice <= CurrentTrade.TakeProfit)
                    DayPnLVirtual += (CurrentTrade.Entry - CurrentTrade.TakeProfit) * contractTickValue;

                if (tickPrice >= CurrentTrade.StopLoss)
                    DayPnLVirtual -= (CurrentTrade.StopLoss - CurrentTrade.Entry) * contractTickValue;

                PrintInfo($"[VIRTUAL_POSITION] {CurrentTrade.Bias} closed at {tickPrice} Virtual Day Pnl {Math.Round(DayPnLVirtual)}");
            }

            if (CurrentTrade.Bias == TradeBias.Long &&
                (tickPrice >= CurrentTrade.TakeProfit || tickPrice <= CurrentTrade.StopLoss))
            {
                CurrentTrade.Virtual = false;
                CurrentTrade.VirtualOpen = false;

                UpdateCVDTradeType(tickPrice >= CurrentTrade.TakeProfit);

                if (tickPrice >= CurrentTrade.TakeProfit)
                    DayPnLVirtual += (CurrentTrade.TakeProfit - CurrentTrade.Entry) * contractTickValue;

                if (tickPrice <= CurrentTrade.StopLoss)
                    DayPnLVirtual -= (CurrentTrade.Entry - CurrentTrade.StopLoss) * contractTickValue;

                PrintInfo($"[VIRTUAL_POSITION] {CurrentTrade.Bias} closed at {tickPrice} Virtual Day Pnl {Math.Round(DayPnLVirtual)}");
            }
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
