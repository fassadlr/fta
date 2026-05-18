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
    public class AlgoPO3v6 : AlgoPO3Base
    {
        private Order LimitOrder { get; set; }
        private List<DealingRange> Ranges { get; set; }

        private DealingRange CurrentRange { get; set; }
        private DealingRange PreviousRange { get; set; }

        private MSSBias mssBias;
        private RangeBias rangeBias;
        private TradeBias tradeBias;

        private bool rangeBreakDetected = false;

        private double StopLoss { get; set; }
        private double TakeProfitPrice { get; set; }
        private double TradeMissedThreshold { get; set; }
        private double BreakEvenThreshold { get; set; }

        private int consecutiveLosses { get; set; }

        private double lastBOSLocation = 0;

        private NewsFilter newsFilter;

        private TradeBias lastTradeBias;

        private ATR atr;
        private DailyAtr dailyAtr;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "FassaPO3 v6.2";
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
                SkipHours = "4,5,7,10";
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

                Events = NewsEvents.Events();

                consecutiveLosses = 0;

                tradeBias = TradeBias.Neutral;
                rangeBias = RangeBias.Neutral;
                mssBias = MSSBias.Neutral;
                lastTradeBias = TradeBias.Neutral;

                newsFilter = new NewsFilter(IncludeCPIWeek, IncludeFOMCWeek, IncludeNFPWeek, IncludeNoEventsWeek);
            }
            else if (State == State.DataLoaded)
            {
                atr = ATR(BarsArray[1], 14);

                BlockEndHoursParsed = [.. BlockEndHours.Split(',').Select((i) => int.Parse(i) + SessionTimeOffset)];
                EdgeHoursParsed = [.. EdgeHours.Split(',').Select((i) => int.Parse(i) + SessionTimeOffset)];
                SkipHoursParsed = [.. SkipHours.Split(',').Select((i) => int.Parse(i) + SessionTimeOffset)];

                SessionStart = SessionStart + SessionTimeOffset;
                SessionEnd = SessionEnd + SessionTimeOffset;

                PrintDebug($"Session Start: {SessionStart}, Session End: {SessionEnd}");
                PrintDebug($"BlockEndHoursParsed: {string.Join(",", BlockEndHoursParsed)}");
                PrintDebug($"EdgeHoursParsed: {string.Join(",", EdgeHoursParsed)}");
                PrintDebug($"SkipHoursParsed: {string.Join(",", SkipHoursParsed)}");
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 0 && Time[0].Hour == SessionStart && Time[0].Minute == 0 && Time[0].Second == 0)
            {
                PerformCleanupBeforeSessionStart();
                return;
            }

            dailyAtr ??= new DailyAtr();

            if (SkipMonths.Split(',').Contains(Time[0].Month.ToString()))
                return;

            if (!Events.IsInWeek(newsFilter, Time[0]))
            {
                //PrintDebug($"Skipping as NFP {newsFilter.IncludeNFP} CPI {newsFilter.IncludeCPI} FOMC {newsFilter.IncludeFOMC} NoEvents {newsFilter.IncludeNoEvents}...");
                return;
            }

            // Filter by news events (TODO, not sure if this still needed)
            if (FilterByEvents() && Position.MarketPosition == MarketPosition.Flat)
                return;

            if (TradingSession == null)
            {
                TradingSession = new TradeSessionState(this);
                PrintDebug($"Creating new trading session.");
            }

            // Adjust time for NY/UK BST overlap
            var adjustSessionEnd = AdjustSessionEnd();

            // If the session has ended stop processing.
            if (Time[0].Hour >= adjustSessionEnd && Position.MarketPosition == MarketPosition.Flat)
            {
                CancelLimitOrder();
                return;
            }

            // Always process fractal and BOS detection.
            if (CurrentBar >= 5 && BarsInProgress == 1)
            {
                dailyAtr.Calculate(High[0], Low[0], Close[0]);

                DetectFractalWithPlatos();
                DetectMSS();
                DetectBOS();

                TradingSession.Update(Close[0], atr[0]);
            }

            if (BarsInProgress != 0)
                return;

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

                    rangeBreakDetected = true;
                    PrintDebug($"Range break detected, range set to {CurrentRange}, Bias {tradeBias}");
                }
            }

            CurrentRange.UpdateCoverage(Close[0]);
            PreviousRange?.UpdateCoverage(Close[0]);

            // The strategy only runs from a given time in the morning (8am)
            if (Time[0].Hour < SessionStart)
                return;

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
                return;

            // Filter time of day and cancel if we are flat.
            if (SkipHoursParsed.Contains(Time[0].Hour))
            {
                CancelLimitOrder();
                return;
            }

            // Cancel limit orders close to end of sessions blocks.
            if (BlockEndHoursParsed.Contains(Time[0].Hour) && Time[0].Minute >= 40)
            {
                CancelLimitOrder();
                return;
            }

            // Check where the last BOS happened
            if (rangeBreakDetected && tradeBias == TradeBias.Short && Close[0] < lastBOSLocation && lastBOSLocation != 0)
            {
                var applicableRange = Ranges.FirstOrDefault(r => r.IsInRange(lastBOSLocation));
                if (applicableRange != null && CurrentRange != applicableRange && Close[0] > applicableRange.Eq)
                {
                    rangeBreakDetected = false;

                    PrintDebug($"Range break ignored as last BOS in previous range {lastBOSLocation}");
                    return;
                }
            }

            if (rangeBreakDetected && tradeBias == TradeBias.Long && Close[0] > lastBOSLocation && lastBOSLocation != 0)
            {
                var applicableRange = Ranges.FirstOrDefault(r => r.IsInRange(lastBOSLocation));
                if (applicableRange != null && CurrentRange != applicableRange && Close[0] < applicableRange.Eq)
                {
                    rangeBreakDetected = false;

                    PrintDebug($"Range break ignored as last BOS in previous range {lastBOSLocation}");
                    return;
                }
            }

            if (!rangeBreakDetected)
                return;

            // If we set a limit order and price gets to TP we need to clear the limit order
            if (LimitOrder != null)
            {
                var shouldCancel = false;
                if (lastTradeBias != tradeBias)
                {
                    shouldCancel = true;
                    PrintDebug($"Trade bias changed from {lastTradeBias} to {tradeBias}, resetting.");
                }
                else
                {
                    if ((tradeBias == TradeBias.Long && High[0] >= TradeMissedThreshold) ||
                       (tradeBias == TradeBias.Short && Low[0] <= TradeMissedThreshold))
                    {
                        PrintDebug($"{tradeBias} price went to TP threshold {TradeMissedThreshold}, resetting");
                        shouldCancel = true;
                    }
                }

                if (shouldCancel)
                {
                    CancelLimitOrder();
                    rangeBias = RangeBias.Neutral;
                }

                return;
            }

            if (BullishBOSDetected && CurrentRange.Low == PreviousRange.High)
            {
                rangeBias = RangeBias.Bullish;
                PrintDebug($"Range bias {rangeBias}: Trade bias {tradeBias}");
            }

            if (BullishBOSDetected && CurrentRange.High == PreviousRange.Low)
            {
                rangeBias = RangeBias.Bearish;
                PrintDebug($"Range bias {rangeBias}: Trade bias {tradeBias}");
            }

            if (BearishBOSDetected && CurrentRange.High == PreviousRange.Low)
            {
                rangeBias = RangeBias.Bearish;
                PrintDebug($"Range bias {rangeBias}: Trade bias {tradeBias}");
            }

            if (BearishBOSDetected && CurrentRange.Low == PreviousRange.High)
            {
                rangeBias = RangeBias.Bullish;
                PrintDebug($"Range bias {rangeBias}: Trade bias {tradeBias}");
            }

            if (rangeBias == RangeBias.Neutral)
                return;

            if (IsLive && State != State.Realtime)
                return;

            //if (Time[0].Hour >= (8 + SessionTimeOffset) && TradingSession.IsPriceAtDailyRangeLevels(Close[0]))
            //{
            //    PrintDebug($"[DAILY_RANGE_SKIP] {Close[0]}");
            //    return;
            //}

            var atrNow = atr[0];
            if (atrNow > 50)
            {
                PrintDebug($"[EXCESSIVE_ATR] skipping trade, ATR {atrNow}");
                return;
            }

            if (atrNow == 0)
            {
                PrintDebug($"[INITIAL_ATR] skipping trade, ATR {atrNow}");
                return;
            }

            var entryPrice = 0.0;

            if (rangeBias == RangeBias.Bearish)
            {
                if (tradeBias == TradeBias.Long)
                {
                    entryPrice = CurrentRange.OBCE2 + TickSize;
                    StopLoss = CurrentRange.BR4;
                    //StopLoss = CurrentRange.BR4 + Math.Round((CurrentRange.FV3 - CurrentRange.BR4) * 0.33, 2);
                    TakeProfitPrice = PreviousRange.MBCE1;
                    TradeMissedThreshold = PreviousRange.BR3;
                    BreakEvenThreshold = PreviousRange.BR2;
                }

                if (tradeBias == TradeBias.Short)
                {
                    entryPrice = CurrentRange.ExtensionHighCE - TickSize;
                    StopLoss = PreviousRange.BR1;
                    //StopLoss = PreviousRange.BR1 - Math.Round((PreviousRange.BR1 - PreviousRange.FV2) * 0.33, 2);
                    TakeProfitPrice = CurrentRange.MBCE2;
                    TradeMissedThreshold = CurrentRange.BR2;
                    BreakEvenThreshold = CurrentRange.BR3;
                }
            }

            if (rangeBias == RangeBias.Bullish)
            {
                if (tradeBias == TradeBias.Long)
                {
                    entryPrice = CurrentRange.ExtensionLowCE + TickSize;
                    StopLoss = PreviousRange.BR4;
                    //StopLoss = PreviousRange.BR4 + Math.Round((PreviousRange.FV3 - PreviousRange.BR4) * 0.33, 2);
                    TakeProfitPrice = CurrentRange.MBCE1;
                    TradeMissedThreshold = CurrentRange.BR3;
                    BreakEvenThreshold = CurrentRange.BR2;
                }

                if (tradeBias == TradeBias.Short)
                {
                    entryPrice = CurrentRange.OBCE1 - TickSize;
                    StopLoss = CurrentRange.BR1;
                    //StopLoss = CurrentRange.BR1 - Math.Round((CurrentRange.BR1 - CurrentRange.FV2) * 0.33, 2);
                    TakeProfitPrice = PreviousRange.MBCE2;
                    TradeMissedThreshold = PreviousRange.BR2;
                    BreakEvenThreshold = PreviousRange.BR3;
                }
            }

            entryPrice = Math.Round(entryPrice / 0.25) * 0.25;

            if (tradeBias == TradeBias.Long)
            {
                var adjustedRisk = AdjustRisk();

                if (Close[0] > entryPrice)
                {
                    PrintDebug($"[PLACE_TRADE_LIMIT] {tradeBias} : {entryPrice} {Close[0]}");
                    LimitOrder = EnterLongLimit(0, true, CalculatePositionSize(entryPrice - StopLoss, adjustedRisk), entryPrice, "long entry");
                }
                else
                {
                    PrintDebug($"[PLACE_TRADE_STOP_MARKET] {tradeBias} : {entryPrice} {Close[0]}");
                    LimitOrder = EnterLongStopMarket(0, true, CalculatePositionSize(entryPrice - StopLoss, adjustedRisk), entryPrice, "long entry");
                }

                lastTradeBias = TradeBias.Long;

                if (atr[0] > 19)
                {
                    StopLoss = entryPrice - Math.Round(atr[0]);
                }

                SetStopLoss("long entry", CalculationMode.Price, StopLoss, true);
                PrintDebug($"Stop {StopLoss}");
                SetProfitTarget("long entry", CalculationMode.Price, TakeProfitPrice, true);
            }

            if (tradeBias == TradeBias.Short)
            {
                var adjustedRisk = AdjustRisk();

                if (Close[0] < entryPrice)
                {
                    PrintDebug($"[PLACE_TRADE_LIMIT] {tradeBias} : {entryPrice} {Close[0]}");
                    LimitOrder = EnterShortLimit(0, true, CalculatePositionSize(StopLoss - entryPrice, adjustedRisk), entryPrice, "short entry");
                }
                else
                {
                    PrintDebug($"[PLACE_TRADE_STOP_MARKET] {tradeBias} : {entryPrice} {Close[0]}");
                    LimitOrder = EnterShortStopMarket(0, true, CalculatePositionSize(StopLoss - entryPrice, adjustedRisk), entryPrice, "short entry");
                }

                lastTradeBias = TradeBias.Short;

                if (atr[0] > 19)
                {
                    StopLoss = entryPrice + Math.Round(atr[0]);
                }

                SetStopLoss("short entry", CalculationMode.Price, StopLoss, true);
                PrintDebug($"Stop {StopLoss}");
                SetProfitTarget("short entry", CalculationMode.Price, TakeProfitPrice, true);
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order.Name == "Profit target")
            {
                // the profit target has had a fill or part fill
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    var lastTrade = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
                    PrintTrade($"{lastTrade.Entry.Time},{LimitOrder.IsStopMarket},Win,{lastTrade.ProfitCurrency},{lastTrade.Quantity},{Events.IsCPIWeek(Time[0])},{Events.IsNFPWeek(Time[0])},{Events.IsFOMCWeek(Time[0])},{Events.IsNoEventWeek(Time[0])}");

                    ProcessSharedBorderRange(false);

                    LimitOrder = null;

                    consecutiveLosses = 0;

                    rangeBreakDetected = false;
                }
            }

            if (execution.Order.Name == "Stop loss")
            {
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    var lastTrade = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
                    PrintTrade($"{lastTrade.Entry.Time},{LimitOrder.IsStopMarket},Loss,{lastTrade.ProfitCurrency},{lastTrade.Quantity},{Events.IsCPIWeek(Time[0])},{Events.IsNFPWeek(Time[0])},{Events.IsFOMCWeek(Time[0])},{Events.IsNoEventWeek(Time[0])}");

                    ProcessSharedBorderRange(true);

                    consecutiveLosses++;

                    LimitOrder = null;

                    rangeBreakDetected = false;
                }
            }

            if (execution.Order.Name == "Trail stop")
            {
                // the trailing stop loss has had a fill or part fill
            }
        }

        private void DetectMSS()
        {
            //PrintDebug($"{tradeBias} {lastFractalLow} {lastFractalHigh} {Close[0]}");
            if (tradeBias == TradeBias.Short && Close[0] > LastFractalHigh && LastFractalHigh != 0)
            {
                mssBias = MSSBias.Bullish;
                PrintDebug($"Bullish MSS detected");
            }

            if (tradeBias == TradeBias.Long && Close[0] < LastFractalLow && LastFractalLow != 0)
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

                        tradeBias = TradeBias.Long;

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

                        tradeBias = TradeBias.Short;

                        if (mssBias == MSSBias.Neutral) mssBias = MSSBias.Bearish;

                        lastBOSLocation = LastFractalLow;

                        PrintDebug($"Bearish BOS detected, MSS bias set to {mssBias} BOS price {lastBOSLocation}");
                    }
                }
            }
        }

        private void PerformCleanupBeforeSessionStart()
        {
            Ranges.Clear();

            CancelLimitOrder();

            CurrentRange = null;
            PreviousRange = null;
            rangeBreakDetected = false;

            InvalidRange = null;
            SharedBorderTrade = null;

            LastFractalHigh = 0;
            LastFractalLow = 0;

            mssBias = MSSBias.Neutral;
            lastTradeBias = TradeBias.Neutral;
            rangeBias = RangeBias.Neutral;
            tradeBias = TradeBias.Neutral;

            BullishBOSDetected = false;
            BearishBOSDetected = false;
            lastBOSLocation = 0;

            dailyAtr = null;

            consecutiveLosses = 0;

            PrintDebug("Start of session, resetting state...");
        }

        private void BreakEvenOrCloseTrade()
        {
            var marketOpen = IsDSTOverlap() ? 13 : 14;

            if (Time[0].Hour == marketOpen && Time[0].Minute == 29)
            {
                PrintDebug($"Closing trade before market open, PnL {Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0])}");

                if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("short entry");

                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("long entry");
            }
            else
            {
                if (StopLoss == Position.AveragePrice)
                    return;

                var breakEvenDirection = "";

                if (lastTradeBias != tradeBias)
                {
                    if (lastTradeBias == TradeBias.Long && High[0] >= BreakEvenThreshold)
                    {
                        PrintDebug($"Setting trade to BE trade bias changed from {lastTradeBias} to {tradeBias}, {Position.AveragePrice}");
                        breakEvenDirection = "long entry";
                    }

                    if (lastTradeBias == TradeBias.Short && Low[0] <= BreakEvenThreshold)
                    {
                        breakEvenDirection = "short entry";
                        PrintDebug($"Setting trade to BE trade bias changed from {lastTradeBias} to {tradeBias}, {Position.AveragePrice}");
                    }
                }
                else
                {
                    if (tradeBias == TradeBias.Long && High[0] >= BreakEvenThreshold)
                    {
                        breakEvenDirection = "long entry";
                        PrintDebug($"Setting trade to BE as threshold reached, {Position.AveragePrice}");
                    }

                    if (tradeBias == TradeBias.Short && Low[0] <= BreakEvenThreshold)
                    {
                        breakEvenDirection = "short entry";
                        PrintDebug($"Setting trade to BE as threshold reached, {Position.AveragePrice}");
                    }
                }

                if (!string.IsNullOrEmpty(breakEvenDirection))
                {
                    SetStopLoss(breakEvenDirection, CalculationMode.Price, Position.AveragePrice, true);
                    StopLoss = Position.AveragePrice;
                    PrintDebug($"BE set, {Position.AveragePrice}");
                }
            }
        }

        private double AdjustRisk()
        {
            var riskAdjusted = Risk;

            if (AdjustRiskBasedOnLosses && consecutiveLosses >= 2)
            {
                riskAdjusted = Risk / 2;
                PrintDebug($"Adjust risk to {riskAdjusted}");
            }

            if (EdgeHoursParsed.Contains(Time[0].Hour))
            {
                riskAdjusted = riskAdjusted * EdgeHourMultiplier;
                PrintDebug($"Edge hour, adjust risk to {riskAdjusted}");
            }

            var atrNow = atr[0];
            if (atrNow > 16)
            {
                //if (dailyAtr.Atr >= 20 && dailyAtr.Atr < 25)
                //{
                //    riskAdjusted = riskAdjusted * 0.75;
                //}

                //if (dailyAtr.Atr >= 25 && dailyAtr.Atr < 30)
                //{
                //    riskAdjusted = riskAdjusted * 0.5;
                //}

                if (atrNow > 30)
                {
                    riskAdjusted = riskAdjusted * 0.50;
                    PrintDebug($"[ATR_ABOVE_STOP_RA] {atrNow} risk {riskAdjusted}");
                }

                PrintDebug($"[ATR_ABOVE_STOP] {atrNow} risk {riskAdjusted}");
            }

            return Math.Round(riskAdjusted);
        }

        private void ProcessSharedBorderRange(bool isLoss)
        {
            var current = Ranges.FirstOrDefault(r => r.IsInRange(StopLoss));
            var previous = Ranges.FirstOrDefault(r => r.IsInRange(TakeProfitPrice));
            if (isLoss == false)
            {
                current = Ranges.FirstOrDefault(r => r.IsInRange(TakeProfitPrice));
                previous = Ranges.FirstOrDefault(r => r.IsInRange(StopLoss));
            }

            var currentSharedBorderRange = GetSharedBorderRange(current, isLoss, rangeBias, lastTradeBias);
            if (SharedBorderTrade != null && SharedBorderTrade.Border == currentSharedBorderRange)
            {
                SharedBorderTrade.Trades++;

                PrintDebug($"[RANGE_INVALID] Shared border range {SharedBorderTrade.Border} trades incremented to {SharedBorderTrade.Trades}");
                if (SharedBorderTrade.Trades >= 2)
                    CreateInvalidRange(current, previous, isLoss, lastTradeBias);
            }
            else
            {
                SharedBorderTrade = new SharedBorderTrade() { Trades = 1, Border = currentSharedBorderRange };
                PrintDebug($"[RANGE_INVALID] Shared border range set to {SharedBorderTrade.Border}");
            }
        }

        private static double GetSharedBorderRange(DealingRange applicable, bool isLoss, RangeBias rangeBias, TradeBias tradeBias)
        {
            if (rangeBias == RangeBias.Bearish)
            {
                if (tradeBias == TradeBias.Short) return isLoss ? applicable.Low : applicable.High;
                if (tradeBias == TradeBias.Long) return isLoss ? applicable.High : applicable.Low;
            }

            if (rangeBias == RangeBias.Bullish)
            {
                if (tradeBias == TradeBias.Long) return isLoss ? applicable.High : applicable.Low;
                if (tradeBias == TradeBias.Short) return isLoss ? applicable.Low : applicable.High;
            }

            return 0;
        }

        private void CreateInvalidRange(DealingRange current, DealingRange previous, bool isLoss, TradeBias lastTradeBias)
        {
            if (InvalidRange != null)
                return;

            if (rangeBias == RangeBias.Bearish && lastTradeBias == TradeBias.Short)
                InvalidRange = new InvalidRange(isLoss ? current.BR3 : previous.BR3, isLoss ? previous.BR2 : current.BR2);

            if (rangeBias == RangeBias.Bearish && lastTradeBias == TradeBias.Long)
                InvalidRange = new InvalidRange(isLoss ? previous.BR3 : current.BR3, isLoss ? current.BR2 : previous.BR2);

            if (rangeBias == RangeBias.Bullish && lastTradeBias == TradeBias.Long)
                InvalidRange = new InvalidRange(isLoss ? previous.BR3 : current.BR3, isLoss ? current.BR2 : previous.BR2);

            if (rangeBias == RangeBias.Bullish && lastTradeBias == TradeBias.Short)
                InvalidRange = new InvalidRange(isLoss ? current.BR3 : previous.BR3, isLoss ? previous.BR2 : current.BR2);

            PrintDebug($"[RANGE_INVALID_CREATED] Invalid range created {rangeBias} {lastTradeBias} {InvalidRange}");
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
            if (LimitOrder == null)
                return;

            CancelOrder(LimitOrder);
            LimitOrder = null;
            PrintDebug("Cancel limit order...");
            lastTradeBias = TradeBias.Neutral;
        }
    }
}