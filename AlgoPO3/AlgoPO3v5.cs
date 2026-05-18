#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;
#endregion

//This namespace holds Strategies in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public class AlgoPO3v5 : Strategy
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
        [Display(Name = "Daily Loss Streak", Order = 5, GroupName = "Parameters")]
        public int DailyLossStreak { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Cumm. Loss Streak", Order = 5, GroupName = "Parameters")]
        public int LossStreak { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Risk", Order = 5, GroupName = "Parameters")]
        public double Risk { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session Start", Order = 5, GroupName = "Parameters")]
        public int SessionStart { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session End", Order = 5, GroupName = "Parameters")]
        public int SessionEnd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Entry Level", Order = 5, GroupName = "Parameters")]
        public string EntryLevel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Edge Hour", Order = 5, GroupName = "Parameters")]
        public int EdgeHour { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Edge Multiplier", Order = 5, GroupName = "Parameters")]
        public double EdgeHourMultiplier { get; set; }

        private Order LimitOrder { get; set; }
        private List<DealingRange> Ranges { get; set; }

        private DealingRange CurrentRange { get; set; }
        private DealingRange PreviousRange { get; set; }
        private TradeBias Bias { get; set; }
        private RangeBias RangeBias { get; set; }
        private InvalidRange InvalidRange { get; set; }

        private double StopLossPrice { get; set; }
        private double TakeProfitPrice { get; set; }
        private double TakeProfitPriceThreshold { get; set; }

        private List<NewsEvent> NewsEvents { get; set; }

        private int ConsecutiveLosses { get; set; }

        private double lastFractalHigh = 0;
        private double lastFractalLow = 0;
        private bool bearishBOSDetected = false;
        private bool bullishBOSDetected = false;
        private bool rangeBreakDetected = false;

        private SharedBorderTrade sharedBorderTrade;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"Enter the description for your new custom Strategy here.";
                Name = "FassaPO3 v5";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                IsExitOnSessionCloseStrategy = true;
                PO3 = 81;
                SessionStart = 80000;
                SessionEnd = 140000;
                Risk = 300;
                DailyLossStreak = 2;
                IsOverlay = true;
                EntryLevel = "CE";
                EdgeHour = 9;
                EdgeHourMultiplier = 1.5;
            }
            else if (State == State.Configure)
            {
                AddDataSeries("MNQ 06-26", BarsPeriodType.Minute, 5);

                Ranges = [];

                NewsEvents = new List<NewsEvent>();

                // June 2025
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 06, 04, 13, 15, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 06, 05, 13, 00, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 06, 06, 13, 00, 0)));   // NFP				
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 06, 11, 13, 00, 0)));   // CPI
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 06, 12, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 06, 17, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 06, 18, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 06, 26, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 06, 27, 13, 30, 0)));

                // July 2025
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 07, 02, 13, 15, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 07, 03, 13, 00, 0)));   // NFP
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 07, 10, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 07, 15, 13, 00, 0)));   // CPI
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 07, 16, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 07, 17, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 07, 24, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 07, 30, 13, 15, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 07, 31, 13, 30, 0)));

                // August 2025
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 08, 01, 13, 00, 0)));   // NFP
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 08, 7, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 08, 12, 13, 00, 0)));   // CPI
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 08, 14, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 08, 15, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 08, 21, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 08, 28, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 08, 29, 13, 30, 0)));

                NewsEvents.Add(new NewsEvent(new DateTime(2025, 09, 05, 13, 00, 0)));   // CPI
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 09, 11, 13, 00, 0)));   // CPI

                NewsEvents.Add(new NewsEvent(new DateTime(2025, 10, 24, 13, 00, 0)));   // CPI

                NewsEvents.Add(new NewsEvent(new DateTime(2025, 11, 20, 13, 00, 0)));   // NFP

                //				NewsEvents.Add(new NewsEvent(new DateTime(2025, 12, 03, 13, 15, 0)));
                //				NewsEvents.Add(new NewsEvent(new DateTime(2025, 12, 04, 13, 30, 0)));				
                //				NewsEvents.Add(new NewsEvent(new DateTime(2025, 12, 09, 13, 15, 0)));
                //				NewsEvents.Add(new NewsEvent(new DateTime(2025, 12, 10, 13, 30, 0)));
                //				NewsEvents.Add(new NewsEvent(new DateTime(2025, 12, 11, 13, 30, 0)));	

                NewsEvents.Add(new NewsEvent(new DateTime(2025, 12, 16, 13, 00, 0)));   // NFP news from 13:15
                NewsEvents.Add(new NewsEvent(new DateTime(2025, 12, 18, 13, 00, 0)));   // CPI

                // Add xmas period
                for (int i = 22; i <= 31; i++)
                {
                    NewsEvents.Add(new NewsEvent(new DateTime(2025, 12, i, 08, 00, 0)));
                }

                // Skip start of January
                for (int i = 1; i <= 4; i++)
                {
                    NewsEvents.Add(new NewsEvent(new DateTime(2026, 01, i, 08, 00, 0)));
                }

                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 7, 13, 15, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 8, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 9, 13, 00, 0)));     // NFP
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 13, 13, 00, 0)));    // CPI
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 14, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 15, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 22, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 29, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 30, 13, 30, 0)));

                NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 4, 13, 15, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 5, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 10, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 11, 13, 00, 0)));    // NFP
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 12, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 13, 13, 00, 0)));    // CPI
                                                                                        //				NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 19, 13, 30, 0)));
                                                                                        //				NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 20, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 26, 13, 30, 0)));
                //				NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 27, 13, 30, 0)));

                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 4, 13, 15, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 5, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 6, 13, 00, 0)));     // NFP
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 10, 12, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 11, 12, 00, 0)));    // CPI
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 12, 12, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 13, 12, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 18, 12, 30, 0)));    // FOMC
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 19, 12, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 23, 11, 06, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 26, 12, 30, 0)));

                NewsEvents.Add(new NewsEvent(new DateTime(2026, 4, 1, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 4, 2, 13, 00, 0)));     // NFP

                // Easter Friday and Monday
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 4, 3, 08, 00, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 4, 6, 08, 00, 0)));

                ConsecutiveLosses = 0;

                Bias = TradeBias.Neutral;
                RangeBias = RangeBias.Neutral;
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar >= 5 && BarsInProgress == 1)
            {
                DetectFractal();
            }

            if (BarsInProgress != 0)
                return;

            DetectFractalBOS();

            // The strategy only runs from a given time in the morning (8am)
            if (ToTime(Time[0]) < SessionStart)
                return;

            // Clear any invalid ranges after the asia session
            if (Time[0].Hour == 6 && Time[0].Minute == 0)
            {
                InvalidRange = null;
                sharedBorderTrade = null;
            }

            var adjustSessionEnd = SessionEnd;

            // Adjust time for NY/UK BST overlap
            if ((Time[0].Date >= new DateTime(2025, 3, 9) && Time[0].Date <= new DateTime(2025, 3, 30)) ||
                (Time[0].Date >= new DateTime(2025, 10, 26) && Time[0].Date <= new DateTime(2025, 11, 2)) ||
                (Time[0].Date >= new DateTime(2026, 3, 9) && Time[0].Date <= new DateTime(2026, 3, 29)))
                adjustSessionEnd = 130000;

            // If the session has ended do some cleanup if neccessary.
            if ((ToTime(Time[0]) > adjustSessionEnd || FilterByEvents()) && Position.MarketPosition == MarketPosition.Flat)
            {
                if (Ranges.Count > 0)
                {
                    PrintDebugWithTime("End of session, clearing ranges...");
                    Ranges.Clear();
                }

                ResetStateAndCancel();

                CurrentRange = null;
                PreviousRange = null;

                InvalidRange = null;

                lastFractalHigh = 0;
                lastFractalLow = 0;

                bearishBOSDetected = false;
                bullishBOSDetected = false;

                rangeBreakDetected = false;

                sharedBorderTrade = null;

                return;
            }

            // BE/Close trade just before NY open
            BreakEvenOrCloseTrade();

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
                Bias = TradeBias.Neutral;
            }

            // Always process range breaks
            if (found != CurrentRange)
            {
                if (PreviousRange != CurrentRange)
                {
                    PreviousRange = CurrentRange;
                    CurrentRange = found;

                    PrintDebugWithTime($"New range set {CurrentRange}");

                    rangeBreakDetected = true;
                }
            }

            if (DetectAndCheckInvalidRange())
            {
                // Set this back to false until the invalid range is cleared
                rangeBreakDetected = false;
                return;
            }

            // Filter time of day and cancel if we are flat.
            if (Time[0].Hour == 10 && Position.MarketPosition == MarketPosition.Flat)
            {
                ResetStateAndCancel();
                return;
            }

            if (!rangeBreakDetected)
                return;

            if (Position.MarketPosition == MarketPosition.Flat && LimitOrder == null)
            {
                if (bullishBOSDetected && CurrentRange.Low == PreviousRange.High)
                {
                    RangeBias = RangeBias.Bullish;

                    Bias = TradeBias.Long;

                    PrintDebugWithTime($"Range bias {RangeBias}: Trade bias set to {Bias}");
                }

                if (bullishBOSDetected && CurrentRange.High == PreviousRange.Low)
                {
                    RangeBias = RangeBias.Bearish;

                    Bias = TradeBias.Long;

                    PrintDebugWithTime($"Range bias {RangeBias}: Trade bias set to {Bias}");
                }

                if (bearishBOSDetected && CurrentRange.High == PreviousRange.Low)
                {
                    RangeBias = RangeBias.Bearish;

                    Bias = TradeBias.Short;

                    PrintDebugWithTime($"Range bias {RangeBias}: Trade bias set to {Bias}");
                }

                if (bearishBOSDetected && CurrentRange.Low == PreviousRange.High)
                {
                    RangeBias = RangeBias.Bullish;

                    Bias = TradeBias.Short;

                    PrintDebugWithTime($"Range bias {RangeBias}: Trade bias set to {Bias}");
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
                    PrintDebugWithTime($"{Bias}: price already went to TP {TakeProfitPrice}, resetting...");

                    CancelOrder(LimitOrder);
                    LimitOrder = null;

                    RangeBias = RangeBias.Neutral;

                    TakeProfitPrice = 0;
                    StopLossPrice = 0;

                    rangeBreakDetected = false;

                    //UpdateDealingRangeState(CurrentRange);
                }

                return;
            }

            // We are currently in a trade.
            if (Position.MarketPosition != MarketPosition.Flat || LimitOrder != null)
            {
                return;
            }

            if (RangeBias == RangeBias.Neutral)
            {
                return;
            }

            var entryPrice = 0.0; //TODO are we rounding entries to tick level?

            if (RangeBias == RangeBias.Bearish)
            {
                if (Bias == TradeBias.Long)
                {
                    // Check the previous range flag:
                    if (PreviousRange.LongTradeExecuted)
                    {
                        PrintWithTime($"Skipping {RangeBias}-{Bias} entry as previously executed");
                        rangeBreakDetected = false;
                        return;
                    }

                    entryPrice = (EntryLevel == "CE" ? CurrentRange.OBCE2 : CurrentRange.ExtensionHigh) + TickSize;
                    StopLossPrice = CurrentRange.BR4 + Math.Round((CurrentRange.FV3 - CurrentRange.BR4) * 0.33, 2);
                    TakeProfitPrice = PreviousRange.MBCE1;
                    TakeProfitPriceThreshold = PreviousRange.BR2;
                }

                if (Bias == TradeBias.Short)
                {
                    // Check the current range flag:
                    if (CurrentRange.ShortTradeExecuted)
                    {
                        PrintWithTime($"Skipping {RangeBias}-{Bias} entry as previously executed");
                        rangeBreakDetected = false;
                        return;
                    }

                    entryPrice = (EntryLevel == "CE" ? CurrentRange.ExtensionHighCE : CurrentRange.ExtensionHigh) - TickSize; ;
                    StopLossPrice = PreviousRange.BR1 - Math.Round((PreviousRange.BR1 - PreviousRange.FV2) * 0.33, 2);
                    TakeProfitPrice = CurrentRange.MBCE2;
                    TakeProfitPriceThreshold = CurrentRange.BR3;
                }
            }

            if (RangeBias == RangeBias.Bullish)
            {
                if (Bias == TradeBias.Long)
                {
                    // Check the current range flag:
                    if (CurrentRange.LongTradeExecuted)
                    {
                        PrintWithTime($"Skipping {RangeBias}-{Bias} entry as previously executed");
                        rangeBreakDetected = false;
                        return;
                    }

                    entryPrice = (EntryLevel == "CE" ? CurrentRange.ExtensionLowCE : CurrentRange.ExtensionLow) + TickSize;
                    StopLossPrice = PreviousRange.BR4 + Math.Round((PreviousRange.FV3 - PreviousRange.BR4) * 0.33, 2);
                    TakeProfitPrice = CurrentRange.MBCE1;
                    TakeProfitPriceThreshold = CurrentRange.BR2;
                }

                if (Bias == TradeBias.Short)
                {
                    // Check the range below's flag:
                    if (PreviousRange.ShortTradeExecuted)
                    {
                        PrintWithTime($"Skipping {RangeBias}-{Bias} entry as previously executed");
                        rangeBreakDetected = false;
                        return;
                    }

                    entryPrice = (CurrentRange.OBCE1) - TickSize;
                    StopLossPrice = CurrentRange.BR1 - Math.Round((CurrentRange.BR1 - CurrentRange.FV2) * 0.33, 2);
                    TakeProfitPrice = PreviousRange.MBCE2;
                    TakeProfitPriceThreshold = PreviousRange.BR3;
                }
            }

            if (Bias == TradeBias.Long)
            {
                if (Close[0] > entryPrice)
                {
                    LimitOrder = EnterLongLimit(0, true, CalculatePositionSize(entryPrice - StopLossPrice), entryPrice, "long entry");
                    PrintWithTime($"{Bias} limit entry : {entryPrice} {Close[0]}");
                }
                else
                {
                    LimitOrder = EnterLongStopMarket(0, true, CalculatePositionSize(entryPrice - StopLossPrice), entryPrice, "long entry");
                    PrintWithTime($"{Bias} stop market entry : {entryPrice} {Close[0]}");
                }

                SetStopLoss("long entry", CalculationMode.Price, StopLossPrice, true);
                PrintDebugWithTime($"Stop level (LVLevel) {StopLossPrice}");

                SetProfitTarget("long entry", CalculationMode.Price, TakeProfitPrice, true);
            }

            if (Bias == TradeBias.Short)
            {
                PrintWithTime($"{Bias} entry : {entryPrice}");

                if (Close[0] < entryPrice)
                {
                    LimitOrder = EnterShortLimit(0, true, CalculatePositionSize(StopLossPrice - entryPrice), entryPrice, "short entry");
                    PrintWithTime($"{Bias} limit entry : {entryPrice} {Close[0]}");
                }
                else
                {
                    LimitOrder = EnterShortStopMarket(0, true, CalculatePositionSize(StopLossPrice - entryPrice), entryPrice, "short entry");
                    PrintWithTime($"{Bias} stop market entry : {entryPrice} {Close[0]}");
                }

                SetStopLoss("short entry", CalculationMode.Price, StopLossPrice, true);
                PrintDebugWithTime($"Stop level (LVLevel) {StopLossPrice}");

                SetProfitTarget("short entry", CalculationMode.Price, TakeProfitPrice, true);
            }
        }

        private void BreakEvenOrCloseTrade()
        {
            if (Position.MarketPosition == MarketPosition.Flat)
                return;

            if (Time[0].Hour == 14 && Time[0].Minute == 29)
            {
                PrintDebugWithTime($"Setting trade to BE before market open, current PnL {Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0])}");

                if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("short entry");

                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("long entry");
            }
            else
            {

                if (Bias == TradeBias.Long && High[0] >= TakeProfitPriceThreshold)
                {
                    SetStopLoss("long entry", CalculationMode.Price, Position.AveragePrice, true);
                    PrintDebugWithTime($"Setting trade to BE as MB reached, {Position.AveragePrice}");
                }

                if (Bias == TradeBias.Short && Low[0] <= TakeProfitPriceThreshold)
                {
                    SetStopLoss("short entry", CalculationMode.Price, Position.AveragePrice, true);
                }

            }
        }

        private bool DetectAndCheckInvalidRange()
        {
            if (InvalidRange == null)
                return false;

            // Are we still in an invalid range?
            var priceToCheck = Bias == TradeBias.Short ? Low[0] : High[0];

            if (InvalidRange.IsInPlay(Close[0]))
                return true;

            // Did we leave the invalid range?
            if (!InvalidRange.IsInPlay(Close[0]))
            {
                PrintDebugWithTime($"[Rng Inv] Price {Close[0]} cleared the invalid range, resetting.");
                InvalidRange = null;

                sharedBorderTrade = null;

                return false;
            }

            return false;
        }

        private void DetectFractal()
        {
            // --- Detect 3-bar fractal high ---
            if (High[1] > High[0] && High[1] > High[2])
            {
                lastFractalHigh = High[1];
                Draw.Dot(this, "FractalHigh" + CurrentBar, false, 1, High[1], Brushes.Green);
                //PrintDebugWithTime($"New Fractal High {lastFractalHigh}");
            }

            // --- Detect 3-bar fractal low ---
            if (Low[1] < Low[0] && Low[1] < Low[2])
            {
                lastFractalLow = Low[1];
                Draw.Dot(this, "FractalLow" + CurrentBar, false, 1, Low[1], Brushes.Red);
                //	PrintDebugWithTime($"New Fractal Low {lastFractalLow}");
            }
        }

        private void DetectFractalBOS()
        {
            // --- Bullish BOS ---
            if (lastFractalHigh != 0 && Close[0] > lastFractalHigh)
            {
                Draw.ArrowUp(this, "BOSUp" + CurrentBar, false, 0, Low[0] - TickSize * 2, Brushes.Blue);

                bearishBOSDetected = false;
                bullishBOSDetected = true;

                PrintDebugWithTime($"Bullish BOS detected");

                lastFractalHigh = 0; // reset after break
            }

            // --- Bearish BOS ---
            if (lastFractalLow != 0 && Close[0] < lastFractalLow)
            {
                Draw.ArrowDown(this, "BOSDown" + CurrentBar, false, 0, High[0] + TickSize * 2, Brushes.Red);

                bearishBOSDetected = true;
                bullishBOSDetected = false;

                PrintDebugWithTime($"Bearish BOS detected");

                lastFractalLow = 0; // reset after break
            }
        }

        private double AdjustRisk()
        {
            var riskAdjusted = Risk;
            if (AdjustRiskBasedOnLosses && ConsecutiveLosses >= 2)
            {
                riskAdjusted = Risk / 2;
                PrintDebugWithTime($"Adjust risk to {Risk}");
            }

            if (Time[0].Hour == EdgeHour)
            {
                riskAdjusted = riskAdjusted * EdgeHourMultiplier;
                PrintDebugWithTime($"Edge hour, adjust risk to {Risk}");
            }

            return riskAdjusted;
        }

        private int CalculatePositionSize(double stopLossDistance)
        {
            var adjustedRisk = AdjustRisk();

            var stopLossTicks = Math.Round(stopLossDistance, 1, MidpointRounding.ToEven) / Instrument.MasterInstrument.TickSize;
            var tickValue = Instrument.MasterInstrument.PointValue * Instrument.MasterInstrument.TickSize;
            return (int)Math.Round(adjustedRisk / (stopLossTicks * tickValue), MidpointRounding.AwayFromZero);
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order.Name == "Profit target")
            {
                // the profit target has had a fill or part fill
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    PrintWithTime("Profit target triggered, resetting...");

                    //ProcessSharedBorderRange(false);

                    LimitOrder = null;

                    ClearDealingRangeStates();

                    Bias = TradeBias.Neutral;

                    StopLossPrice = 0;
                    TakeProfitPrice = 0;
                    TakeProfitPriceThreshold = 0;

                    ConsecutiveLosses = 0;

                    rangeBreakDetected = false;
                }
            }

            if (execution.Order.Name == "Stop loss")
            {
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    PrintWithTime("Stop loss triggered, resetting...");

                    ProcessSharedBorderRange(true);

                    ConsecutiveLosses++;

                    LimitOrder = null;

                    ClearDealingRangeStates();

                    Bias = TradeBias.Neutral;

                    StopLossPrice = 0;
                    TakeProfitPrice = 0;
                    TakeProfitPriceThreshold = 0;

                    rangeBreakDetected = false;
                }
            }

            if (execution.Order.Name == "Trail stop")
            {
                // the trailing stop loss has had a fill or part fill
            }
        }

        private void ProcessSharedBorderRange(bool isLoss)
        {
            var current = Ranges.FirstOrDefault(r => r.IsInRange(StopLossPrice));
            var previous = Ranges.FirstOrDefault(r => r.IsInRange(TakeProfitPrice));
            if (isLoss == false)
            {
                current = Ranges.FirstOrDefault(r => r.IsInRange(TakeProfitPrice));
                previous = Ranges.FirstOrDefault(r => r.IsInRange(StopLossPrice));
            }

            var currentSharedBorderRange = GetSharedBorderRange(current, isLoss);
            if (sharedBorderTrade != null && sharedBorderTrade.Border == currentSharedBorderRange)
            {
                sharedBorderTrade.Trades++;

                PrintDebugWithTime($"[Rng Inv] Shared border range {sharedBorderTrade.Border} trades incremented to {sharedBorderTrade.Trades}");

                if (sharedBorderTrade.Trades >= 2)
                    CreateInvalidRange(current, previous, isLoss);
            }
            else
            {
                sharedBorderTrade = new SharedBorderTrade() { Trades = 1, Border = currentSharedBorderRange };
                PrintDebugWithTime($"[Rng Inv] Shared border range set to {sharedBorderTrade.Border}");
            }
        }

        private double GetSharedBorderRange(DealingRange applicable, bool isLoss)
        {
            if (RangeBias == RangeBias.Bearish)
            {
                if (Bias == TradeBias.Short) return isLoss ? applicable.Low : applicable.High;
                if (Bias == TradeBias.Long) return isLoss ? applicable.High : applicable.Low;
            }

            if (RangeBias == RangeBias.Bullish)
            {
                if (Bias == TradeBias.Long) return isLoss ? applicable.High : applicable.Low;
                if (Bias == TradeBias.Short) return isLoss ? applicable.Low : applicable.High;
            }

            return 0;
        }

        private void CreateInvalidRange(DealingRange current, DealingRange previous, bool isLoss)
        {
            if (InvalidRange != null)
                return;

            if (RangeBias == RangeBias.Bearish && Bias == TradeBias.Short)
                InvalidRange = new InvalidRange(isLoss ? current.BR4 : previous.BR4, isLoss ? previous.BR1 : current.BR1);

            if (RangeBias == RangeBias.Bearish && Bias == TradeBias.Long)
                InvalidRange = new InvalidRange(isLoss ? previous.BR4 : current.BR4, isLoss ? current.BR1 : previous.BR1);

            if (RangeBias == RangeBias.Bullish && Bias == TradeBias.Long)
                InvalidRange = new InvalidRange(isLoss ? previous.BR4 : current.BR4, isLoss ? current.BR1 : previous.BR1);

            if (RangeBias == RangeBias.Bullish && Bias == TradeBias.Short)
                InvalidRange = new InvalidRange(isLoss ? current.BR4 : previous.BR4, isLoss ? previous.BR1 : current.BR1);

            PrintDebugWithTime($"[Rng Inv] Invalid range created, {InvalidRange.ToString()}");
        }

        private void UpdateDealingRangeState(DealingRange applicable)
        {
            var applicableRange = Ranges.FirstOrDefault(r => r == applicable);
            if (Bias == TradeBias.Long)
            {
                PrintDebugWithTime($"{Bias}: Updating range state range: {applicableRange}");
                applicableRange.LongTradeExecuted = true;
            }

            if (Bias == TradeBias.Short)
            {
                PrintDebugWithTime($"{Bias}: Updating range state range: {applicableRange}");
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

        private DealingRange CreateDealingRange()
        {
            var newDealingRange = new DealingRange(PO3, Close[0]);
            Ranges.Add(newDealingRange);
            PrintDebugWithTime($"Creating New Range: {newDealingRange}");
            return newDealingRange;
        }

        private void ResetStateAndCancel()
        {
            Bias = TradeBias.Neutral;
            RangeBias = RangeBias.Neutral;

            StopLossPrice = 0;
            TakeProfitPrice = 0;
            TakeProfitPriceThreshold = 0;

            rangeBreakDetected = false;

            if (LimitOrder != null)
            {
                CancelOrder(LimitOrder);
                LimitOrder = null;
                PrintDebugWithTime("Cancel limit order...");
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