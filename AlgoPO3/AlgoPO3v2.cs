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
    public class AlgoPO3v2 : Strategy
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
        [Display(Name = "Vol Imbs Enabled", Order = 4, GroupName = "Parameters")]
        public bool VolImbsEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Risk", Order = 5, GroupName = "Parameters")]
        public int Risk { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session Start", Order = 5, GroupName = "Parameters")]
        public int SessionStart { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session End", Order = 5, GroupName = "Parameters")]
        public int SessionEnd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Invalid Range Level", Order = 5, GroupName = "Parameters")]
        public int InvalidRangeLevel { get; set; }

        private Order LimitOrder { get; set; }
        private List<DealingRange> Ranges { get; set; }

        private DealingRange CurrentRange { get; set; }
        private DealingRange PreviousRange { get; set; }
        private TradeBias Bias { get; set; }
        private DealingRange TradeRange { get; set; }

        private List<NewsEvent> NewsEvents { get; set; }

        private int ConsecutiveLosses { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"Enter the description for your new custom Strategy here.";
                Name = "FassaPO3 v2";
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
                Ranges = [];
                NewsEvents =
                [
                    //NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 7, 8, 0, 0))); // Filter whole day
                    new NewsEvent(new DateTime(2026, 1, 9, 13, 30, 0)),
                    new NewsEvent(new DateTime(2026, 1, 13, 13, 30, 0)),
                    new NewsEvent(new DateTime(2026, 2, 13, 13, 30, 0)),
                    new NewsEvent(new DateTime(2026, 1, 30, 13, 30, 0)),
                    new NewsEvent(new DateTime(2026, 3, 6, 13, 30, 0)),
                    new NewsEvent(new DateTime(2026, 3, 10, 12, 30, 0)),
                ];

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
                }

                if (Ranges.Count > 0)
                {
                    PrintDebugWithTime("Clearing ranges...");
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
                var updateAndCancel = false;
                if (Bias == TradeBias.Short && Low[0] <= CurrentRange.InvalidLevel(Bias, InvalidRangeLevel) && !CurrentRange.ShortTradeExecuted)
                    updateAndCancel = true;

                if (Bias == TradeBias.Long && High[0] >= CurrentRange.InvalidLevel(Bias, InvalidRangeLevel) && !CurrentRange.LongTradeExecuted)
                    updateAndCancel = true;

                // A trade has been attempted on this leg so ignore next time.
                if (updateAndCancel)
                {
                    PrintDebugWithTime($"{Bias}: price went past TP, updating range state.");
                    UpdateDealingRangeState(CurrentRange);

                    if (LimitOrder != null)
                    {
                        CancelOrder(LimitOrder);
                        LimitOrder = null;

                        PrintDebugWithTime($"{Bias} limit order cancelled.");
                    }
                }
            }


            // If a position is open or a limit order is pending, defer processing.
            if (Position.MarketPosition != MarketPosition.Flat || LimitOrder != null)
            {
                // BE trade at level
                if (BreakEvenEnabled && Position.MarketPosition == MarketPosition.Short)
                {
                    if (Low[0] <= CurrentRange.MB1)
                    {
                        SetStopLoss("short entry", CalculationMode.Price, CurrentRange.FV3Entry, true);
                    }
                }

                if (BreakEvenEnabled && Position.MarketPosition == MarketPosition.Long)
                {
                    if (High[0] >= CurrentRange.MB2)
                    {
                        SetStopLoss("long entry", CalculationMode.Price, CurrentRange.FV2Entry, true);
                    }
                }

                return;
            }

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
                PreviousRange = CurrentRange;
                CurrentRange = found;
            }

            // We can still be in the opening dealing range and as such defer processing.
            if (PreviousRange == null)
                return;

            // If a previous range dealing range has been set then a break into
            // a new range has occurred. We then determine the trade bias.
            if (CurrentRange.Low == PreviousRange.High)
                Bias = TradeBias.Long;

            if (CurrentRange.High == PreviousRange.Low)
                Bias = TradeBias.Short;

            // The candle's low must be above the top of the FV partition
            // Filter out scenarios where the high has already reach the TP (BR2)
            if (Bias == TradeBias.Long && !CurrentRange.LongTradeExecuted && Low[0] > CurrentRange.FV2)// && High[0] < CurrentRange.MB2)
            {
                // Disabled for now
                // if(CurrentRange.LongTradeLoss)
                //	return;

                if (!CanPlaceTrade())
                {
                    PrintDebugWithTime($"{Bias} entry ignored");
                    return;
                }

                PrintWithTime($"Long entry : {CurrentRange.FV2Entry}");

                var adjustedRisk = Risk;
                if (AdjustRiskBasedOnLosses && ConsecutiveLosses >= 2)
                {
                    adjustedRisk = Risk / 2;
                    PrintDebugWithTime($"Adjust risk to {Risk}");
                }

                var stopLossDistance = CurrentRange.FV2 - CurrentRange.ExtensionLow;
                var stopLossTicks = stopLossDistance / Instrument.MasterInstrument.TickSize;
                var tickValue = Instrument.MasterInstrument.PointValue * Instrument.MasterInstrument.TickSize;
                var positionSize = (int)Math.Round(adjustedRisk / (stopLossTicks * tickValue), MidpointRounding.AwayFromZero);
                LimitOrder = EnterLongLimit(0, true, positionSize, CurrentRange.FV2Entry, "long entry");
                SetStopLoss("long entry", CalculationMode.Price, CurrentRange.ExtensionLow, true);
                SetProfitTarget("long entry", CalculationMode.Price, CurrentRange.BR3, true);

                // Sets the range that the trade was taken in.
                TradeRange = CurrentRange;
            }

            // The candle's high must be below the top of the FV partition
            // Filter out scenarios where the low has already reached the TP (BR1)
            if (Bias == TradeBias.Short && !CurrentRange.ShortTradeExecuted && High[0] < CurrentRange.FV3)// && Low[0] > CurrentRange.MB1)
            {
                // Disabled for now				
                // if(CurrentRange.ShortTradeLoss)
                //	return;

                if (!CanPlaceTrade())
                {
                    PrintDebugWithTime($"{Bias} entry ignored");
                    return;
                }

                PrintWithTime($"{Bias} entry : {CurrentRange.FV3Entry}");

                var adjustedRisk = Risk;
                if (AdjustRiskBasedOnLosses && ConsecutiveLosses >= 2)
                {
                    adjustedRisk = Risk / 2;
                    PrintDebugWithTime($"Adjust risk to {Risk}");
                }

                var stopLossDistance = Math.Round((CurrentRange.ExtensionHigh) - CurrentRange.FV3, 1, MidpointRounding.ToEven);
                var stopLossTicks = stopLossDistance / Instrument.MasterInstrument.TickSize;
                var tickValue = Instrument.MasterInstrument.PointValue * Instrument.MasterInstrument.TickSize;
                var positionSize = (int)Math.Round(adjustedRisk / (stopLossTicks * tickValue), MidpointRounding.AwayFromZero);
                LimitOrder = EnterShortLimit(0, true, positionSize, CurrentRange.FV3Entry, "short entry");
                SetStopLoss("short entry", CalculationMode.Price, CurrentRange.ExtensionHigh, true);
                SetProfitTarget("short entry", CalculationMode.Price, CurrentRange.BR2, true);

                // Sets the range that the trade was taken in.
                TradeRange = CurrentRange;
            }

            if ((Bias == TradeBias.Long && Close[0] > CurrentRange.Low) ||
               (Bias == TradeBias.Short && Close[0] < CurrentRange.High))
                ClearDealingRangeState(PreviousRange);
        }

        private bool CanPlaceTrade()
        {
            var canPlaceLimitOrder = false;

            // We first need to determine if a gap occured through the FV partition
            if (FVGOneCandleCheck())
                canPlaceLimitOrder = true;
            else if (FVGFormed())
                canPlaceLimitOrder = true;
            else if (FVGFormedViaWalkback())
                canPlaceLimitOrder = true;
            else
            {
                // If not, did a volume imbalance form in the FV partition
                if (VolImbsEnabled && VolumeImbalanceFormed())
                    canPlaceLimitOrder = true;
            }

            return canPlaceLimitOrder;
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
                    Bias = TradeBias.Neutral;
                    ConsecutiveLosses = 0;

                    // If this was a winning trade update the state of the dealing range
                    UpdateDealingRangeState(TradeRange);
                }
            }

            if (execution.Order.Name == "Stop loss")
            {
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    PrintWithTime("Stop loss triggered, resetting...");
                    LimitOrder = null;
                    Bias = TradeBias.Neutral;
                    ConsecutiveLosses++;

                    // If this a losing trade update the state of the dealing range.
                    UpdateDealingRangeState(TradeRange);
                    UpdateDealingRangeTradeLoss(TradeRange);
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
                PrintDebugWithTime($"{Bias} Updating range state range: {applicable}");
                applicableRange.LongTradeExecuted = true;
            }

            if (Bias == TradeBias.Short)
            {
                PrintDebugWithTime($"{Bias} Updating range state range: {applicable}");
                applicableRange.ShortTradeExecuted = true;
            }
        }

        private void UpdateDealingRangeTradeLoss(DealingRange applicable)
        {
            var applicableRange = Ranges.FirstOrDefault(r => r == applicable);
            if (Bias == TradeBias.Long)
            {
                PrintDebugWithTime($"{Bias} Updating range state range: {applicable}");
                applicableRange.LongTradeLoss = true;
            }

            if (Bias == TradeBias.Short)
            {
                PrintDebugWithTime($"{Bias} Updating range state range: {applicable}");
                applicableRange.ShortTradeLoss = true;
            }
        }

        private DealingRange CreateDealingRange()
        {
            var newDealingRange = new DealingRange(PO3, Close[0]);
            Ranges.Add(newDealingRange);
            PrintDebugWithTime($"Creating New Range: {newDealingRange.ToString()}");
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

        private bool FVGFormed()
        {
            if (Bias == TradeBias.Long)
            {
                if (Low[0] > High[2] && High[2] < Math.Round(CurrentRange.FV2 / 0.05) * 0.05)
                {
                    PrintDebugWithTime($"{Bias}: Imbalance check pass {High[2]} {Low[0]} {CurrentRange.FV2}");
                    return true;
                }
                else
                {
                    PrintDebugWithTime($"{Bias}: Imbalance check #1 {High[2]} {Low[0]} {CurrentRange.FV2}");
                }
            }

            if (Bias == TradeBias.Short)
            {
                if (High[0] < Low[2] && Low[2] > Math.Round(CurrentRange.FV3 / 0.05) * 0.05)
                {
                    PrintDebugWithTime($"{Bias}: Imbalance check pass {High[0]} {Low[2]} {CurrentRange.FV3}");
                    return true;
                }
                else
                {
                    PrintDebugWithTime($"{Bias}: Imbalance check #1 {High[2]} {Low[0]} {CurrentRange.FV2}");
                }
            }

            return false;
        }

        private bool FVGOneCandleCheck()
        {
            if (Bias == TradeBias.Long)
            {
                if (Close[0] > CurrentRange.FV2)
                {
                    if (Open[1] < CurrentRange.FV1 && Close[1] > CurrentRange.FV2 &&
                       High[2] < Math.Round(CurrentRange.FV2 / 0.05) * 0.05 && // The imbalance must be in the FV partition 	
                       High[2] < Low[0])
                    {
                        PrintDebugWithTime($"{Bias}: One candle imbalance check passed {Open[1]} {Close[1]}");
                        return true;
                    }
                    else
                    {
                        PrintDebugWithTime($"{Bias}: One candle imbalance check #2 {Open[1]} {Close[1]}");
                    }
                }
                else
                {
                    PrintDebugWithTime($"{Bias}: One candle imbalance check {Open[1]} {Close[1]}");
                }
            }

            if (Bias == TradeBias.Short)
            {
                if (Close[0] < CurrentRange.FV3)
                {
                    if (Open[1] > CurrentRange.FV4 && Close[1] < CurrentRange.FV3 && // The middle candle crossed the FV completely
                       Low[2] > Math.Round(CurrentRange.FV3 / 0.05) * 0.05 && // The imbalance must be in the FV partition 	
                       Low[2] > High[0])
                    {
                        PrintDebugWithTime($"{Bias}: One candle imbalance check passed {Open[1]} {Close[1]}");
                        return true;
                    }
                    else
                    {
                        PrintDebugWithTime($"{Bias}: One candle imbalance check #2 {Open[1]} {Close[1]}");
                    }
                }
                else
                {
                    PrintDebugWithTime($"{Bias}: One candle imbalance check #1 {Open[1]} {Close[1]}");
                }
            }

            return false;
        }

        private bool FVGFormedViaWalkback()
        {
            if (Bias == TradeBias.Long)
            {
                int index = 0;
                while (true)
                {
                    // We have gone too far down, return false.
                    if (Low[index] < CurrentRange.FV1)
                    {
                        PrintDebugWithTime($"{Bias}: Imbalance wb check #1 {index} {Open[index]} {Close[index + 1]} {CurrentRange.FV1}");
                        return false;
                    }

                    // All candles must have bodies
                    if (Open[index] == Close[index])
                    {
                        PrintDebugWithTime($"{Bias}: Imbalance wb check #2 {index} {Open[index]} {Close[index]}");
                        return false;
                    }

                    // All candles must be bullish
                    if (index > 0 && (Close[index] < Open[index] || Close[index + 1] < Open[index + 1]))
                    {
                        PrintDebugWithTime($"{Bias}: Imbalance wb check #3 {index} {Open[index]} {Close[index]} {Open[index + 1]} {Close[index + 1]}");
                        return false;
                    }

                    if (Low[index] > High[index + 2] && High[index + 2] < Math.Round(CurrentRange.FV2 / 0.05) * 0.05)
                    {
                        PrintDebugWithTime($"{Bias}: Imbalance wb check passed {index} {High[index + 2]} {Low[index]} {CurrentRange.FV2}");
                        return true;
                    }

                    index++;
                }
            }

            if (Bias == TradeBias.Short)
            {
                int index = 0;
                while (true)
                {
                    // We have gone too far up, return false.
                    if (High[index] > CurrentRange.FV4)
                    {
                        PrintDebugWithTime($"{Bias}: Imbalance wb check #1 {index} {Open[index]} {Close[index + 1]} {CurrentRange.FV4}");
                        return false;
                    }

                    // All candles must have bodies
                    if (Open[index] == Close[index])
                    {
                        PrintDebugWithTime($"{Bias}: Imbalance wb check #2 {index} {Open[index]} {Close[index]}");
                        return false;
                    }

                    // All candles must be bearish (skip the first candle)
                    if (index > 0 && (Close[index] > Open[index] || Close[index + 1] > Open[index + 1]))
                    {
                        PrintDebugWithTime($"{Bias}: Imbalance wb check #3 {index} {Open[index]} {Close[index]} {Open[index + 1]} {Close[index + 1]}");
                        return false;
                    }

                    if (High[index] < Low[index + 2] && Low[index + 2] > Math.Round(CurrentRange.FV3 / 0.05) * 0.05)
                    {
                        PrintDebugWithTime($"{Bias}: Imbalance wb check passed {index} {High[index]} {Low[index + 2]} {CurrentRange.FV3}");
                        return true;
                    }

                    index++;
                }
            }

            return false;
        }

        private bool VolumeImbalanceFormed()
        {
            if (Bias == TradeBias.Long)
            {
                // We might need to walk back a few bars
                // Ignore the first candle as that will always be outside of the FV partition
                int i = 1;
                while (true)
                {
                    // We have gone too far down, return false.
                    if (Open[i] < CurrentRange.FV1)
                    {
                        PrintDebugWithTime($"{Bias}: Vol imb check #1 {i} {Open[i]} {CurrentRange.FV1}");
                        return false;
                    }

                    // Only inspect vol imbs where the opens are in the FV partition
                    if (Open[i] < CurrentRange.FV2)
                    {
                        // All candles must have bodies
                        if (Open[i] == Close[i])
                        {
                            PrintDebugWithTime($"{Bias}: Vol imb check #3 {i} {Open[i]} {Close[i]}");
                            return false;
                        }

                        // Are the previous 2 candles bullish
                        if (Close[i] > Open[i] && Close[i + 1] > Open[i + 1])
                        {
                            // The high of the first candle cannot go beyond the open
                            // of the second candle
                            if (High[i + 1] > Close[i])
                            {
                                PrintDebugWithTime($"{Bias}: Vol imb check #5 {i} {High[i + 1]} {Close[i]}");
                                return false;
                            }

                            // The low of the first candle must be lower than the low
                            // of the second candle
                            // DISABLED FOR NOW
                            //							if(Low[i + 1] > Low[i])
                            //							{
                            //								PrintDebugWithTime($"{Bias}: Vol imb check #6 {i} {Low[i + 1]} {Low[i]}");
                            //								return false;
                            //							}								

                            // Detect vol imbalance
                            if (Open[i] > Close[i + 1])
                            {
                                PrintDebugWithTime($"{Bias}: Vol imb check passed {i} {Open[i]} {Close[i + 1]} {CurrentRange.FV2}");
                                return true;
                            }
                        }
                        else
                        {
                            PrintDebugWithTime($"{Bias}: Vol imb check #4 {i} {Open[i]} {Close[i]} {Open[i + 1]} {Close[i + 1]}");
                            return false;
                        }
                    }
                    else
                    {
                        PrintDebugWithTime($"{Bias}: Vol imb check #2 {i} {Open[i]} {CurrentRange.FV1}");
                    }

                    i++;
                }
            }

            if (Bias == TradeBias.Short)
            {
                // We might need to walk back a few bars
                // Ignore the first candle as that will always be outside of the FV partition				
                int i = 1;
                while (true)
                {
                    // We have gone too far down, return false.
                    if (Open[i] > CurrentRange.FV4)
                    {
                        PrintDebugWithTime($"{Bias}: Vol imb check #1 {i} {Open[i]} {CurrentRange.FV4}");
                        return false;
                    }

                    // Only inspect volume imbs where the opens are in the FV partition
                    if (Open[i] > CurrentRange.FV3)
                    {
                        // All candles mst have bodies
                        if (Open[i] == Close[i])
                        {
                            PrintDebugWithTime($"{Bias}: Vol imb check #3 {i} {Open[i]} {Close[i]}");
                            return false;
                        }

                        // Are the previous 2 candles bearish
                        if (Close[i] < Open[i] && Close[i + 1] < Open[i + 1])
                        {
                            // The low of the first candle cannot go beyond the close
                            // of the second candle
                            if (Low[i + 1] < Close[i])
                            {
                                PrintDebugWithTime($"{Bias}: Vol imb check #5 {i} {Low[i + 1]} {Close[i]}");
                                return false;
                            }

                            // The high of the first candle must be higher than the high
                            // of the second candle
                            // DISABLED FOR NOW
                            //							if(High[i + 1] > High[i])
                            //							{
                            //								PrintDebugWithTime($"{Bias}: Vol imb check #6 {i} {High[i + 1]} {High[i]}");
                            //								return false;
                            //							}								

                            // Detect vol imbalance
                            if (Open[i] < Close[i + 1])
                            {
                                PrintDebugWithTime($"{Bias}: Vol imb check passed {i} {Open[i]} {Close[i]} {Open[i + 1]} {Close[i + 1]}");
                                return true;
                            }
                        }
                        else
                        {
                            PrintDebugWithTime($"{Bias}: Vol imb check #4 {i} {Open[i]} {Close[i]} {Open[i + 1]} {Close[i + 1]}");
                            return false;
                        }
                    }
                    else
                    {
                        PrintDebugWithTime($"{Bias}: Vol imb check #2 {i} {Open[i]} {CurrentRange.FV3}");
                    }

                    i++;
                }
            }

            return false;
        }

        private bool FilterByEvents()
        {
            var newsEvent = NewsEvents.FirstOrDefault(n => Time[0].Date == n.EventTime.Date && Time[0].TimeOfDay >= n.EventTime.TimeOfDay);
            return newsEvent != null;
        }

        public void PrintWithTime(string toPrint)
        {
            Print($"{(ToTime(Time[0]))} {toPrint}");
        }

        public void PrintDebugWithTime(string toPrint)
        {
            if (IsDebug)
                Print($"{(ToTime(Time[0]))} {toPrint}");
        }
    }
}