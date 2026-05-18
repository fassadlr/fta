#region Using declarations
using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Custom.Strategies.FTA.AlgoPO3;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;
#endregion

//This namespace holds Strategies in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public class AlgoPO3v7 : AlgoPO3Base
    {
        private ATR atr;

        private List<NewsEvent> events;

        private Order PendingOrder { get; set; }

        private List<DealingRange> Ranges { get; set; }
        private DealingRange CurrentRange { get; set; }
        private DealingRange PreviousRange { get; set; }

        private MSSBias mssBias;
        private TradeBias tradeBias;
        private bool isTradeInThisRange;

        private int ConsecutiveLosses { get; set; }

        private double lastBOSLocation = 0;

        private NewsFilter newsFilter;

        private TradeDetails lastTrade;

        private DailyAtr dailyAtr;

        private double atrValue = 0;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "FassaPO3 v7";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                IsExitOnSessionCloseStrategy = true;
                PO3 = 81;
                SessionStart = 0;
                SessionEnd = 13;
                Risk = 160;
                IsOverlay = true;

                BlockEndHours = "3,9,12";
                EdgeHours = "0,3,9,11";
                EdgeHourMultiplier = 1.5;
                SkipHours = "4,5,10";
                SkipMonths = "8";

                IncludeCPIWeek = true;
                IncludeFOMCWeek = false;
                IncludeNFPWeek = true;
                IncludeNoEventsWeek = true;
            }
            else if (State == State.Configure)
            {
                AddDataSeries("MNQ 06-26", BarsPeriodType.Minute, 5);

                Ranges = [];

                events = NewsEvents.Events();

                ConsecutiveLosses = 0;

                tradeBias = TradeBias.Neutral;
                mssBias = MSSBias.Neutral;

                newsFilter = new NewsFilter(IncludeCPIWeek, IncludeFOMCWeek, IncludeNFPWeek, IncludeNoEventsWeek);
            }
            else if (State == State.DataLoaded)
            {
                atr = ATR(BarsArray[1], 14);

                BlockEndHoursParsed = [.. BlockEndHours.Split(',').Select((i) => int.Parse(i) + SessionTimeOffset)];
                SkipHoursParsed = [.. SkipHours.Split(',').Select((i) => int.Parse(i) + SessionTimeOffset)];

                SessionStart = SessionStart + SessionTimeOffset;
                SessionEnd = SessionEnd + SessionTimeOffset;
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 0 && Bars.IsFirstBarOfSession)
                PerformCleanupBeforeStart();

            dailyAtr ??= new DailyAtr();

            // Always process fractal and BOS detection on 5min.
            if (CurrentBar >= 5 && BarsInProgress == 1)
            {
                DetectFractalFiveBar();
                DetectMSS();
                DetectBOS();
            }

            if (SkipMonths.Split(',').Contains(Time[0].Month.ToString()))
                return;

            if (!events.IsInWeek(newsFilter, Time[0]))
            {
                //PrintDebug($"Skipping as NFP {newsFilter.IncludeNFP} CPI {newsFilter.IncludeCPI} FOMC {newsFilter.IncludeFOMC} NoEvents {newsFilter.IncludeNoEvents}...");
                return;
            }

            // Filter by news events (TODO, not sure if this still needed)
            if (FilterByEvents() && Position.MarketPosition == MarketPosition.Flat)
                return;

            // Adjust time for NY/UK BST overlap
            var adjustSessionEnd = AdjustSessionEnd();
            if (Time[0].Hour >= adjustSessionEnd && Position.MarketPosition == MarketPosition.Flat)
                return;

            // The strategy only runs from a given time.
            if (Time[0].Hour < SessionStart)
                return;

            if (TradingSession == null)
            {
                TradingSession = new TradeSessionState(this);
                PrintDebug($"Creating new trading session.");
            }

            if (BarsInProgress == 1)
            {
                atrValue = atr[0];

                //dailyAtr.Calculate(High[0], Low[0], Close[0]);

                // Create the initial dealing range, this will only happen once per session.
                if (CurrentRange == null)
                {
                    CurrentRange = CreateDealingRange();
                    return;
                }

                // Determine if we are still in the same dealing range.
                var found = Ranges.FirstOrDefault(r => r.IsInRange(Close[0])) ?? CreateDealingRange();

                // Always process range breaks
                if (found != CurrentRange)
                {
                    if (PreviousRange != CurrentRange)
                    {
                        PreviousRange = CurrentRange;
                        CurrentRange = found;

                        TradingSession.DetermineRangeBias(PreviousRange, CurrentRange);
                    }
                }

                CurrentRange.UpdateCoverage(Close[0]);
                PreviousRange?.UpdateCoverage(Close[0]);

                TradingSession.Update(Close[0], atrValue);

                return;
            }

            // If we are in a position then break even or close and stop processing.
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                BreakEvenOrCloseTrade();
                return;
            }

            /*
             ### From here we are flat or waiting on a pending order to execute ###
            */

            if (DetectAndCheckInvalidRange())
            {
                return;
            }

            // Filter time of day and cancel if we are flat.
            if (SkipHoursParsed.Contains(Time[0].Hour))
            {
                CancelLimitOrder();
                return;
            }

            // Cancel limit orders close to end of sessions blocks.
            if (BlockEndHoursParsed.Contains(Time[0].Hour) && Time[0].Minute >= 45)
            {
                CancelLimitOrder();
                return;
            }

            // If we set a limit order and price gets to TP we need to clear the limit order
            if (PendingOrder != null)
            {
                if ((lastTrade.Bias == TradeBias.Long && High[0] >= lastTrade.TradeMissedThreshold) ||
                   (lastTrade.Bias == TradeBias.Short && Low[0] <= lastTrade.TradeMissedThreshold))
                {
                    PrintDebug($"{lastTrade.Bias} price went to TP threshold {lastTrade.TradeMissedThreshold}, resetting");
                    TradingSession.ClearRangeBias();

                    CancelLimitOrder();
                }

                return;
            }

            //if (State != State.Realtime)
            //    return;

            (tradeBias, isTradeInThisRange) = TradingSession.CalculateTradeBiasUniDirectional(Time[0], Open[0], Close[0]);
            if (atrValue > 50)
            {
                PrintDebug($"[EXCESSIVE_ATR] skipping trade, ATR {atrValue}");
                return;
            }

            if (atrValue == 0)
            {
                PrintDebug($"[INITIAL_ATR] skipping trade, ATR {atrValue}");
                return;
            }

            if (tradeBias == TradeBias.Short)
            {
                double entry = 0;
                double stopLoss = 0;
                double takeProfit = 0;
                double tradeMissedThreshold = 0;
                double breakEvenThreshold = 0;

                if (isTradeInThisRange)
                {
                    entry = CurrentRange.ExtensionHighCE - TickSize;
                    stopLoss = PreviousRange.BR1 - Math.Round((PreviousRange.BR1 - PreviousRange.FV2) * 0.33, 2);
                    takeProfit = CurrentRange.MBCE2;
                    tradeMissedThreshold = Math.Round(CurrentRange.Low + (PO3 * 0.03), 2);
                    breakEvenThreshold = CurrentRange.BR3;
                }
                else
                {
                    entry = PreviousRange.ExtensionHighCE - TickSize;
                    stopLoss = CurrentRange.BR1 - Math.Round((CurrentRange.BR1 - CurrentRange.FV2) * 0.33, 2);
                    takeProfit = PreviousRange.MBCE2;
                    tradeMissedThreshold = Math.Round(PreviousRange.Low + (PO3 * 0.03), 2);
                    breakEvenThreshold = PreviousRange.BR3;
                }

                entry = Math.Round(entry / 0.25) * 0.25;

                lastTrade = new TradeDetails()
                {
                    Bias = tradeBias,
                    Atr = atrValue,
                    Entry = entry,
                    StopLoss = stopLoss,
                    TakeProfit = takeProfit,
                    TradeMissedThreshold = tradeMissedThreshold,
                    BreakEvenThreshold = breakEvenThreshold,
                };

                if (Close[0] < lastTrade.Entry)
                {
                    PrintDebug($"[PLACE_LIMIT] {tradeBias} entry at {lastTrade.Entry} TMT {lastTrade.TradeMissedThreshold} BET {lastTrade.BreakEvenThreshold}");
                    PendingOrder = EnterShortLimit(0, true, CalculatePositionSize(lastTrade.StopLoss - lastTrade.Entry), lastTrade.Entry, "short entry");
                }
                else
                {
                    PrintDebug($"[PLACE_STOP_MARKET] {tradeBias} entry at {lastTrade.Entry} TMT {lastTrade.TradeMissedThreshold} BET {lastTrade.BreakEvenThreshold}");
                    PendingOrder = EnterShortStopMarket(0, true, CalculatePositionSize(lastTrade.StopLoss - lastTrade.Entry), lastTrade.Entry, "short entry");
                }

                if (atrValue > 16)
                {
                    lastTrade.StopLoss = lastTrade.Entry + Math.Round(atrValue);
                    PrintDebug($"[ATR_ADJ_STOP] Atr {atrValue} StopLoss {lastTrade.StopLoss}");

                    //if (atrValue >= 30)
                    //{
                    //    StopLoss = entryPrice - 30;
                    //    PrintDebug($"[ATR_ADJ_STOP] {StopLoss}");
                    //}
                }

                PrintDebug($"Stop level {lastTrade.StopLoss}");
                SetStopLoss("short entry", CalculationMode.Price, lastTrade.StopLoss, true);
                SetProfitTarget("short entry", CalculationMode.Price, lastTrade.TakeProfit, true);
            }

            if (tradeBias == TradeBias.Long)
            {
                double entry = 0;
                double stopLoss = 0;
                double takeProfit = 0;
                double tradeMissedThreshold = 0;
                double breakEvenThreshold = 0;

                if (isTradeInThisRange)
                {
                    entry = CurrentRange.ExtensionLowCE + TickSize;
                    stopLoss = PreviousRange.BR4 + Math.Round((PreviousRange.FV3 - PreviousRange.BR4) * 0.33, 2);
                    takeProfit = CurrentRange.MBCE1;
                    tradeMissedThreshold = Math.Round(CurrentRange.High - (PO3 * 0.03), 2);
                    breakEvenThreshold = CurrentRange.BR2;
                }
                else
                {
                    entry = PreviousRange.ExtensionLowCE + TickSize;
                    stopLoss = CurrentRange.BR4 + Math.Round((CurrentRange.FV3 - CurrentRange.BR4) * 0.33, 2);
                    takeProfit = PreviousRange.MBCE1;
                    tradeMissedThreshold = Math.Round(PreviousRange.High - (PO3 * 0.03), 2);
                    breakEvenThreshold = PreviousRange.BR2;
                }

                entry = Math.Round(entry / 0.25) * 0.25;

                lastTrade = new TradeDetails()
                {
                    Bias = tradeBias,
                    Atr = atrValue,
                    Entry = entry,
                    StopLoss = stopLoss,
                    TakeProfit = takeProfit,
                    TradeMissedThreshold = tradeMissedThreshold,
                    BreakEvenThreshold = breakEvenThreshold,
                };

                lastTrade.Entry = Math.Round(lastTrade.Entry / 0.25) * 0.25;

                if (Close[0] > lastTrade.Entry)
                {
                    PrintDebug($"[PLACE_LIMIT] {lastTrade.Bias} entry at {lastTrade.Entry} TMT {lastTrade.TradeMissedThreshold} BET");
                    PendingOrder = EnterLongLimit(0, true, CalculatePositionSize(lastTrade.Entry - lastTrade.StopLoss), lastTrade.Entry, "long entry");
                }
                else
                {
                    PrintDebug($"[PLACE_STOP_MARKET] {lastTrade.Bias} entry at {lastTrade.Entry} TMT {lastTrade.TradeMissedThreshold} BET");
                    PendingOrder = EnterLongStopMarket(0, true, CalculatePositionSize(lastTrade.Entry - lastTrade.StopLoss), lastTrade.Entry, "long entry");
                }

                if (atrValue > 16)
                {
                    lastTrade.StopLoss = lastTrade.Entry - Math.Round(atrValue);
                    PrintDebug($"[ATR_ADJ_STOP] Atr {atrValue} StopLoss {lastTrade.StopLoss}");

                    //if (atrValue >= 30)
                    //{
                    //    StopLoss = entryPrice - 30;
                    //    PrintDebug($"[ATR_ADJ_STOP] {StopLoss}");
                    //}
                }

                PrintDebug($"Stop level {lastTrade.StopLoss}");
                SetStopLoss("long entry", CalculationMode.Price, lastTrade.StopLoss, true);
                SetProfitTarget("long entry", CalculationMode.Price, lastTrade.TakeProfit, true);
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order.Name == "Profit target")
            {
                // the profit target has had a fill or part fill
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    var lastTradeAudit = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
                    PrintDebug("TRADE_WIN");
                    PrintTrade($"{lastTradeAudit.Entry.Time},{PendingOrder.IsStopMarket},Win,{lastTradeAudit.ProfitCurrency},{lastTradeAudit.Quantity},{Math.Round(this.lastTrade.Atr)},{events.IsCPIWeek(Time[0])},{events.IsNFPWeek(Time[0])},{events.IsFOMCWeek(Time[0])},{events.IsNoEventWeek(Time[0])}");

                    ProcessSharedBorderRange(false);

                    ConsecutiveLosses = 0;

                    PendingOrder = null;

                    TradingSession.ClearRangeBias();
                }
            }

            if (execution.Order.Name == "Stop loss")
            {
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    var lastTradeAudit = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
                    PrintDebug("TRADE_LOSS");
                    PrintTrade($"{lastTradeAudit.Entry.Time},{PendingOrder.IsStopMarket},Loss,{lastTradeAudit.ProfitCurrency},{lastTradeAudit.Quantity},{Math.Round(this.lastTrade.Atr)},{events.IsCPIWeek(Time[0])},{events.IsNFPWeek(Time[0])},{events.IsFOMCWeek(Time[0])},{events.IsNoEventWeek(Time[0])}");

                    ProcessSharedBorderRange(true);

                    ConsecutiveLosses++;

                    lastTrade.IsLoss = true;

                    PendingOrder = null;

                    TradingSession.ClearRangeBias();
                }
            }
        }

        private void DetectMSS()
        {
            //PrintDebug($"{tradeBias} {lastFractalLow} {lastFractalHigh} {Close[0]}");
            if (BearishBOSDetected && Close[0] > LastFractalHigh && LastFractalHigh != 0)
            {
                mssBias = MSSBias.Bullish;
                PrintDebug($"Bullish MSS detected");
            }

            if (BullishBOSDetected && Close[0] < LastFractalLow && LastFractalLow != 0)
            {
                mssBias = MSSBias.Bearish;
                PrintDebug($"Bearish MSS detected");
            }
        }

        private void DetectBOS()
        {
            //---Bullish BOS ---
            if (LastFractalHigh != 0 && Close[0] > LastFractalHigh)
            {
                if (mssBias == MSSBias.Neutral || mssBias == MSSBias.Bullish)
                {
                    if (lastBOSLocation == 0 || lastBOSLocation != LastFractalHigh)
                    {
                        BearishBOSDetected = false;
                        BullishBOSDetected = true;

                        if (mssBias == MSSBias.Neutral) mssBias = MSSBias.Bullish;

                        lastBOSLocation = LastFractalHigh;

                        PrintDebug($"Bullish BOS detected, MSS bias set to {mssBias} BOS price {lastBOSLocation}");
                    }
                }
            }

            // --- Bearish BOS ---
            if (LastFractalLow != 0 && Close[0] < LastFractalLow)
            {
                if (mssBias == MSSBias.Neutral || mssBias == MSSBias.Bearish)
                {
                    if (lastBOSLocation == 0 || lastBOSLocation != LastFractalLow)
                    {
                        BearishBOSDetected = true;
                        BullishBOSDetected = false;

                        if (mssBias == MSSBias.Neutral) mssBias = MSSBias.Bearish;

                        lastBOSLocation = LastFractalLow;

                        PrintDebug($"Bearish BOS detected, MSS bias set to {mssBias} BOS price {lastBOSLocation}");
                    }
                }
            }
        }

        private void PerformCleanupBeforeStart()
        {
            PrintDebug("Performing cleanup before session start...");

            Ranges.Clear();

            CancelLimitOrder();

            CurrentRange = null;
            PreviousRange = null;

            InvalidRange = null;
            SharedBorderTrade = null;

            LastFractalHigh = 0;
            LastFractalLow = 0;

            BullishBOSDetected = false;
            BearishBOSDetected = false;

            mssBias = MSSBias.Neutral;
            tradeBias = TradeBias.Neutral;

            lastBOSLocation = 0;

            TradingSession = null;
        }

        private void BreakEvenOrCloseTrade()
        {
            var marketOpen = IsDSTOverlap() ? 13 : 14;

            if (Time[0].Hour == marketOpen && Time[0].Minute == 29)
            {
                PrintDebug($"Setting trade to BE before market open, current PnL {Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0])}");

                if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("short entry");

                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("long entry");
            }
            else
            {
                if (lastTrade.StopLoss == Position.AveragePrice)
                    return;

                var breakEvenDirection = "";

                if (lastTrade.Bias != tradeBias)
                {
                    if (lastTrade.Bias == TradeBias.Long && High[0] >= lastTrade.BreakEvenThreshold)
                    {
                        PrintDebug($"[BREAK_EVEN] trade bias changed from {lastTrade.Bias} to {tradeBias}, {Position.AveragePrice}");
                        breakEvenDirection = "long entry";
                    }

                    if (lastTrade.Bias == TradeBias.Short && Low[0] <= lastTrade.BreakEvenThreshold)
                    {
                        breakEvenDirection = "short entry";
                        PrintDebug($"[BREAK_EVEN] trade bias changed from {lastTrade.Bias} to {tradeBias}, {Position.AveragePrice}");
                    }
                }
                else
                {
                    if (tradeBias == TradeBias.Long && High[0] >= lastTrade.BreakEvenThreshold)
                    {
                        breakEvenDirection = "long entry";
                        PrintDebug($"[BREAK_EVEN] threshold reached, {Position.AveragePrice}");
                    }

                    if (tradeBias == TradeBias.Short && Low[0] <= lastTrade.BreakEvenThreshold)
                    {
                        breakEvenDirection = "short entry";
                        PrintDebug($"[BREAK_EVEN] threshold reached, {Position.AveragePrice}");
                    }
                }

                if (!string.IsNullOrEmpty(breakEvenDirection))
                {
                    SetStopLoss(breakEvenDirection, CalculationMode.Price, Position.AveragePrice, true);
                    lastTrade.StopLoss = Position.AveragePrice;
                    PrintDebug($"[BREAK_EVEN] {Position.AveragePrice}");
                }
            }
        }

        private double AdjustRisk()
        {
            var riskAdjusted = Risk;
            if (AdjustRiskBasedOnLosses && ConsecutiveLosses >= 2)
            {
                riskAdjusted = Risk / 2;
                PrintDebug($"Adjust risk to {riskAdjusted}");
            }

            if (EdgeHours.Split(',').Contains(Time[0].Hour.ToString()))
            {
                riskAdjusted = riskAdjusted * EdgeHourMultiplier;
                PrintDebug($"Edge hour, adjust risk to {riskAdjusted}");
            }

            if (atrValue > 16)
            {
                //if (atrValue >= 20 && atrValue < 25)
                //{
                //    riskAdjusted = riskAdjusted * 0.75;
                //}

                //if (atrValue >= 25 && atrValue < 30)
                //{
                //    riskAdjusted = riskAdjusted * 0.5;
                //}

                if (atrValue > 30)
                {
                    riskAdjusted = riskAdjusted * 0.50;
                    PrintDebug($"[ATR_ABOVE_STOP_RA] {atrValue} risk {riskAdjusted}");
                }

                PrintDebug($"[ATR_ABOVE_STOP] {atrValue} risk {riskAdjusted}");
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

        private void ProcessSharedBorderRange(bool isLoss)
        {
            var current = Ranges.FirstOrDefault(r => r.IsInRange(lastTrade.StopLoss));
            var previous = Ranges.FirstOrDefault(r => r.IsInRange(lastTrade.TakeProfit));
            if (isLoss == false)
            {
                current = Ranges.FirstOrDefault(r => r.IsInRange(lastTrade.TakeProfit));
                previous = Ranges.FirstOrDefault(r => r.IsInRange(lastTrade.StopLoss));
            }

            var currentSharedBorderRange = GetSharedBorderRange(current, isLoss, TradingSession.RangeBias);
            if (SharedBorderTrade == null)
            {
                SharedBorderTrade = new SharedBorderTrade() { Trades = 0, Border = currentSharedBorderRange };
                PrintDebug($"[RANGE_INVALID_UPDATE] Shared border range set to {SharedBorderTrade.Border}");
            }

            if (SharedBorderTrade.Border == currentSharedBorderRange)
            {
                SharedBorderTrade.Trades++;

                PrintDebug($"[RANGE_INVALID_UPDATE] Shared border range {SharedBorderTrade.Border} trades incremented to {SharedBorderTrade.Trades}");

                if (SharedBorderTrade.Trades >= 1)
                    CreateInvalidRange(current, previous, isLoss);
            }
        }

        private double GetSharedBorderRange(DealingRange applicable, bool isLoss, RangeBias rangeBias)
        {
            if (rangeBias == RangeBias.Bearish)
            {
                if (lastTrade.Bias == TradeBias.Short) return isLoss ? applicable.Low : applicable.High;
                if (lastTrade.Bias == TradeBias.Long) return isLoss ? applicable.High : applicable.Low;
            }

            if (rangeBias == RangeBias.Bullish)
            {
                if (lastTrade.Bias == TradeBias.Long) return isLoss ? applicable.High : applicable.Low;
                if (lastTrade.Bias == TradeBias.Short) return isLoss ? applicable.Low : applicable.High;
            }

            return 0;
        }

        private void CreateInvalidRange(DealingRange current, DealingRange previous, bool isLoss)
        {
            if (InvalidRange != null)
                return;

            if (TradingSession.RangeBias == RangeBias.Bearish && lastTrade.Bias == TradeBias.Short)
                InvalidRange = new InvalidRange(isLoss ? current.BR3 : previous.BR3, isLoss ? previous.BR2 : current.BR2);

            if (TradingSession.RangeBias == RangeBias.Bearish && lastTrade.Bias == TradeBias.Long)
                InvalidRange = new InvalidRange(isLoss ? previous.BR3 : current.BR3, isLoss ? current.BR2 : previous.BR2);

            if (TradingSession.RangeBias == RangeBias.Bullish && lastTrade.Bias == TradeBias.Long)
                InvalidRange = new InvalidRange(isLoss ? previous.BR3 : current.BR3, isLoss ? current.BR2 : previous.BR2);

            if (TradingSession.RangeBias == RangeBias.Bullish && lastTrade.Bias == TradeBias.Short)
                InvalidRange = new InvalidRange(isLoss ? current.BR3 : previous.BR3, isLoss ? previous.BR2 : current.BR2);

            PrintDebug($"[CREATE_INVALID_RANGE] Invalid range created {TradingSession.RangeBias} {lastTrade.Bias} {InvalidRange}");
        }

        private DealingRange CreateDealingRange()
        {
            var newDealingRange = new DealingRange(PO3, Close[0]);
            Ranges.Add(newDealingRange);
            PrintDebug($"Creating new range: {newDealingRange}");
            return newDealingRange;
        }

        private void CancelLimitOrder()
        {
            lastTrade = null;

            if (PendingOrder == null)
                return;

            CancelOrder(PendingOrder);
            PendingOrder = null;
            PrintDebug("CANCEL_LIMIT_ORDER");
        }

        private bool FilterByEvents()
        {
            var newsEvent = events.FirstOrDefault(n => Time[0].Date == n.EventTime.Date && Time[0].TimeOfDay >= n.EventTime.TimeOfDay);
            return newsEvent != null;
        }
    }

    internal class TradeDetails
    {
        public bool IsLoss { get; set; }
        internal TradeBias Bias { get; set; }
        internal double Atr { get; set; }
        internal double Entry { get; set; } = 0;
        internal double StopLoss { get; set; } = 0;
        internal double TakeProfit { get; set; } = 0;
        internal double TradeMissedThreshold { get; set; } = 0;
        internal double BreakEvenThreshold { get; set; } = 0;
    }
}