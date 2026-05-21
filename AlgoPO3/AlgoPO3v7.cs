using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

//This namespace holds Strategies in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public class FassaPO3v7 : AlgoPO3Base
    {
        private ATR atr;

        private List<NewsEvent> events;

        private Order LimitOrder { get; set; }

        private List<DealingRange> Ranges { get; set; }
        private DealingRange CurrentRange { get; set; }
        private DealingRange PreviousRange { get; set; }

        private MSSBias mssBias;
        private TradeBias tradeBias;

        private int ConsecutiveLosses { get; set; }

        private SharedBorderTrade sharedBorderTrade;

        private double lastBOSLocation = 0;

        private NewsFilter newsFilter;

        private TradeSessionState tradingSession;

        private TradeDetails currentTrade;

        //private DailyAtr dailyAtr;

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
                Risk = 300;
                IsOverlay = true;
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
            if (BarsInProgress == 0 && Bars.IsFirstBarOfSession)
            {
                PerformCleanupBeforeStart();
                return;
            }

            if (SkipMonths.Split(',').Contains(Time[0].Month.ToString()))
                return;

            if (!events.IsInWeek(newsFilter, Time[0]))
            {
                PrintDebug($"Skipping as NFP {newsFilter.IncludeNFP} CPI {newsFilter.IncludeCPI} FOMC {newsFilter.IncludeFOMC} NoEvents {newsFilter.IncludeNoEvents}...");
                return;
            }

            // Filter by news events (TODO, not sure if this still needed)
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

            //dailyAtr ??= new DailyAtr();

            // Always process fractal and BOS detection on 5min.
            if (CurrentBar >= 5 && BarsInProgress == 1)
            {
                if (tradingSession == null)
                {
                    tradingSession = new TradeSessionState(this);
                    PrintDebug($"Creating new trading session.");
                }

                //dailyAtr.Calculate(High[0], Low[0], Close[0]);

                // Create the initial dealing range, this will only happen once per session.
                if (CurrentRange == null)
                {
                    CurrentRange = CreateDealingRange();
                    return;
                }

                DetectFractalWithPlatos();
                DetectMSS();
                DetectBOS();

                // Determine if we are still in the same dealing range.
                var found = Ranges.FirstOrDefault(r => r.IsInRange(Close[0])) ?? CreateDealingRange();

                // Always process range breaks
                if (found != CurrentRange)
                {
                    if (PreviousRange != CurrentRange)
                    {
                        PreviousRange = CurrentRange;
                        CurrentRange = found;

                        PrintDebug($"[RANGE_BREAK_DETECTED] New range {CurrentRange}");
                    }
                }

                CurrentRange.UpdateCoverage(Close[0]);
                PreviousRange?.UpdateCoverage(Close[0]);
                tradingSession.Update(CurrentRange, PreviousRange, Open[0], Close[0], High[0], Low[0], Time[0], atr[0]);
                tradeBias = tradingSession.CalculateTradeBias(Time[0], Open[0], Close[0], High[0], Low[0]);
                PrintDebug($"Trade Bias: {tradeBias}, {tradingSession.Info()}");

                return;
            }

            // The strategy only runs from a given time.
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
            if (LimitOrder != null)
            {
                if ((currentTrade.Bias == TradeBias.Long && High[0] >= currentTrade.TradeMissedThreshold) ||
                   (currentTrade.Bias == TradeBias.Short && Low[0] <= currentTrade.TradeMissedThreshold))
                {
                    PrintDebug($"{currentTrade.Bias} price went to TP threshold {currentTrade.TradeMissedThreshold}, resetting");
                    CancelLimitOrder();
                }

                return;
            }

            if (IsLive && State != State.Realtime)
                return;

            // Half of PO3 range.
            if (atr[0] > 41)
            {
                PrintDebug($"[EXCESSIVE_ATR] skipping trade, ATR {atr[0]}");
                return;
            }

            if (atr[0] == 0)
            {
                PrintDebug($"[INITIAL_ATR] skipping trade, ATR {atr[0]}");
                return;
            }

            if (tradeBias == TradeBias.Short)
            {
                currentTrade = new TradeDetails()
                {
                    Bias = tradeBias,
                    Atr = atr[0],
                    Entry = CurrentRange.ExtensionHighCE - TickSize,
                    StopLoss = PreviousRange.BR1 - Math.Round((PreviousRange.BR1 - PreviousRange.FV2) * 0.33, 2),
                    TakeProfit = CurrentRange.MBCE2,
                    TradeMissedThreshold = Math.Round(CurrentRange.Low + (PO3 * 0.03), 2),
                    BreakEvenThreshold = CurrentRange.BR3,
                };

                currentTrade.Entry = Math.Round(currentTrade.Entry / 0.25) * 0.25;

                if (Close[0] < currentTrade.Entry)
                {
                    PrintDebug($"[PLACE_TRADE] {tradeBias} entry at {currentTrade.Entry} TMT {currentTrade.TradeMissedThreshold} BET {currentTrade.BreakEvenThreshold}");
                    LimitOrder = EnterShortLimit(0, true, CalculatePositionSize(currentTrade.StopLoss - currentTrade.Entry), currentTrade.Entry, "short entry");

                    if (atr[0] > 16)
                    {
                        currentTrade.StopLoss = currentTrade.Entry + Math.Round(atr[0]);
                        PrintDebug($"[ATR_ADJ_STOP] Atr {atr[0]} StopLoss {currentTrade.StopLoss}");

                        //if (dailyAtr.Atr >= 30)
                        //{
                        //    StopLoss = entryPrice - 30;
                        //    PrintDebug($"[ATR_ADJ_STOP] {StopLoss}");
                        //}
                    }

                    PrintDebug($"Stop {currentTrade.StopLoss}");

                    SetStopLoss("short entry", CalculationMode.Price, currentTrade.StopLoss, true);
                    SetProfitTarget("short entry", CalculationMode.Price, currentTrade.TakeProfit, true);
                }
                else
                    PrintDebug($"[PRICE_BELOW_ENTRY] {Close[0]} below {currentTrade.Entry}");
            }

            if (tradeBias == TradeBias.Long)
            {
                currentTrade = new TradeDetails()
                {
                    Bias = tradeBias,
                    Atr = atr[0],
                    Entry = CurrentRange.ExtensionLowCE + TickSize,
                    StopLoss = PreviousRange.BR4 + Math.Round((PreviousRange.FV3 - PreviousRange.BR4) * 0.33, 2),
                    TakeProfit = CurrentRange.MBCE1,
                    TradeMissedThreshold = Math.Round(CurrentRange.High - (PO3 * 0.03), 2),
                    BreakEvenThreshold = CurrentRange.BR2
                };

                currentTrade.Entry = Math.Round(currentTrade.Entry / 0.25) * 0.25;

                if (Close[0] > currentTrade.Entry)
                {
                    PrintDebug($"[PLACE_TRADE] {currentTrade.Bias} entry at {currentTrade.Entry} TMT {currentTrade.TradeMissedThreshold} BET");
                    LimitOrder = EnterLongLimit(0, true, CalculatePositionSize(currentTrade.Entry - currentTrade.StopLoss), currentTrade.Entry, "long entry");

                    if (atr[0] > 16)
                    {
                        currentTrade.StopLoss = currentTrade.Entry - Math.Round(atr[0]);
                        PrintDebug($"[ATR_ADJ_STOP] Atr {atr[0]} StopLoss {currentTrade.StopLoss}");

                        //if (dailyAtr.Atr >= 30)
                        //{
                        //    StopLoss = entryPrice - 30;
                        //    PrintDebug($"[ATR_ADJ_STOP] {StopLoss}");
                        //}
                    }
                    PrintDebug($"Stop {currentTrade.StopLoss}");

                    SetStopLoss("long entry", CalculationMode.Price, currentTrade.StopLoss, true);
                    SetProfitTarget("long entry", CalculationMode.Price, currentTrade.TakeProfit, true);
                }
                else
                    PrintDebug($"[PRICE_BELOW_ENTRY] {Close[0]} above {currentTrade.Entry}");
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
                    PrintDebug("TRADE_WIN");
                    PrintTrade($"{lastTrade.Entry.Time},{LimitOrder.IsStopMarket},Win,{lastTrade.ProfitCurrency},{lastTrade.Quantity},{Math.Round(currentTrade.Atr)},{events.IsCPIWeek(Time[0])},{events.IsNFPWeek(Time[0])},{events.IsFOMCWeek(Time[0])},{events.IsNoEventWeek(Time[0])}");

                    ProcessSharedBorderRange(false);

                    ConsecutiveLosses = 0;

                    LimitOrder = null;
                }
            }

            if (execution.Order.Name == "Stop loss")
            {
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    var lastTrade = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
                    PrintDebug("TRADE_LOSS");
                    PrintTrade($"{lastTrade.Entry.Time},{LimitOrder.IsStopMarket},Loss,{lastTrade.ProfitCurrency},{lastTrade.Quantity},{Math.Round(currentTrade.Atr)},{events.IsCPIWeek(Time[0])},{events.IsNFPWeek(Time[0])},{events.IsFOMCWeek(Time[0])},{events.IsNoEventWeek(Time[0])}");

                    ProcessSharedBorderRange(true);

                    ConsecutiveLosses++;

                    LimitOrder = null;
                }
            }
        }

        private void DetectMSS()
        {
            //PrintDebug($"{tradeBias} {lastFractalLow} {lastFractalHigh} {Close[0]}");
            if (BearishBOSDetected && Close[0] > LastFractalHigh && LastFractalHigh != 0)
            {
                mssBias = MSSBias.Bullish;
                PrintDebug($"[BULLISH_MSS]");
            }

            if (BullishBOSDetected && Close[0] < LastFractalLow && LastFractalLow != 0)
            {
                mssBias = MSSBias.Bearish;
                PrintDebug($"[BEARISH_MSS]");
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

                        PrintDebug($"[BULLISH_BOS] MSS bias {mssBias} BOS price {lastBOSLocation}");
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

                        PrintDebug($"[BEARISH_BOS] MSS bias {mssBias} BOS price {lastBOSLocation}");
                    }
                }
            }
        }

        private void PerformCleanupBeforeStart()
        {
            PrintDebug("[SESSION_START_CLEANP]");

            Ranges.Clear();

            CancelLimitOrder();

            CurrentRange = null;
            PreviousRange = null;

            InvalidRange = null;
            sharedBorderTrade = null;

            LastFractalHigh = 0;
            LastFractalLow = 0;

            BullishBOSDetected = false;
            BearishBOSDetected = false;

            mssBias = MSSBias.Neutral;
            tradeBias = TradeBias.Neutral;

            lastBOSLocation = 0;

            tradingSession = null;
        }

        private void BreakEvenOrCloseTrade()
        {
            if ((IsDSTOverlap() && Time[0].Hour == 13 && Time[0].Minute == 29) ||
                (!IsDSTOverlap() && Time[0].Hour == 14 && Time[0].Minute == 29))
            {
                PrintDebug($"Setting trade to BE before market open, current PnL {Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0])}");

                if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("short entry");

                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("long entry");
            }
            else
            {
                if (currentTrade.StopLoss == Position.AveragePrice)
                    return;

                var breakEvenDirection = "";

                if (currentTrade.Bias != tradeBias)
                {
                    if (currentTrade.Bias == TradeBias.Long && High[0] >= currentTrade.BreakEvenThreshold)
                    {
                        PrintDebug($"Setting trade to BE trade bias changed from {currentTrade.Bias} to {tradeBias}, {Position.AveragePrice}");
                        breakEvenDirection = "long entry";
                    }

                    if (currentTrade.Bias == TradeBias.Short && Low[0] <= currentTrade.BreakEvenThreshold)
                    {
                        breakEvenDirection = "short entry";
                        PrintDebug($"Setting trade to BE trade bias changed from {currentTrade.Bias} to {tradeBias}, {Position.AveragePrice}");
                    }
                }
                else
                {
                    if (tradeBias == TradeBias.Long && High[0] >= currentTrade.BreakEvenThreshold)
                    {
                        breakEvenDirection = "long entry";
                        PrintDebug($"Setting trade to BE as threshold reached, {Position.AveragePrice}");
                    }

                    if (tradeBias == TradeBias.Short && Low[0] <= currentTrade.BreakEvenThreshold)
                    {
                        breakEvenDirection = "short entry";
                        PrintDebug($"Setting trade to BE as threshold reached, {Position.AveragePrice}");
                    }
                }

                if (!string.IsNullOrEmpty(breakEvenDirection))
                {
                    SetStopLoss(breakEvenDirection, CalculationMode.Price, Position.AveragePrice, true);
                    currentTrade.StopLoss = Position.AveragePrice;
                    PrintDebug($"BE set, {Position.AveragePrice}");
                }
            }
        }

        private double AdjustRisk()
        {
            var riskAdjusted = Risk;
            //if (AdjustRiskBasedOnLosses && ConsecutiveLosses >= 2)
            //{
            //    riskAdjusted = Risk / 2;
            //    PrintDebug($"Adjust risk to {riskAdjusted}");
            //}

            if (EdgeHours.Split(',').Contains(Time[0].Hour.ToString()))
            {
                riskAdjusted = riskAdjusted * EdgeHourMultiplier;
                PrintDebug($"Edge hour, adjust risk to {riskAdjusted}");
            }

            if (atr[0] > 16)
            {
                //if (dailyAtr.Atr >= 20 && dailyAtr.Atr < 25)
                //{
                //    riskAdjusted = riskAdjusted * 0.75;
                //}

                //if (dailyAtr.Atr >= 25 && dailyAtr.Atr < 30)
                //{
                //    riskAdjusted = riskAdjusted * 0.5;
                //}

                if (atr[0] > 30)
                {
                    riskAdjusted = riskAdjusted * 0.50;
                    PrintDebug($"[ATR_ABOVE_STOP_RA] {atr[0]} risk {riskAdjusted}");
                }

                PrintDebug($"[ATR_ABOVE_STOP] {atr[0]} risk {riskAdjusted}");
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
            var current = Ranges.FirstOrDefault(r => r.IsInRange(currentTrade.StopLoss));
            var previous = Ranges.FirstOrDefault(r => r.IsInRange(currentTrade.TakeProfit));
            if (isLoss == false)
            {
                current = Ranges.FirstOrDefault(r => r.IsInRange(currentTrade.TakeProfit));
                previous = Ranges.FirstOrDefault(r => r.IsInRange(currentTrade.StopLoss));
            }

            var currentSharedBorderRange = GetSharedBorderRange(current, isLoss, tradingSession.RangeBias);
            if (sharedBorderTrade != null && sharedBorderTrade.Border == currentSharedBorderRange)
            {
                sharedBorderTrade.Trades++;

                PrintDebug($"[RANGE_INVALID_UPDATE] Shared border range {sharedBorderTrade.Border} trades incremented to {sharedBorderTrade.Trades}");

                if (sharedBorderTrade.Trades >= 2)
                    CreateInvalidRange(current, previous, isLoss);
            }
            else
            {
                sharedBorderTrade = new SharedBorderTrade() { Trades = 1, Border = currentSharedBorderRange };
                PrintDebug($"[RANGE_INVALID_UPDATE] Shared border range set to {sharedBorderTrade.Border}");
            }
        }

        private double GetSharedBorderRange(DealingRange applicable, bool isLoss, RangeBias rangeBias)
        {
            if (rangeBias == RangeBias.Bearish)
            {
                if (currentTrade.Bias == TradeBias.Short) return isLoss ? applicable.Low : applicable.High;
                if (currentTrade.Bias == TradeBias.Long) return isLoss ? applicable.High : applicable.Low;
            }

            if (rangeBias == RangeBias.Bullish)
            {
                if (currentTrade.Bias == TradeBias.Long) return isLoss ? applicable.High : applicable.Low;
                if (currentTrade.Bias == TradeBias.Short) return isLoss ? applicable.Low : applicable.High;
            }

            return 0;
        }

        private void CreateInvalidRange(DealingRange current, DealingRange previous, bool isLoss)
        {
            if (InvalidRange != null)
                return;

            if (tradingSession.RangeBias == RangeBias.Bearish && currentTrade.Bias == TradeBias.Short)
                InvalidRange = new InvalidRange(isLoss ? current.BR3 : previous.BR3, isLoss ? previous.BR2 : current.BR2);

            if (tradingSession.RangeBias == RangeBias.Bearish && currentTrade.Bias == TradeBias.Long)
                InvalidRange = new InvalidRange(isLoss ? previous.BR3 : current.BR3, isLoss ? current.BR2 : previous.BR2);

            if (tradingSession.RangeBias == RangeBias.Bullish && currentTrade.Bias == TradeBias.Long)
                InvalidRange = new InvalidRange(isLoss ? previous.BR3 : current.BR3, isLoss ? current.BR2 : previous.BR2);

            if (tradingSession.RangeBias == RangeBias.Bullish && currentTrade.Bias == TradeBias.Short)
                InvalidRange = new InvalidRange(isLoss ? current.BR3 : previous.BR3, isLoss ? previous.BR2 : current.BR2);

            PrintDebug($"[CREATE_INVALID_RANGE] Invalid range created {tradingSession.RangeBias} {currentTrade.Bias} {InvalidRange}");
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
            PrintDebug("[CANCEL_LIMIT_ORDER]");

            currentTrade = null;
        }
    }

    internal class TradeDetails
    {
        internal TradeBias Bias { get; set; }
        internal double Atr { get; set; }
        internal double Entry { get; set; } = 0;
        internal double StopLoss { get; set; } = 0;
        internal double TakeProfit { get; set; } = 0;
        internal double TradeMissedThreshold { get; set; } = 0;
        internal double BreakEvenThreshold { get; set; } = 0;
        internal OrderType OrderType { get; set; }
        internal double VWAPDistance { get; set; }
    }
}