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
    public class AlgoPO3v4 : Strategy
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

        private Order LimitOrder { get; set; }
        private List<DealingRange> Ranges { get; set; }

        private DealingRange CurrentRange { get; set; }
        private DealingRange PreviousRange { get; set; }
        private TradeBias Bias { get; set; }
        private RangeBias RangeBias { get; set; }
        private InvalidRange InvalidRange { get; set; }

        private double TakeProfitPrice { get; set; }

        private List<NewsEvent> NewsEvents { get; set; }

        private int ConsecutiveLosses { get; set; }
        private int ConsecutiveDailyLosses { get; set; }

        private DateTime? PauseUntil { get; set; }

        private double RiskMultiplier { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"Enter the description for your new custom Strategy here.";
                Name = "FassaPO3 v4";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                IsExitOnSessionCloseStrategy = true;
                PO3 = 81;
                SessionStart = 80000;
                SessionEnd = 140000;
                Risk = 300;
                DailyLossStreak = 3;
            }
            else if (State == State.Configure)
            {
                AddDataSeries("MNQ 03-26", BarsPeriodType.Tick, 1);
                Ranges = new List<DealingRange>();

                NewsEvents = new List<NewsEvent>();

                //NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 7, 13, 30, 0))); //Low Level Red
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 8, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 9, 13, 30, 0)));//NFP
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 13, 13, 30, 0)));//CPI
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 14, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 15, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 22, 13, 30, 0)));
                //NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 28, 8, 0, 0))); //FOMC				
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 29, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 1, 30, 13, 30, 0)));

                //NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 4, 13, 15, 0)));  //Low Level Red
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 5, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 10, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 11, 13, 30, 0))); //NFP
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 12, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 13, 13, 30, 0))); //CPI
                                                                                     //NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 18, 08, 30, 0))); //FOMC
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 19, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 20, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 26, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 2, 27, 13, 30, 0)));

                //NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 4, 13, 15, 0))); // Low Level Red
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 5, 13, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 6, 13, 30, 0))); //NFP
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 10, 12, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 11, 12, 30, 0))); //CPI
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 12, 12, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 13, 12, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 18, 12, 30, 0))); //FOMC
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 19, 12, 30, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 23, 11, 06, 0)));
                NewsEvents.Add(new NewsEvent(new DateTime(2026, 3, 26, 12, 30, 0)));

                ConsecutiveLosses = 0;
                ConsecutiveDailyLosses = 0;

                RiskMultiplier = 1.25;
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            if (PauseUntil != null && Time[0] <= PauseUntil)
            {
                //PrintDebugWithTime($"Pause trading until {PauseUntil}");
                return;
            }

            if (PauseUntil != null && Time[0] > PauseUntil)
            {
                PauseUntil = null;
                ConsecutiveLosses = 0;
                return;
            }

            // Filter time of day and cancel if we are flat.
            if (Time[0].Hour == 10 && Position.MarketPosition == MarketPosition.Flat)
            {
                ResetStateAndCancel();
                return;
            }

            // Adjust time for NY/UK BST overlap
            if ((Time[0].Date >= new DateTime(2025, 3, 9) && Time[0].Date <= new DateTime(2025, 3, 30)) ||
                (Time[0].Date >= new DateTime(2025, 10, 26) && Time[0].Date <= new DateTime(2025, 11, 2)) ||
                (Time[0].Date >= new DateTime(2026, 3, 9) && Time[0].Date <= new DateTime(2026, 3, 29)))
                SessionEnd = 120000;

            // The strategy only runs from a given time in the morning (8am)
            if (ToTime(Time[0]) < SessionStart)
                return;

            // If the session has ended do some cleanup if neccessary.
            if ((ToTime(Time[0]) > SessionEnd || FilterByEvents()) && Position.MarketPosition == MarketPosition.Flat)
            {
                if (Ranges.Count > 0)
                {
                    PrintDebugWithTime("End of session, clearing ranges...");
                    Ranges.Clear();
                }

                ResetStateAndCancel();

                InvalidRange = null;
                ConsecutiveDailyLosses = 0;

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
                if (PreviousRange != CurrentRange)
                {
                    PreviousRange = CurrentRange;
                    CurrentRange = found;

                    // If a previous range dealing range has been set then a break into
                    // a new range has occurred. We then determine the trade bias.

                    // We are entered the range bullish
                    if (Position.MarketPosition == MarketPosition.Flat && LimitOrder == null && CurrentRange.Low == PreviousRange.High)
                    {
                        RangeBias = RangeBias.Bullish;

                        // We are short from the FV if the close of the breaker candle did not enter the FV partition.
                        if (High[0] < CurrentRange.FV1)
                        {
                            Bias = TradeBias.Short;
                        }

                        // We are long from the range extension if the breaker candle touched the FV partition
                        if (High[0] >= CurrentRange.FV1)
                        {
                            Bias = TradeBias.Long;
                        }

                        PrintDebugWithTime($"Range bias {RangeBias}: Trade bias set to {Bias}");
                    }

                    //We entered the range bearish
                    if (Position.MarketPosition == MarketPosition.Flat && LimitOrder == null && CurrentRange.High == PreviousRange.Low)
                    {
                        RangeBias = RangeBias.Bearish;

                        // We are long if the close of the breaker candle did not enter the FV partition.
                        if (Low[0] > CurrentRange.FV4)
                        {
                            Bias = TradeBias.Long;
                        }

                        // We are short from the range extension if the breaker candle entered the FV partition
                        if (Low[0] <= CurrentRange.FV4)
                        {
                            Bias = TradeBias.Short;
                        }

                        PrintDebugWithTime($"Range bias {RangeBias}: Trade bias set to {Bias}");
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
                    PrintDebugWithTime($"{Bias}: price already went to TP {TakeProfitPrice}, resetting...");
                    CancelOrder(LimitOrder);
                    LimitOrder = null;
                    RangeBias = RangeBias.Neutral;
                    Bias = TradeBias.Neutral;
                    TakeProfitPrice = 0;
                }

                return;
            }

            // We are currently in a trade.
            if (Position.MarketPosition != MarketPosition.Flat || LimitOrder != null)
            {
                return;
            }

            // Are we still in an invalid range?
            if (InvalidRange != null && InvalidRange.IsInPlay(Close[0]))
            {
                return;
            }

            // Did we leave the invalid range?
            if (InvalidRange != null && !InvalidRange.IsInPlay(Close[0]))
            {
                PrintDebugWithTime($"Price cleared the invalid range, resetting.");
                InvalidRange = null;
                ConsecutiveDailyLosses = 0;
                return;
            }

            var entryPrice = 0.0; //TODO are we rounding entries to tick level?
            var tpPrice = 0.0;
            var slPrice = 0.0;

            if (RangeBias == RangeBias.Bearish)
            {
                if (Bias == TradeBias.Long)
                {
                    entryPrice = CurrentRange.FV4;
                    slPrice = CurrentRange.BR4 + Math.Round((CurrentRange.FV3 - CurrentRange.BR4) * 0.33, 2);
                    tpPrice = PreviousRange.MBCE1;
                }

                if (Bias == TradeBias.Short)
                {
                    entryPrice = CurrentRange.ExtensionHigh;
                    slPrice = PreviousRange.BR1 - Math.Round((PreviousRange.BR1 - PreviousRange.FV2) * 0.33, 2);
                    tpPrice = CurrentRange.MBCE2;
                }
            }

            if (RangeBias == RangeBias.Bullish)
            {
                if (Bias == TradeBias.Long)
                {
                    entryPrice = CurrentRange.ExtensionLow;
                    slPrice = PreviousRange.BR4 + Math.Round((PreviousRange.FV3 - PreviousRange.BR4) * 0.33, 2);
                    tpPrice = CurrentRange.MBCE1;
                }

                if (Bias == TradeBias.Short)
                {
                    entryPrice = CurrentRange.FV1;
                    slPrice = CurrentRange.BR1 - Math.Round((CurrentRange.BR1 - CurrentRange.FV2) * 0.33, 2);
                    tpPrice = PreviousRange.MBCE2;
                }
            }

            //PrintDebugWithTime($"{CurrentRange.LongTradeExecuted} {TakeProfitPrice}");
            if (Bias == TradeBias.Long && !CurrentRange.LongTradeExecuted)
            {
                PrintWithTime($"{Bias} entry : {entryPrice}");

                LimitOrder = EnterLongLimit(0, true, CalculatePositionSize(entryPrice - slPrice), entryPrice, "long entry");

                SetStopLoss("long entry", CalculationMode.Price, slPrice, true);
                PrintDebugWithTime($"Stop level (LVLevel) {slPrice}");

                SetProfitTarget("long entry", CalculationMode.Price, tpPrice, true);
                TakeProfitPrice = tpPrice;
            }

            //PrintDebugWithTime($"{CurrentRange.ShortTradeExecuted} {Close[0]} {CurrentRange.Low} {CurrentRange.FV1} ");	
            if (Bias == TradeBias.Short && !CurrentRange.ShortTradeExecuted)
            {
                PrintWithTime($"{Bias} entry : {entryPrice}");

                LimitOrder = EnterShortLimit(0, true, CalculatePositionSize(slPrice - entryPrice), entryPrice, "short entry");

                SetStopLoss("short entry", CalculationMode.Price, slPrice, true);
                PrintDebugWithTime($"Stop level (LVLevel) {slPrice}");

                SetProfitTarget("short entry", CalculationMode.Price, tpPrice, true);
                TakeProfitPrice = tpPrice;
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

            if (Time[0].Hour == 11)
                riskAdjusted = riskAdjusted * RiskMultiplier;

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
                    LimitOrder = null;

                    ClearDealingRangeStates();

                    Bias = TradeBias.Neutral;
                    TakeProfitPrice = 0;

                    ConsecutiveLosses = 0;
                    ConsecutiveDailyLosses = 0;
                    InvalidRange = null;
                }
            }

            if (execution.Order.Name == "Stop loss")
            {
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    PrintWithTime("Stop loss triggered, resetting...");

                    ConsecutiveLosses++;
                    ConsecutiveDailyLosses++;

                    if (ConsecutiveDailyLosses == DailyLossStreak)
                    {
                        if (RangeBias == RangeBias.Bearish && Bias == TradeBias.Short)
                        {
                            InvalidRange = new InvalidRange(CurrentRange.High, PreviousRange.Low);
                        }
                        if (RangeBias == RangeBias.Bearish && Bias == TradeBias.Long)
                        {
                            InvalidRange = new InvalidRange(PreviousRange.High, CurrentRange.Low);
                        }
                        if (RangeBias == RangeBias.Bullish && Bias == TradeBias.Long)
                        {
                            InvalidRange = new InvalidRange(PreviousRange.High, CurrentRange.Low);
                        }
                        if (RangeBias == RangeBias.Bullish && Bias == TradeBias.Short)
                        {
                            InvalidRange = new InvalidRange(CurrentRange.High, PreviousRange.Low);
                        }

                        PrintDebugWithTime($"{RangeBias} {Bias} Invalid range created, {InvalidRange}");
                    }


                    //					IGNORE for now					
                    //					if(ConsecutiveLosses >= LossStreak)
                    //					{
                    //						PauseUntil = Time[0].AddDays(1);//.Date.AddHours(14);
                    //						PrintDebugWithTime($"Trading pause set until {PauseUntil}");
                    //					}

                    LimitOrder = null;

                    ClearDealingRangeStates();

                    Bias = TradeBias.Neutral;
                    TakeProfitPrice = 0;
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

        private DealingRange CreateDealingRange()
        {
            var newDealingRange = new DealingRange(PO3, Close[0]);
            Ranges.Add(newDealingRange);
            PrintDebugWithTime($"Creating New Range: {newDealingRange.ToString()}");
            return newDealingRange;
        }

        private void ResetStateAndCancel()
        {
            Bias = TradeBias.Neutral;
            RangeBias = RangeBias.Neutral;
            TakeProfitPrice = 0;

            CurrentRange = null;
            PreviousRange = null;

            ClearDealingRangeStates();

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