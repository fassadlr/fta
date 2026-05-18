#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;
#endregion

//This namespace holds Strategies in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public class AlgoPO3v3 : Strategy
    {
        [NinjaScriptProperty]
        [Display(Name = "PO3", Description = "PO3", Order = 0, GroupName = "Parameters")]
        public int PO3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Debug Logs", Order = 1, GroupName = "Parameters")]
        public bool IsDebug { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Adjust Risk on Consec Loss", Order = 2, GroupName = "Parameters")]
        public bool AdjustRiskBasedOnLosses { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Break Even", Order = 3, GroupName = "Parameters")]
        public bool BreakEvenEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Risk", Order = 5, GroupName = "Parameters")]
        public int Risk { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session Start", Order = 5, GroupName = "Parameters")]
        public int SessionStart { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session End", Order = 5, GroupName = "Parameters")]
        public int SessionEnd { get; set; }

        private Order LimitOrder { get; set; }
        private List<DealingRange> Ranges { get; set; }

        private DealingRange CurrentRange { get; set; }
        private DealingRange PreviousRange { get; set; }
        private TradeBias Bias { get; set; }
        private DealingRange TradeRange { get; set; }

        private double TakeProfitPrice { get; set; }

        private List<NewsEvent> NewsEvents { get; set; }

        private int ConsecutiveLosses { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"Enter the description for your new custom Strategy here.";
                Name = "FassaPO3 v3";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                IsExitOnSessionCloseStrategy = true;
                PO3 = 81;
                SessionStart = 80000;
                SessionEnd = 140000;
                Risk = 300;
            }
            else if (State == State.Configure)
            {
                AddDataSeries("MNQ 03-26", BarsPeriodType.Tick, 1);
                Ranges = new List<DealingRange>();

                NewsEvents = new List<NewsEvent>();
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 9, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 13, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 13, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 30, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 6, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 10, 12, 30, 0)));

                ConsecutiveLosses = 0;
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            // Adjust time for NY/UK BST overlap
            if (Time[0].Date >= new DateTime(2026, 3, 9) && Time[0].Date <= new DateTime(2026, 3, 29))
                SessionEnd = 130000;

            // The strategy only runs from a given time in the morning (8am)
            if (ToTime(Time[0]) <= SessionStart)
                return;

            // If the session has ended do some cleanup if neccessary.
            if ((ToTime(Time[0]) > SessionEnd || FilterByEvents()) && Position.MarketPosition == MarketPosition.Flat)
            {
                if (LimitOrder != null)
                {
                    CancelOrder(LimitOrder);
                    LimitOrder = null;
                    PrintDebugWithTime("End of session, cancel limit order...");
                }

                if (Ranges.Count > 0)
                {
                    PrintDebugWithTime("End of session, clearing ranges...");
                    Ranges.Clear();
                }

                TradeRange = null;
                CurrentRange = null;
                PreviousRange = null;
                Bias = TradeBias.Neutral;

                return;
            }

            // If no previous range has been set it means that we never broke range
            // and such there will be no bias or previous limit order.
            if (CurrentRange != null && PreviousRange != null)
            {
                //				var updateAndCancel = false;
                //				if(Bias == TradeBias.Short && Low[0] >= CurrentRange.MBCE1 && !CurrentRange.ShortTradeExecuted)
                //					updateAndCancel = true;

                //				if(Bias == TradeBias.Long && High[0] >= CurrentRange.InvalidLevel(Bias, InvalidRangeLevel) && !CurrentRange.LongTradeExecuted)
                //					updateAndCancel = true;

                //				// A trade has been attempted on this leg so ignore next time.
                //				if(updateAndCancel)
                //				{
                //					PrintDebugWithTime($"{Bias}: price went past TP, updating range state.");
                //					UpdateDealingRangeState(CurrentRange);

                //					if(LimitOrder != null)
                //					{
                //						CancelOrder(LimitOrder);
                //						LimitOrder = null;

                //						PrintDebugWithTime($"{Bias} limit order cancelled.");
                //					}	
                //				}
            }


            // ** Break Even Trade Logic **
            // If a position is open or a limit order is pending, defer processing.
            //			if(Position.MarketPosition != MarketPosition.Flat || LimitOrder != null)
            //			{
            //				// BE trade at level
            //				if(BreakEvenEnabled && Position.MarketPosition == MarketPosition.Short)
            //				{
            //					if(Low[0] <= CurrentRange.MB1)
            //					{
            //						SetStopLoss("short entry", CalculationMode.Price, CurrentRange.FV3Entry, true);
            //					}
            //				}

            //				if(BreakEvenEnabled && Position.MarketPosition == MarketPosition.Long)
            //				{
            //					if(High[0] >= CurrentRange.MB2)
            //					{
            //						SetStopLoss("long entry", CalculationMode.Price, CurrentRange.FV2Entry, true);
            //					}
            //				}

            //				return;
            //			}

            // Create the initial dealing range, this will only happen once per session.
            if (CurrentRange == null)
            {
                CurrentRange = CreateDealingRange();
                Bias = TradeBias.Neutral;
                return; // We can return as no signals will be created on strategy startup.
            }

            // Determine if we are still in the same dealing range.
            var found = Ranges.FirstOrDefault(r => r.IsInRange(Close[0]));
            if (found == null)
            {
                found = CreateDealingRange();
                Bias = TradeBias.Neutral;
            }

            // Compare and set the previous range if this range is not the current.
            if (found != CurrentRange)
            {
                if (PreviousRange != CurrentRange)
                {
                    PreviousRange = CurrentRange;
                    CurrentRange = found;

                    // If a previous range dealing range has been set then a break into
                    // a new range has occurred. We then determine the trade bias.

                    // We are shorting from the new range extension high					
                    var cancelLimitOrder = false;
                    if (Position.MarketPosition == MarketPosition.Flat && LimitOrder == null && CurrentRange.Low == PreviousRange.High)
                    {
                        Bias = TradeBias.Short;
                        PrintDebugWithTime($"New bias set to {Bias}");

                        cancelLimitOrder = true;

                        //Check if the close is past the entry already.
                        if (Close[0] > CurrentRange.FV1)
                        {
                            PrintDebugWithTime($"{Bias} entry ignored, price closed passed entry {CurrentRange.FV1}");
                            UpdateDealingRangeState(CurrentRange);
                        }
                    }

                    //We are longing from the new range extension low
                    if (Position.MarketPosition == MarketPosition.Flat && LimitOrder == null && CurrentRange.High == PreviousRange.Low)
                    {
                        Bias = TradeBias.Long;
                        PrintDebugWithTime($"New bias set to {Bias}");

                        cancelLimitOrder = true;

                        //Check if the close is past the entry already.
                        if (Close[0] < CurrentRange.FV4)
                        {
                            PrintDebugWithTime($"{Bias} entry ignored, price closed passed entry {CurrentRange.FV4}");
                            UpdateDealingRangeState(CurrentRange);
                        }
                    }

                    if (LimitOrder != null && cancelLimitOrder)
                    {
                        // TODO This needs some thought
                        // We need to have a level where the limit order is cancelled.
                        //ClearDealingRangeState(CurrentRange);
                        //CancelOrder(LimitOrder);
                        //LimitOrder = null;


                        //PrintDebugWithTime($"{Bias} limit order cancelled.");
                    }
                }
            }

            // We can still be in the opening dealing range and as such defer processing.
            if (PreviousRange == null)
                return;

            // If we set a limit order and price gets to TP we need to clear the limit order
            if (Position.MarketPosition == MarketPosition.Flat && LimitOrder != null)
            {
                if ((Bias == TradeBias.Long && High[0] >= TakeProfitPrice) ||
                   (Bias == TradeBias.Short && Low[0] <= TakeProfitPrice))
                {
                    PrintDebugWithTime($"{Bias}: price went passed TP {TakeProfitPrice}, resetting...");
                    CancelOrder(LimitOrder);
                    LimitOrder = null;
                    Bias = TradeBias.Neutral;
                    TakeProfitPrice = 0;

                    //ClearDealingRangeState();
                }

                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat || LimitOrder != null)
            {
                return;
            }

            //			 If a previous range dealing range has been set then a break into
            //			 a new range has occurred. We then determine the trade bias.
            //			 We are shorting from the new range extension high
            //			if(CurrentRange.Low == PreviousRange.High)
            //			{
            //				Bias = TradeBias.Short;
            //				PrintDebugWithTime($"New bias set to {Bias}");
            //			}

            //			 We are longing from the new range extension low
            //			if(CurrentRange.High == PreviousRange.Low)
            //			{
            //				Bias = TradeBias.Long;
            //				PrintDebugWithTime($"New bias set to {Bias}");
            //			}

            // The candle's low must be above the top of the FV partition
            //PrintDebugWithTime($"{CurrentRange.LongTradeExecuted} {TakeProfitPrice}");
            if (Bias == TradeBias.Long && !CurrentRange.LongTradeExecuted && Close[0] < CurrentRange.High && Close[0] > CurrentRange.FV4)
            {
                // Disabled for now
                // if(CurrentRange.LongTradeLoss)
                //	return;

                //				if(!CanPlaceTrade())
                //				{
                //					PrintDebugWithTime($"{Bias} entry ignored");	
                //					return;
                //				}

                var entryRounded = Math.Round(CurrentRange.FV4 / 0.25) * 0.25;
                PrintWithTime($"{Bias} entry : {entryRounded}");

                var adjustedRisk = Risk;
                if (AdjustRiskBasedOnLosses && ConsecutiveLosses >= 2)
                {
                    adjustedRisk = Risk / 2;
                    PrintDebugWithTime($"Adjust risk to {Risk}");
                }

                var LVLevel = CurrentRange.BR4 + Math.Round((CurrentRange.FV3 - CurrentRange.BR4) * 0.33, 2);
                PrintDebugWithTime($"Stop level (LVLevel) {LVLevel}");
                var stopLossDistance = Math.Round(entryRounded - LVLevel, 1, MidpointRounding.ToEven);
                var stopLossTicks = stopLossDistance / Instrument.MasterInstrument.TickSize;
                var tickValue = Instrument.MasterInstrument.PointValue * Instrument.MasterInstrument.TickSize;
                var positionSize = (int)Math.Round(adjustedRisk / (stopLossTicks * tickValue), MidpointRounding.AwayFromZero);
                LimitOrder = EnterLongLimit(0, true, positionSize, entryRounded, "long entry");

                SetStopLoss("long entry", CalculationMode.Price, LVLevel, true);

                TakeProfitPrice = PreviousRange.MBCE1;
                SetProfitTarget("long entry", CalculationMode.Price, TakeProfitPrice, true);

                // Sets the range that the trade was taken in.
                TradeRange = CurrentRange;
            }

            //PrintDebugWithTime($"{CurrentRange.ShortTradeExecuted} {Close[0]} {CurrentRange.Low} {CurrentRange.FV1} ");	
            if (Bias == TradeBias.Short && !CurrentRange.ShortTradeExecuted && Close[0] > CurrentRange.Low && Close[0] < CurrentRange.FV1)
            {
                // Disabled for now				
                // if(CurrentRange.ShortTradeLoss)
                //	return;

                //				if(!CanPlaceTrade())
                //				{
                //					PrintDebugWithTime($"{Bias} entry ignored");	
                //					return;
                //				}

                var entryRounded = Math.Round(CurrentRange.FV1 / 0.25) * 0.25;
                PrintWithTime($"{Bias} entry : {entryRounded}");

                var adjustedRisk = Risk;
                if (AdjustRiskBasedOnLosses && ConsecutiveLosses >= 2)
                {
                    adjustedRisk = Risk / 2;
                    PrintDebugWithTime($"Adjust risk to {Risk}");
                }

                var LVLevel = CurrentRange.BR1 - Math.Round((CurrentRange.BR1 - CurrentRange.FV2) * 0.33, 2);
                PrintDebugWithTime($"Stop level (LVLevel) {LVLevel}");
                var stopLossDistance = Math.Round(LVLevel - entryRounded, 1, MidpointRounding.ToEven);
                var stopLossTicks = stopLossDistance / Instrument.MasterInstrument.TickSize;
                var tickValue = Instrument.MasterInstrument.PointValue * Instrument.MasterInstrument.TickSize;
                var positionSize = (int)Math.Round(adjustedRisk / (stopLossTicks * tickValue), MidpointRounding.AwayFromZero);
                LimitOrder = EnterShortLimit(0, true, positionSize, entryRounded, "short entry");

                SetStopLoss("short entry", CalculationMode.Price, LVLevel, true);

                TakeProfitPrice = PreviousRange.MBCE2;
                SetProfitTarget("short entry", CalculationMode.Price, TakeProfitPrice, true);

                // Sets the range that the trade was taken in.
                TradeRange = CurrentRange;
            }

            //			if((Bias == TradeBias.Long && Close[0] > CurrentRange.Low) ||
            //			   (Bias == TradeBias.Short && Close[0] < CurrentRange.High))
            //				ClearDealingRangeState(PreviousRange);
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order.Name == "Profit target")
            {
                // the profit target has had a fill or part fill
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    PrintWithTime("Profit target triggered, resetting...");
                    LimitOrder = null;

                    ClearDealingRangeStates();

                    Bias = TradeBias.Neutral;
                    TakeProfitPrice = 0;

                    ConsecutiveLosses = 0;
                }
            }

            if (execution.Order.Name == "Stop loss")
            {
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    PrintWithTime("Stop loss triggered, resetting...");
                    LimitOrder = null;

                    ClearDealingRangeStates();

                    Bias = TradeBias.Neutral;
                    TakeProfitPrice = 0;

                    ConsecutiveLosses++;
                }
            }

            if (execution.Order.Name == "Trail stop")
            {
                // the trailing stop loss has had a fill or part fill
            }
        }

        private void UpdateDealingRangeState(DealingRange applicable)
        {
            var applicableRange = Ranges.FirstOrDefault(r => r == applicable);
            if (Bias == TradeBias.Long)
            {
                PrintDebugWithTime($"{Bias}: Updating range state range: {applicable}");
                applicableRange.LongTradeExecuted = true;
            }

            if (Bias == TradeBias.Short)
            {
                PrintDebugWithTime($"{Bias}: Updating range state range: {applicable}");
                applicableRange.ShortTradeExecuted = true;
            }
        }

        private void ClearDealingRangeStates()
        {
            foreach (var range in Ranges)
            {
                range.LongTradeExecuted = false;
                range.ShortTradeExecuted = false;
            }
        }

        private void UpdateDealingRangeTradeLoss(DealingRange applicable)
        {
            var applicableRange = Ranges.FirstOrDefault(r => r == applicable);
            if (Bias == TradeBias.Long)
            {
                PrintDebugWithTime($"{Bias}: Updating range state range: {applicableRange}");
                applicableRange.LongTradeLoss = true;
            }

            if (Bias == TradeBias.Short)
            {
                PrintDebugWithTime($"{Bias}: Updating range state range: {applicableRange}");
                applicableRange.ShortTradeLoss = true;
            }
        }

        private DealingRange CreateDealingRange()
        {
            var newDealingRange = new DealingRange(PO3, Close[0]);
            Ranges.Add(newDealingRange);
            PrintDebugWithTime($"Creating New Range: {newDealingRange}");
            return newDealingRange;
        }

        private void ClearDealingRangeState(DealingRange range)
        {
            // Clear trade taken state of the range below:
            var applicable = Ranges.FirstOrDefault(r => r == range);
            if (applicable != null && (applicable.LongTradeExecuted || applicable.ShortTradeExecuted))
            {
                applicable.LongTradeExecuted = false;
                applicable.ShortTradeExecuted = false;
                PrintDebugWithTime($"Clearing state for range: {applicable}");
            }
        }

        private bool FilterByEvents()
        {
            var newsEvent = NewsEvents.FirstOrDefault(n => Time[0].Date == n.EventTime.Date && Time[0].TimeOfDay >= n.EventTime.TimeOfDay);
            return newsEvent != null;
        }

        public void PrintWithTime(string toPrint)
        {
            Print($"{Time[0]} {toPrint}");
        }

        public void PrintDebugWithTime(string toPrint)
        {
            if (IsDebug)
                Print($"{Time[0]} {toPrint}");
        }
    }
}
