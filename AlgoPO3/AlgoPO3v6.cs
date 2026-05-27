#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;
#endregion

//This namespace holds Strategies in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public class AlgoPO3v6 : AlgoPO3Base
    {
        [NinjaScriptProperty]
        [Display(Name = "Stop Grace", Order = 19, GroupName = "Parameters")]
        public int StopGrace { get; set; }

        private Order PendingOrder { get; set; }
        private List<DealingRange> Ranges { get; set; }

        private DealingRange CurrentRange { get; set; }
        private DealingRange PreviousRange { get; set; }
        private DateTime? lastInitialization;

        private MSSBias mssBias;
        private RangeBias rangeBias;
        private TradeBias tradeBias;

        private bool rangeBreakDetected = false;

        private TradeDetails CurrentTrade;
        private double lastBOSLocation = 0;

        private NewsFilter newsFilter;

        private ATR atr;
        private DailyAtr dailyAtr;
        private double atrCurrent;
        private OrderFlowVWAP vwap;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "PO3 Algo v6.5";
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
                StopGrace = 3;

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

                tradeBias = TradeBias.Neutral;
                rangeBias = RangeBias.Neutral;
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

                vwap = OrderFlowVWAP(
                        VWAPResolution.Standard,
                        Bars.TradingHours,
                        VWAPStandardDeviations.One,
                        1, 2, 3
                    );
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

            // Filter by news events (TODO, not sure if this still needed)
            if (FilterByEvents() && Position.MarketPosition == MarketPosition.Flat)
                return;

            if (TradingSession == null)
            {
                TradingSession = new TradeSessionState(this);
                PrintDebug($"Creating new trading session.");
            }

            // Always process fractal and BOS detection.
            if (BarsInProgress == 1)
            {
                dailyAtr.Calculate(High[0], Low[0], Close[0]);

                DetectFractalWithPlatos();
                DetectMSS();
                DetectBOS();

                //TradingSession.Update(Close[0], atrCurrent);
            }

            // Adjust time for NY/UK BST overlap
            var adjustSessionEnd = AdjustSessionEnd();

            // If the session has ended stop processing.
            if (Time[0].Hour >= adjustSessionEnd && Position.MarketPosition == MarketPosition.Flat)
            {
                CancelLimitOrder();
                return;
            }

            //atrCurrent = atr[0];
            atrCurrent = dailyAtr.Atr;

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
                    PrintDebug($"[RANGE_BREAK] new range {CurrentRange}, Bias {tradeBias}");
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

                    PrintDebug($"[RANGE_BREAK_IGNORED_SHORT] last BOS in previous range {lastBOSLocation}");
                    return;
                }
            }

            if (rangeBreakDetected && tradeBias == TradeBias.Long && Close[0] > lastBOSLocation && lastBOSLocation != 0)
            {
                var applicableRange = Ranges.FirstOrDefault(r => r.IsInRange(lastBOSLocation));
                if (applicableRange != null && CurrentRange != applicableRange && Close[0] < applicableRange.Eq)
                {
                    rangeBreakDetected = false;

                    PrintDebug($"[RANGE_BREAK_IGNORED_LONG] last BOS in previous range {lastBOSLocation}");
                    return;
                }
            }

            if (!rangeBreakDetected)
                return;

            // If we set a limit order and price gets to TP we need to clear the limit order
            if (PendingOrder != null)
            {
                var shouldCancel = false;
                if (CurrentTrade.Bias != tradeBias)
                {
                    shouldCancel = true;
                    PrintDebug($"[LIMIT_ORDER_CANCEL] Trade bias changed from {CurrentTrade.Bias} to {tradeBias}.");
                }
                else
                {
                    if ((CurrentTrade.Bias == TradeBias.Long && High[0] >= CurrentTrade.TradeMissedThreshold) ||
                       (CurrentTrade.Bias == TradeBias.Short && Low[0] <= CurrentTrade.TradeMissedThreshold))
                    {
                        PrintDebug($"[LIMIT_ORDER_CANCEL] {CurrentTrade.Bias} price went to TP threshold {CurrentTrade.TradeMissedThreshold}.");
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

            if (atrCurrent > 50)
            {
                PrintDebug($"[EXCESSIVE_ATR] skipping trade, ATR {atrCurrent}");
                return;
            }

            if (atrCurrent == 0)
            {
                PrintDebug($"[INITIAL_ATR] skipping trade, ATR {atrCurrent}");
                return;
            }

            if (rangeBias == RangeBias.Bearish)
            {
                if (tradeBias == TradeBias.Long)
                {
                    CurrentTrade = new TradeDetails()
                    {
                        Bias = TradeBias.Long,
                        Atr = atrCurrent,
                        Entry = CurrentRange.OBCE2 + TickSize,
                        StopLoss = CurrentRange.BR4,
                        //StopLoss = CurrentRange.BR4 + Math.Round((CurrentRange.FV3 - CurrentRange.BR4) * 0.33, 2),
                        TakeProfit = PreviousRange.MBCE1,
                        TradeMissedThreshold = PreviousRange.BR3,
                        BreakEvenThreshold = PreviousRange.BR2,
                    };
                }

                if (tradeBias == TradeBias.Short)
                {
                    CurrentTrade = new TradeDetails()
                    {
                        Bias = TradeBias.Short,
                        Atr = atrCurrent,
                        Entry = CurrentRange.ExtensionHighCE - TickSize,
                        StopLoss = PreviousRange.BR1,
                        //StopLoss = PreviousRange.BR1 - Math.Round((PreviousRange.BR1 - PreviousRange.FV2) * 0.33, 2),
                        TakeProfit = CurrentRange.MBCE2,
                        TradeMissedThreshold = CurrentRange.BR2,
                        BreakEvenThreshold = CurrentRange.BR3,
                    };
                }
            }

            if (rangeBias == RangeBias.Bullish)
            {
                if (tradeBias == TradeBias.Long)
                {
                    CurrentTrade = new TradeDetails()
                    {
                        Bias = TradeBias.Long,
                        Atr = atrCurrent,
                        Entry = CurrentRange.ExtensionLowCE + TickSize,
                        StopLoss = PreviousRange.BR4,
                        //StopLoss = PreviousRange.BR4 + Math.Round((PreviousRange.FV3 - PreviousRange.BR4) * 0.33, 2),
                        TakeProfit = CurrentRange.MBCE1,
                        TradeMissedThreshold = CurrentRange.BR3,
                        BreakEvenThreshold = CurrentRange.BR2,
                    };

                    CurrentTrade.OneR_Threshold = CurrentTrade.Entry + (CurrentTrade.Entry - CurrentTrade.StopLoss) * 1;
                }

                if (tradeBias == TradeBias.Short)
                {
                    CurrentTrade = new TradeDetails()
                    {
                        Bias = TradeBias.Short,
                        Atr = atrCurrent,
                        Entry = CurrentRange.OBCE1 - TickSize,
                        StopLoss = CurrentRange.BR1,
                        //StopLoss = CurrentRange.BR1 - Math.Round((CurrentRange.BR1 - CurrentRange.FV2) * 0.33, 2),
                        TakeProfit = PreviousRange.MBCE2,
                        TradeMissedThreshold = PreviousRange.BR2,
                        BreakEvenThreshold = PreviousRange.BR3,
                    };

                    CurrentTrade.OneR_Threshold = CurrentTrade.Entry - (CurrentTrade.StopLoss - CurrentTrade.Entry) * 1;
                }
            }

            CurrentTrade.Entry = Math.Round(CurrentTrade.Entry / 0.25) * 0.25;

            CalculateVWAPDistance();

            if (CurrentTrade.Bias == TradeBias.Long)
            {
                if (atrCurrent >= 19)
                {
                    CurrentTrade.StopLoss = CurrentTrade.Entry - Math.Round(atrCurrent) - StopGrace;
                    var atrAdjustment = atrCurrent - 19;

                    if (atrCurrent < 30)
                        CurrentTrade.Entry = CurrentTrade.Entry - (atrAdjustment * 0.15);
                }

                if (Close[0] > CurrentTrade.Entry)
                {
                    //if (Time[0].Hour == 6)
                    //{
                    //    PrintDebug($"[LIMIT_ORDER_SKIPPED_VWAP] {CurrentTrade.VWAPDistance}");
                    //    return;
                    //}

                    PrintDebug($"[PLACE_TRADE_LIMIT] {CurrentTrade.Bias}, {CurrentTrade.Entry}, atr {atrCurrent}");
                    CurrentTrade.OrderType = OrderType.Limit;
                    PendingOrder = EnterLongLimit(0, true, CalculatePositionSize(), CurrentTrade.Entry, "long entry");
                }
                else
                {
                    if (CurrentTrade.VWAPDistance < 20)
                    {
                        PrintDebug($"[STOP_ORDER_SKIPPED_VWAP] {CurrentTrade.VWAPDistance}");
                        return;
                    }

                    PrintDebug($"[PLACE_TRADE_STOP_MARKET] {CurrentTrade.Bias} at {CurrentTrade.Entry}, {Close[0]}, atr {atrCurrent}");
                    CurrentTrade.OrderType = OrderType.StopMarket;
                    PendingOrder = EnterLongStopMarket(0, true, CalculatePositionSize(), CurrentTrade.Entry, "long entry");
                }

                SetStopLoss("long entry", CalculationMode.Price, CurrentTrade.StopLoss, true);
                PrintDebug($"[STOP_LOSS] {CurrentTrade.StopLoss}");
                SetProfitTarget("long entry", CalculationMode.Price, CurrentTrade.TakeProfit, true);

                return;
            }

            if (CurrentTrade.Bias == TradeBias.Short)
            {
                if (atrCurrent >= 19)
                {
                    CurrentTrade.StopLoss = CurrentTrade.Entry + Math.Round(atrCurrent) + StopGrace;
                    var atrAdjustment = atrCurrent - 19;

                    if (atrCurrent < 30)
                        CurrentTrade.Entry = CurrentTrade.Entry + (atrAdjustment * 0.15);
                }

                if (Close[0] < CurrentTrade.Entry)
                {
                    //if (Time[0].Hour == 6)
                    //{
                    //    PrintDebug($"[LIMIT_ORDER_SKIPPED_VWAP] {CurrentTrade.VWAPDistance}");
                    //    return;
                    //}

                    PrintDebug($"[PLACE_TRADE_LIMIT] {CurrentTrade.Bias}, {CurrentTrade.Entry}, atr {atrCurrent}");
                    CurrentTrade.OrderType = OrderType.Limit;
                    PendingOrder = EnterShortLimit(0, true, CalculatePositionSize(), CurrentTrade.Entry, "short entry");
                }
                else
                {
                    if (CurrentTrade.VWAPDistance < 20)
                    {
                        PrintDebug($"[STOP_ORDER_SKIPPED_VWAP] {CurrentTrade.VWAPDistance}");
                        return;
                    }

                    PrintDebug($"[PLACE_TRADE_STOP_MARKET] {CurrentTrade.Bias} at {CurrentTrade.Entry}, {Close[0]}, atr {atrCurrent}");
                    CurrentTrade.OrderType = OrderType.StopMarket;
                    PendingOrder = EnterShortStopMarket(0, true, CalculatePositionSize(), CurrentTrade.Entry, "short entry");
                }

                SetStopLoss("short entry", CalculationMode.Price, CurrentTrade.StopLoss, true);
                PrintDebug($"[STOP_LOSS] {CurrentTrade.StopLoss}");
                SetProfitTarget("short entry", CalculationMode.Price, CurrentTrade.TakeProfit, true);

                return;
            }
        }

        private void CalculateVWAPDistance()
        {
            if (CurrentTrade.Entry > vwap.VWAP[0])
            {
                CurrentTrade.VWAPLocation = "Above";
                CurrentTrade.VWAPDistance = Math.Round(CurrentTrade.Entry - vwap.VWAP[0]);
            }
            else
            {
                CurrentTrade.VWAPLocation = "Below";
                CurrentTrade.VWAPDistance = Math.Round(vwap.VWAP[0] - CurrentTrade.Entry);
            }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (marketPosition != MarketPosition.Flat)
                return;

            if (SystemPerformance.AllTrades.Count == 0)
                return;

            var trade = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
            var winLoss = trade.ProfitCurrency >= 0 ? "Win" : "Loss";
            PrintTrade($"{trade.Entry.Time},{CurrentTrade.Bias},{CurrentTrade.OrderType},{winLoss},{trade.ProfitCurrency},{trade.Quantity},{CurrentTrade.VWAPDistance},{CurrentTrade.VWAPLocation}");

            if (trade.ProfitCurrency >= 0)
                ProcessSharedBorderRange(false);
            else
                ProcessSharedBorderRange(true);

            rangeBreakDetected = false;

            PendingOrder = null;
        }

        private void DetectMSS()
        {
            //PrintDebug($"{tradeBias} {lastFractalLow} {lastFractalHigh} {Close[0]}");
            if (tradeBias == TradeBias.Short && Close[0] > LastFractalHigh && LastFractalHigh != 0)
            {
                mssBias = MSSBias.Bullish;
                PrintDebug($"[BULLISH_MSS]");
            }

            if (tradeBias == TradeBias.Long && Close[0] < LastFractalLow && LastFractalLow != 0)
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

                        tradeBias = TradeBias.Long;

                        if (mssBias == MSSBias.Neutral) mssBias = MSSBias.Bullish;

                        lastBOSLocation = LastFractalHigh;

                        PrintDebug($"[BULLISH_BOS] MSS bias set to {mssBias} BOS price {lastBOSLocation}");
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

                        PrintDebug($"[BEARISH_BOS] MSS bias set to {mssBias} BOS price {lastBOSLocation}");
                    }
                }
            }
        }

        private void PerformCleanupBeforeSessionStart()
        {
            PrintDebug("[SESSION_START] resetting state");

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
            rangeBias = RangeBias.Neutral;
            tradeBias = TradeBias.Neutral;

            BullishBOSDetected = false;
            BearishBOSDetected = false;
            lastBOSLocation = 0;

            dailyAtr ??= new DailyAtr();

            CurrentTrade = null;
        }

        private void BreakEvenOrCloseTrade()
        {
            var marketOpen = IsDSTOverlap() ? 13 : 14;

            if (Time[0].Hour == marketOpen && Time[0].Minute == 29)
            {
                PrintDebug($"[CLOSE_TRADE] market open, PnL {Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0])}");

                if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("short entry");

                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("long entry");
            }
            else
            {
                if (CurrentTrade.StopLoss == Position.AveragePrice)
                    return;

                var breakEvenDirection = "";

                if (CurrentTrade.Bias != tradeBias)
                {
                    if (CurrentTrade.Bias == TradeBias.Long && High[0] >= CurrentTrade.BreakEvenThreshold)
                    {
                        PrintDebug($"[BREAK_EVEN_BIAS] changed from {CurrentTrade.Bias} to {tradeBias}, {Position.AveragePrice}");
                        breakEvenDirection = "long entry";
                    }

                    if (CurrentTrade.Bias == TradeBias.Short && Low[0] <= CurrentTrade.BreakEvenThreshold)
                    {
                        breakEvenDirection = "short entry";
                        PrintDebug($"[BREAK_EVEN_BIAS] changed from {CurrentTrade.Bias} to {tradeBias}, {Position.AveragePrice}");
                    }
                }
                else
                {
                    //if (!CurrentTrade.StopLossAdjusted_OneR)
                    //{
                    //    if (CurrentTrade.Bias == TradeBias.Long && High[0] >= CurrentTrade.OneR_Threshold)
                    //    {
                    //        CurrentTrade.StopLoss = CurrentTrade.StopLoss + StopGrace;
                    //        CurrentTrade.StopLossAdjusted_OneR = true;

                    //        SetStopLoss("long entry", CalculationMode.Price, CurrentTrade.StopLoss, true);
                    //        PrintDebug($"[BREAK_EVEN_ONE_R_LONG] {CurrentTrade.StopLoss}");
                    //    }

                    //    if (CurrentTrade.Bias == TradeBias.Short && Low[0] <= CurrentTrade.OneR_Threshold)
                    //    {
                    //        CurrentTrade.StopLoss = CurrentTrade.StopLoss - StopGrace;
                    //        CurrentTrade.StopLossAdjusted_OneR = true;

                    //        SetStopLoss("short entry", CalculationMode.Price, CurrentTrade.StopLoss, true);
                    //        PrintDebug($"[BREAK_EVEN_ONE_R_SHORT]");
                    //    }
                    //}

                    if (CurrentTrade.Bias == TradeBias.Long && High[0] >= CurrentTrade.BreakEvenThreshold)
                    {
                        breakEvenDirection = "long entry";
                        PrintDebug($"[BREAK_EVEN_TP] threshold reached");
                    }

                    if (CurrentTrade.Bias == TradeBias.Short && Low[0] <= CurrentTrade.BreakEvenThreshold)
                    {
                        breakEvenDirection = "short entry";
                        PrintDebug($"[BREAK_EVEN_TP] threshold reached");
                    }
                }

                if (!string.IsNullOrEmpty(breakEvenDirection))
                {
                    SetStopLoss(breakEvenDirection, CalculationMode.Price, Position.AveragePrice, true);
                    CurrentTrade.StopLoss = Position.AveragePrice;
                    PrintDebug($"[BREAK_EVEN] set {Position.AveragePrice}");
                }
            }
        }

        private int CalculatePositionSize()
        {
            int positionSize = PositionSize;

            if (EdgeHoursParsed.Contains(Time[0].Hour) && atrCurrent <= 20)
            {
                positionSize = (int)(PositionSize * 1.5);
                PrintDebug($"[POSITION_ADJUSTED_EDGEHOUR] {positionSize}");
            }

            if (atrCurrent > 20 && atrCurrent <= 25)
            {
                positionSize = PositionSize + 1;
                PrintDebug($"[POSITION_ADJUSTED_ATR] {positionSize}");
            }

            if (atrCurrent > 25 && atrCurrent <= 36)
            {
                positionSize = PositionSize;
                PrintDebug($"[POSITION_ADJUSTED_ATR] {positionSize}");
            }

            if (atrCurrent > 36)
            {
                positionSize = PositionSize - 1;
                PrintDebug($"[POSITION_ADJUSTED_ATR] {positionSize}");
            }

            return positionSize;
        }

        private void ProcessSharedBorderRange(bool isLoss)
        {
            var current = Ranges.FirstOrDefault(r => r.IsInRange(CurrentTrade.StopLoss));
            var previous = Ranges.FirstOrDefault(r => r.IsInRange(CurrentTrade.TakeProfit));
            if (isLoss == false)
            {
                current = Ranges.FirstOrDefault(r => r.IsInRange(CurrentTrade.TakeProfit));
                previous = Ranges.FirstOrDefault(r => r.IsInRange(CurrentTrade.StopLoss));
            }

            var currentSharedBorderRange = GetSharedBorderRange(current, isLoss, rangeBias, CurrentTrade.Bias);
            if (SharedBorderTrade != null && SharedBorderTrade.Border == currentSharedBorderRange)
            {
                SharedBorderTrade.Trades++;

                PrintDebug($"[RANGE_INVALID] Shared border range {SharedBorderTrade.Border} trades incremented to {SharedBorderTrade.Trades}");
                if (SharedBorderTrade.Trades >= 2)
                    CreateInvalidRange(current, previous, isLoss, CurrentTrade.Bias);
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
            if (PendingOrder == null)
                return;

            CancelOrder(PendingOrder);

            PendingOrder = null;
            CurrentTrade = null;

            PrintDebug("[CANCEL_LIMIT_ORDER]");
        }
    }
}