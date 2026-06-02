#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;
#endregion

//This namespace holds Strategies in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public class AlgoPO3v2 : AlgoPO3Base
    {
        [NinjaScriptProperty]
        [Display(Name = "Adjust Risk on Consec Loss", Order = 18, GroupName = "Parameters")]
        public bool AdjustRiskBasedOnLosses { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Break Even", Order = 19, GroupName = "Parameters")]
        public bool BreakEvenEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Vol Imbs Enabled", Order = 20, GroupName = "Parameters")]
        public bool VolImbsEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Invalid Range Level", Order = 21, GroupName = "Parameters")]
        public int InvalidRangeLevel { get; set; }

        private List<DealingRange> Ranges { get; set; }

        private DealingRange CurrentRange { get; set; }
        private DealingRange PreviousRange { get; set; }
        private TradeBias tradeBias;
        private DealingRange TradeRange { get; set; }

        private int ConsecutiveLosses { get; set; }

        private DateTime? lastInitialization;
        private NewsFilter newsFilter;

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
                SessionStart = 1;
                SessionEnd = 13;
                PositionSize = 4;
                IsOverlay = true;

                BlockEndHours = "3,9,12";
                EdgeHours = "1,3,9,11";
                EdgeHourMultiplier = 1.5;
                SkipHours = "4,5,7,10";
                SkipMonths = "8";

                IncludeCPIWeek = true;
                IncludeFOMCWeek = false;
                IncludeNFPWeek = true;
                IncludeNoEventsWeek = true;
            }
            else if (State == State.Configure)
            {
                //AddDataSeries("MNQ 06-26", BarsPeriodType.Minute, 5);

                Ranges = [];

                Events = NewsEvents.Events();

                tradeBias = TradeBias.Neutral;

                newsFilter = new NewsFilter(IncludeCPIWeek, IncludeFOMCWeek, IncludeNFPWeek, IncludeNoEventsWeek);
            }
            else if (State == State.DataLoaded)
            {
                BlockEndHoursParsed = [.. BlockEndHours.Split(',').Select((i) => int.Parse(i) + SessionTimeOffset)];
                EdgeHoursParsed = [.. EdgeHours.Split(',').Select((i) => int.Parse(i) + SessionTimeOffset)];
                SkipHoursParsed = [.. SkipHours.Split(',').Select((i) => int.Parse(i) + SessionTimeOffset)];

                AdjustedSessionStart = SessionStart + SessionTimeOffset;
                AdjustedSessionEnd = SessionEnd + SessionTimeOffset;

                PrintDebug($"Session Start: {AdjustedSessionStart}, Session End: {AdjustedSessionEnd}");
                PrintDebug($"BlockEndHoursParsed: {string.Join(",", BlockEndHoursParsed)}");
                PrintDebug($"EdgeHoursParsed: {string.Join(",", EdgeHoursParsed)}");
                PrintDebug($"SkipHoursParsed: {string.Join(",", SkipHoursParsed)}");
            }
        }

        protected override void OnBarUpdate()
        {
            if (lastInitialization == null || lastInitialization?.Date != Time[0].Date)
            {
                PerformCleanupBeforeSessionStart();
                lastInitialization = Time[0].Date;
                return;
            }

            if (SkipMonths.Split(',').Contains(Time[0].Month.ToString()))
                return;

            if (!Events.IsInWeek(newsFilter, Time[0]))
            {
                //PrintDebug($"Skipping as NFP {newsFilter.IncludeNFP} CPI {newsFilter.IncludeCPI} FOMC {newsFilter.IncludeFOMC} NoEvents {newsFilter.IncludeNoEvents}...");
                return;
            }

            // Filter by news events
            if (FilterByEvents() && Position.MarketPosition == MarketPosition.Flat)
                return;

            // Adjust time for NY/UK BST overlap
            var adjustSessionEnd = AdjustSessionEnd();

            // If the session has ended stop processing.
            if (Time[0].Hour >= adjustSessionEnd && Position.MarketPosition == MarketPosition.Flat)
            {
                CancelLimitOrder();
                return;
            }

            // If no previous range has been set it means that we never broke range
            // and such there will be no bias or previous limit order.
            if (CurrentRange != null && PreviousRange != null)
            {
                var updateAndCancel = false;
                if (tradeBias == TradeBias.Short && Low[0] <= CurrentRange.InvalidLevel(tradeBias, InvalidRangeLevel) && !CurrentRange.ShortTradeExecuted)
                    updateAndCancel = true;

                if (tradeBias == TradeBias.Long && High[0] >= CurrentRange.InvalidLevel(tradeBias, InvalidRangeLevel) && !CurrentRange.LongTradeExecuted)
                    updateAndCancel = true;

                // A trade has been attempted on this leg so ignore next time.
                if (updateAndCancel)
                {
                    PrintDebug($"{tradeBias}: price went past TP, updating range state.");
                    UpdateDealingRangeState(CurrentRange);

                    if (PendingOrder != null)
                    {
                        CancelOrder(PendingOrder);
                        PendingOrder = null;

                        PrintDebug($"{tradeBias} limit order cancelled.");
                    }
                }
            }

            // If a position is open or a limit order is pending, defer processing.
            if (Position.MarketPosition != MarketPosition.Flat || PendingOrder != null)
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
                return; // We can return as no signals will be created on strategy startup.
            }

            // Determine if we are still in the same dealing range.
            var found = Ranges.FirstOrDefault(r => r.IsInRange(Close[0]));
            if (found == null)
            {
                found = CreateDealingRange();
                tradeBias = TradeBias.Neutral;
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

            // The strategy only runs from a given time in the morning (8am)
            if (Time[0].Hour < AdjustedSessionStart)
                return;

            // Filter time of day and cancel if we are flat.
            if (SkipHoursParsed.Contains(Time[0].Hour))
            {
                CancelLimitOrder();
                return;
            }

            // If a previous range dealing range has been set then a break into
            // a new range has occurred. We then determine the trade bias.
            if (CurrentRange.Low == PreviousRange.High)
                tradeBias = TradeBias.Long;

            if (CurrentRange.High == PreviousRange.Low)
                tradeBias = TradeBias.Short;

            // The candle's low must be above the top of the FV partition
            // Filter out scenarios where the high has already reach the TP (BR2)
            if (tradeBias == TradeBias.Long && !CurrentRange.LongTradeExecuted && Low[0] > CurrentRange.FV2)// && High[0] < CurrentRange.MB2)
            {
                // Disabled for now
                // if(CurrentRange.LongTradeLoss)
                //	return;

                if (!CanPlaceTrade())
                {
                    PrintDebug($"{tradeBias} entry ignored");
                    return;
                }

                PrintDebug($"[LONG_LIMIT_ENTRY] at {CurrentRange.FV2Entry}, Stop: {CurrentRange.ExtensionLow}, TP: {CurrentRange.BR3}");

                PendingOrder = EnterLongLimit(0, true, PositionSize, CurrentRange.FV2Entry, "long entry");
                SetStopLoss("long entry", CalculationMode.Price, CurrentRange.ExtensionLow, true);
                SetProfitTarget("long entry", CalculationMode.Price, CurrentRange.BR3);

                // Sets the range that the trade was taken in.
                TradeRange = CurrentRange;
            }

            // The candle's high must be below the top of the FV partition
            // Filter out scenarios where the low has already reached the TP (BR1)
            if (tradeBias == TradeBias.Short && !CurrentRange.ShortTradeExecuted && High[0] < CurrentRange.FV3)// && Low[0] > CurrentRange.MB1)
            {
                // Disabled for now				
                // if(CurrentRange.ShortTradeLoss)
                //	return;

                if (!CanPlaceTrade())
                {
                    PrintDebug($"{tradeBias} entry ignored");
                    return;
                }

                PrintDebug($"[SHORT_LIMIT_ENTRY] at {CurrentRange.FV3Entry}, Stop: {CurrentRange.ExtensionHigh}, TP: {CurrentRange.BR2}");

                PendingOrder = EnterShortLimit(0, true, PositionSize, CurrentRange.FV3Entry, "short entry");
                SetStopLoss("short entry", CalculationMode.Price, CurrentRange.ExtensionHigh, true);
                SetProfitTarget("short entry", CalculationMode.Price, CurrentRange.BR2);

                // Sets the range that the trade was taken in.
                TradeRange = CurrentRange;
            }

            if ((tradeBias == TradeBias.Long && Close[0] > CurrentRange.Low) ||
               (tradeBias == TradeBias.Short && Close[0] < CurrentRange.High))
                ClearDealingRangeState(PreviousRange);
        }

        private void PerformCleanupBeforeSessionStart()
        {
            PrintDebug("[SESSION_START] resetting state");

            Ranges.Clear();

            CancelLimitOrder();

            TradeRange = null;
            CurrentRange = null;
            PreviousRange = null;

            tradeBias = TradeBias.Neutral;
        }

        private bool CanPlaceTrade()
        {

            // We first need to determine if a gap occured through the FV partition
            var fvgOneCandle = false;// FVGOneCandleCheck();
            var fvg = FVGFormed();
            var fvgWalkBack = false;// FVGFormedViaWalkback();

            //{
            //    // If not, did a volume imbalance form in the FV partition
            //    if (VolImbsEnabled && VolumeImbalanceFormed())
            //        canPlaceLimitOrder = true;
            //}

            return fvgOneCandle || fvg || fvgWalkBack;
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (marketPosition != MarketPosition.Flat)
                return;

            if (SystemPerformance.AllTrades.Count == 0)
                return;

            var trade = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
            var winLoss = trade.ProfitCurrency >= 0 ? "Win" : "Loss";
            PrintTrade($"{trade.Entry.Time},{tradeBias},n/a,{winLoss},{trade.ProfitCurrency},{trade.Quantity}");

            PendingOrder = null;
            tradeBias = TradeBias.Neutral;

            if (trade.ProfitCurrency >= 0)
            {
                ConsecutiveLosses = 0;

                // If this was a winning trade update the state of the dealing range
                UpdateDealingRangeState(TradeRange);
            }
            else
            {
                ConsecutiveLosses++;

                // If this a losing trade update the state of the dealing range.
                UpdateDealingRangeState(TradeRange);
                UpdateDealingRangeTradeLoss(TradeRange);
            }
        }

        private void UpdateDealingRangeState(DealingRange applicable)
        {
            var applicableRange = Ranges.FirstOrDefault(r => r == applicable);
            if (tradeBias == TradeBias.Long)
            {
                PrintDebug($"{tradeBias} Updating range state range: {applicable}");
                applicableRange.LongTradeExecuted = true;
            }

            if (tradeBias == TradeBias.Short)
            {
                PrintDebug($"{tradeBias} Updating range state range: {applicable}");
                applicableRange.ShortTradeExecuted = true;
            }
        }

        private void UpdateDealingRangeTradeLoss(DealingRange applicable)
        {
            var applicableRange = Ranges.FirstOrDefault(r => r == applicable);
            if (tradeBias == TradeBias.Long)
            {
                PrintDebug($"{tradeBias} Updating range state range: {applicable}");
                applicableRange.LongTradeLoss = true;
            }

            if (tradeBias == TradeBias.Short)
            {
                PrintDebug($"{tradeBias} Updating range state range: {applicable}");
                applicableRange.ShortTradeLoss = true;
            }
        }

        private DealingRange CreateDealingRange()
        {
            var newDealingRange = new DealingRange(PO3, Close[0]);
            Ranges.Add(newDealingRange);
            PrintDebug($"Creating New Range: {newDealingRange}");
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
                PrintDebug($"Clearing state for range: {applicable}");
            }
        }

        private bool FVGFormed()
        {
            if (tradeBias == TradeBias.Long)
            {
                if (Low[0] > High[2] && High[2] < Math.Round(CurrentRange.FV2 / 0.05) * 0.05)
                {
                    PrintDebug($"{tradeBias}: Imbalance check pass {High[2]} {Low[0]} {CurrentRange.FV2}");
                    return true;
                }
                else
                {
                    PrintDebug($"{tradeBias}: Imbalance check #1 {High[2]} {Low[0]} {CurrentRange.FV2}");
                }
            }

            if (tradeBias == TradeBias.Short)
            {
                if (High[0] < Low[2] && Low[2] > Math.Round(CurrentRange.FV3 / 0.05) * 0.05)
                {
                    PrintDebug($"{tradeBias}: Imbalance check pass {High[0]} {Low[2]} {CurrentRange.FV3}");
                    return true;
                }
                else
                {
                    PrintDebug($"{tradeBias}: Imbalance check #1 {High[2]} {Low[0]} {CurrentRange.FV2}");
                }
            }

            return false;
        }

        private bool FVGOneCandleCheck()
        {
            if (tradeBias == TradeBias.Long)
            {
                if (Close[0] > CurrentRange.FV2)
                {
                    if (Open[1] < CurrentRange.FV1 && Close[1] > CurrentRange.FV2 &&
                       High[2] < Math.Round(CurrentRange.FV2 / 0.05) * 0.05 && // The imbalance must be in the FV partition 	
                       High[2] < Low[0])
                    {
                        PrintDebug($"{tradeBias}: One candle imbalance check passed {Open[1]} {Close[1]}");
                        return true;
                    }
                    else
                    {
                        PrintDebug($"{tradeBias}: One candle imbalance check #2 {Open[1]} {Close[1]}");
                    }
                }
                else
                {
                    PrintDebug($"{tradeBias}: One candle imbalance check {Open[1]} {Close[1]}");
                }
            }

            if (tradeBias == TradeBias.Short)
            {
                if (Close[0] < CurrentRange.FV3)
                {
                    if (Open[1] > CurrentRange.FV4 && Close[1] < CurrentRange.FV3 && // The middle candle crossed the FV completely
                       Low[2] > Math.Round(CurrentRange.FV3 / 0.05) * 0.05 && // The imbalance must be in the FV partition 	
                       Low[2] > High[0])
                    {
                        PrintDebug($"{tradeBias}: One candle imbalance check passed {Open[1]} {Close[1]}");
                        return true;
                    }
                    else
                    {
                        PrintDebug($"{tradeBias}: One candle imbalance check #2 {Open[1]} {Close[1]}");
                    }
                }
                else
                {
                    PrintDebug($"{tradeBias}: One candle imbalance check #1 {Open[1]} {Close[1]}");
                }
            }

            return false;
        }

        private bool FVGFormedViaWalkback()
        {
            if (tradeBias == TradeBias.Long)
            {
                int index = 0;
                while (true)
                {
                    // We have gone too far down, return false.
                    if (Low[index] < CurrentRange.FV1)
                    {
                        PrintDebug($"{tradeBias}: Imbalance wb check #1 {index} {Open[index]} {Close[index + 1]} {CurrentRange.FV1}");
                        return false;
                    }

                    // All candles must have bodies
                    if (Open[index] == Close[index])
                    {
                        PrintDebug($"{tradeBias}: Imbalance wb check #2 {index} {Open[index]} {Close[index]}");
                        return false;
                    }

                    // All candles must be bullish
                    if (index > 0 && (Close[index] < Open[index] || Close[index + 1] < Open[index + 1]))
                    {
                        PrintDebug($"{tradeBias}: Imbalance wb check #3 {index} {Open[index]} {Close[index]} {Open[index + 1]} {Close[index + 1]}");
                        return false;
                    }

                    if (Low[index] > High[index + 2] && High[index + 2] < Math.Round(CurrentRange.FV2 / 0.05) * 0.05)
                    {
                        PrintDebug($"{tradeBias}: Imbalance wb check passed {index} {High[index + 2]} {Low[index]} {CurrentRange.FV2}");
                        return true;
                    }

                    index++;
                }
            }

            if (tradeBias == TradeBias.Short)
            {
                int index = 0;
                while (true)
                {
                    // We have gone too far up, return false.
                    if (High[index] > CurrentRange.FV4)
                    {
                        PrintDebug($"{tradeBias}: Imbalance wb check #1 {index} {Open[index]} {Close[index + 1]} {CurrentRange.FV4}");
                        return false;
                    }

                    // All candles must have bodies
                    if (Open[index] == Close[index])
                    {
                        PrintDebug($"{tradeBias}: Imbalance wb check #2 {index} {Open[index]} {Close[index]}");
                        return false;
                    }

                    // All candles must be bearish (skip the first candle)
                    if (index > 0 && (Close[index] > Open[index] || Close[index + 1] > Open[index + 1]))
                    {
                        PrintDebug($"{tradeBias}: Imbalance wb check #3 {index} {Open[index]} {Close[index]} {Open[index + 1]} {Close[index + 1]}");
                        return false;
                    }

                    if (High[index] < Low[index + 2] && Low[index + 2] > Math.Round(CurrentRange.FV3 / 0.05) * 0.05)
                    {
                        PrintDebug($"{tradeBias}: Imbalance wb check passed {index} {High[index]} {Low[index + 2]} {CurrentRange.FV3}");
                        return true;
                    }

                    index++;
                }
            }

            return false;
        }

        private bool VolumeImbalanceFormed()
        {
            if (tradeBias == TradeBias.Long)
            {
                // We might need to walk back a few bars
                // Ignore the first candle as that will always be outside of the FV partition
                int i = 1;
                while (true)
                {
                    // We have gone too far down, return false.
                    if (Open[i] < CurrentRange.FV1)
                    {
                        PrintDebug($"{tradeBias}: Vol imb check #1 {i} {Open[i]} {CurrentRange.FV1}");
                        return false;
                    }

                    // Only inspect vol imbs where the opens are in the FV partition
                    if (Open[i] < CurrentRange.FV2)
                    {
                        // All candles must have bodies
                        if (Open[i] == Close[i])
                        {
                            PrintDebug($"{tradeBias}: Vol imb check #3 {i} {Open[i]} {Close[i]}");
                            return false;
                        }

                        // Are the previous 2 candles bullish
                        if (Close[i] > Open[i] && Close[i + 1] > Open[i + 1])
                        {
                            // The high of the first candle cannot go beyond the open
                            // of the second candle
                            if (High[i + 1] > Close[i])
                            {
                                PrintDebug($"{tradeBias}: Vol imb check #5 {i} {High[i + 1]} {Close[i]}");
                                return false;
                            }

                            // The low of the first candle must be lower than the low
                            // of the second candle
                            // DISABLED FOR NOW
                            //							if(Low[i + 1] > Low[i])
                            //							{
                            //								PrintDebug($"{Bias}: Vol imb check #6 {i} {Low[i + 1]} {Low[i]}");
                            //								return false;
                            //							}								

                            // Detect vol imbalance
                            if (Open[i] > Close[i + 1])
                            {
                                PrintDebug($"{tradeBias}: Vol imb check passed {i} {Open[i]} {Close[i + 1]} {CurrentRange.FV2}");
                                return true;
                            }
                        }
                        else
                        {
                            PrintDebug($"{tradeBias}: Vol imb check #4 {i} {Open[i]} {Close[i]} {Open[i + 1]} {Close[i + 1]}");
                            return false;
                        }
                    }
                    else
                    {
                        PrintDebug($"{tradeBias}: Vol imb check #2 {i} {Open[i]} {CurrentRange.FV1}");
                    }

                    i++;
                }
            }

            if (tradeBias == TradeBias.Short)
            {
                // We might need to walk back a few bars
                // Ignore the first candle as that will always be outside of the FV partition				
                int i = 1;
                while (true)
                {
                    // We have gone too far down, return false.
                    if (Open[i] > CurrentRange.FV4)
                    {
                        PrintDebug($"{tradeBias}: Vol imb check #1 {i} {Open[i]} {CurrentRange.FV4}");
                        return false;
                    }

                    // Only inspect volume imbs where the opens are in the FV partition
                    if (Open[i] > CurrentRange.FV3)
                    {
                        // All candles mst have bodies
                        if (Open[i] == Close[i])
                        {
                            PrintDebug($"{tradeBias}: Vol imb check #3 {i} {Open[i]} {Close[i]}");
                            return false;
                        }

                        // Are the previous 2 candles bearish
                        if (Close[i] < Open[i] && Close[i + 1] < Open[i + 1])
                        {
                            // The low of the first candle cannot go beyond the close
                            // of the second candle
                            if (Low[i + 1] < Close[i])
                            {
                                PrintDebug($"{tradeBias}: Vol imb check #5 {i} {Low[i + 1]} {Close[i]}");
                                return false;
                            }

                            // The high of the first candle must be higher than the high
                            // of the second candle
                            // DISABLED FOR NOW
                            //							if(High[i + 1] > High[i])
                            //							{
                            //								PrintDebug($"{Bias}: Vol imb check #6 {i} {High[i + 1]} {High[i]}");
                            //								return false;
                            //							}								

                            // Detect vol imbalance
                            if (Open[i] < Close[i + 1])
                            {
                                PrintDebug($"{tradeBias}: Vol imb check passed {i} {Open[i]} {Close[i]} {Open[i + 1]} {Close[i + 1]}");
                                return true;
                            }
                        }
                        else
                        {
                            PrintDebug($"{tradeBias}: Vol imb check #4 {i} {Open[i]} {Close[i]} {Open[i + 1]} {Close[i + 1]}");
                            return false;
                        }
                    }
                    else
                    {
                        PrintDebug($"{tradeBias}: Vol imb check #2 {i} {Open[i]} {CurrentRange.FV3}");
                    }

                    i++;
                }
            }

            return false;
        }
    }
}