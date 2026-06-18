using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public class AlgoPO3v7 : AlgoPO3Base
    {
        [NinjaScriptProperty]
        [Display(Name = "Stop Grace", Order = 21, GroupName = "Parameters")]
        public int StopGrace { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Clear Ranges Hour", Order = 22, GroupName = "Parameters")]
        public int ClearRangesHour { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Range Covered End Hour", Order = 23, GroupName = "Parameters")]
        public int RangeCoveredEndHour { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "New York End Time", Order = 24, GroupName = "Parameters")]
        public int NewYorkEndTime { get; set; }

        private List<DealingRange> Ranges { get; set; }

        private DealingRange CurrentRange { get; set; }
        private DealingRange PreviousRange { get; set; }

        private DateTime? lastInitialization;

        private RangeBias rangeBias;
        private TradeBias tradeBias;

        private bool rangeBreakDetected = false;

        private NewsFilter newsFilter;

        private DailyAtr dailyAtr;
        private double atrCurrent;
        private OrderFlowVWAP vwap;
        private bool isConnected;

        private FractalManager fractalManager;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "PO3 Algo v7.4";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                IsExitOnSessionCloseStrategy = true;
                ConnectionLossHandling = ConnectionLossHandling.KeepRunning;

                PO3 = 81;
                PositionSize = 4;
                IsOverlay = true;

                EdgeHourMultiplier = 1.5;
                SkipMonths = "8";
                StopGrace = 3;

                IncludeCPIWeek = true;
                IncludeFOMCWeek = false;
                IncludeNFPWeek = true;
                IncludeNoEventsWeek = true;

                ClearRangesHour = 7;
                RangeCoveredEndHour = 10;
                NewYorkEndTime = 8;
            }
            else if (State == State.Configure)
            {
                AddDataSeries("MNQ 06-26", BarsPeriodType.Minute, 5);

                Ranges = [];

                Events = NewsEvents.Events();

                tradeBias = TradeBias.Neutral;
                rangeBias = RangeBias.Neutral;

                newsFilter = new NewsFilter(IncludeCPIWeek, IncludeFOMCWeek, IncludeNFPWeek, IncludeNoEventsWeek);

                TokyoZone = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
                LondonZone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
                NewYorkZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }
            else if (State == State.DataLoaded)
            {
                fractalManager = new FractalManager(this);

                vwap = OrderFlowVWAP(
                        VWAPResolution.Standard,
                        Bars.TradingHours,
                        VWAPStandardDeviations.One,
                        1, 2, 3
                    );
            }
            else if (State == State.Terminated)
            {
                if (TradeLogs.Count > 0 && ExportTradeLogs)
                {
                    string path = $@"C:\Users\fassa\Documents\NinjaTrader Testing\TradeLog{DateTime.Now:yyyyMMdd_HHmmss}.csv";

                    using (StreamWriter sw = new(path))
                    {
                        sw.WriteLine("EntryTime,Bias,OrderType,Result,Pnl,Quantity,VWAPDistance,VWAPLocation");
                        foreach (string trade in TradeLogs)
                        {
                            sw.WriteLine(trade);
                        }
                    }

                    Print($"Trade log written to {path}");
                }
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

            if (TradingHalted) return;

            if (SkipMonths.Split(',').Contains(Time[0].Month.ToString()))
            {
                TradingHalted = true;
                return;
            }

            if (!Events.IsInWeek(newsFilter, Time[0]))
            {
                PrintInfo($"Skipping as NFP {newsFilter.IncludeNFP} CPI {newsFilter.IncludeCPI} FOMC {newsFilter.IncludeFOMC} NoEvents {newsFilter.IncludeNoEvents}...");
                TradingHalted = true;
                return;
            }

            // Skip all day events (easter etc).
            var newsForToday = GetNewsEventForToday();
            if (newsForToday != null && newsForToday.AllDay && Position.MarketPosition == MarketPosition.Flat)
            {
                TradingHalted = true;
                PrintInfo($"Trading skipped for day due to all day event.");
                return;
            }

            // Stop trading when the last candle is disproportionally big.
            if (CandleBodyRange(0) > 300)
            {
                TradingHalted = true;
                PrintInfo($"Trading halted as last candle was {CandleBodyRange(0)} points.");
                return;
            }

            // Always process fractal and BOS detection.
            if (BarsInProgress == 1)
            {
                dailyAtr.Calculate(High[0], Low[0], Close[0]);

                fractalManager.DetectFractalThreeBarWithPlatos(5);
                DetectBOS();
            }

            // If the session has ended stop processing.
            if (NewYorkTime().Hour >= NewYorkEndTime && NewYorkTime().Date == Time[0].Date &&
                Position.MarketPosition == MarketPosition.Flat)
            {
                CancelLimitOrder();
                PrintInfo($"Trading session ended.");
                TradingHalted = true;
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
                    // Update Current Range's coverage as a candle could ahve open and closed into a new range.
                    CurrentRange.UpdateCoverage(Close[0]);

                    PreviousRange = CurrentRange;

                    CurrentRange = found;

                    rangeBreakDetected = true;
                    PrintDebug($"[RANGE_BREAK] new range {CurrentRange}, Bias {tradeBias}");
                }
            }

            CurrentRange.UpdateCoverage(Close[0]);

            /* ----- STRATEGY START TIME ANCHORED TO TOKYO-----*/
            if (TokyoTime().TimeOfDay < new TimeSpan(9, 0, 0))
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

            // Make sure that range coverings are cleared at the london open.
            //if (LondonTime().TimeOfDay == new TimeSpan(ClearRangesHour, 0, 0))
            //{
            //    Ranges.ForEach(r =>
            //    {
            //        PrintDebug($"[RANGE_COVERED_CLEAR] {r}");
            //        r.ClearCovered();
            //    });
            //}

            if (DetectAndCheckInvalidRange())
                return;

            // Filter time of day and cancel if we are flat.
            var validSkipHour = LondonTime().Hour == 4 || LondonTime().Hour == 5 || LondonTime().Hour == 6 || LondonTime().Hour == 7 || LondonTime().Hour == 10;
            if (validSkipHour)
            {
                CancelLimitOrder();
                return;
            }

            // Cancel limit orders close to end of sessions blocks.
            // TODO: Should everything be LONDON hours?
            var validBlockEndHour = LondonTime().Hour == 3 || LondonTime().Hour == 9 || LondonTime().Hour == 12;
            if (validBlockEndHour && Time[0].Minute >= 40)
            {
                CancelLimitOrder();
                return;
            }

            if (!rangeBreakDetected)
                return;

            // If we set a limit order and price gets to TP we need to clear the limit order
            // Always return as we are waiting for the PendingOrder to fill or be cancelled.
            if (CurrentTrade?.PendingOrder != null)
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

            if (IsLive && !isConnected)
                return;

            //if (atrCurrent > 50)
            //{
            //    PrintDebug($"[EXCESSIVE_ATR] skipping trade, ATR {atrCurrent}");
            //    return;
            //}

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
                        TakeProfit = CurrentRange.MBCE1,
                        TradeMissedThreshold = CurrentRange.BR3,
                        BreakEvenThreshold = CurrentRange.BR2,
                    };
                }

                if (tradeBias == TradeBias.Short)
                {
                    CurrentTrade = new TradeDetails()
                    {
                        Bias = TradeBias.Short,
                        Atr = atrCurrent,
                        Entry = CurrentRange.OBCE1 - TickSize,
                        StopLoss = CurrentRange.BR1,
                        TakeProfit = PreviousRange.MBCE2,
                        TradeMissedThreshold = PreviousRange.BR2,
                        BreakEvenThreshold = PreviousRange.BR3,
                    };
                }
            }

            CurrentTrade.Entry = Math.Round(CurrentTrade.Entry / 0.25) * 0.25;

            /***** TRADE INSPECTION BEFORE PLACEMENT *****/

            // If the entry is below/above the trade missed threshold then skip.
            if (CurrentTrade.Bias == TradeBias.Short && Close[0] < CurrentTrade.TradeMissedThreshold)
            {
                PrintDebug($"[TRADE_SKIPPED] {CurrentTrade.Bias}, entry {CurrentTrade.Entry} below threshold {CurrentTrade.TradeMissedThreshold}");
                return;
            }

            if (CurrentTrade.Bias == TradeBias.Long && Close[0] > CurrentTrade.TradeMissedThreshold)
            {
                PrintDebug($"[TRADE_SKIPPED] {CurrentTrade.Bias}, entry {CurrentTrade.Entry} above threshold {CurrentTrade.TradeMissedThreshold}");
                return;
            }

            // The range that was potentially completed will be the range where the stop is in.
            //var entryRange = Ranges.FirstOrDefault(r => r.IsInRange(CurrentTrade.StopLoss));
            //if (entryRange != null &&
            //    entryRange.CoveredPercentage >= 95 &&
            //    LondonTime().Hour >= 8 && LondonTime().Hour < RangeCoveredEndHour)
            //{
            //    PrintDebug($"[RANGE_COVERED_SKIPPING] {CurrentTrade.OrderType} {CurrentTrade.Bias}, entry {CurrentTrade.Entry}, range {entryRange}");
            //    return;
            //}

            /*---------------------------------------------*/

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
                    PrintDebug($"[PLACE_TRADE_LIMIT] {CurrentTrade.Bias}, {CurrentTrade.Entry}, atr {atrCurrent}");
                    CurrentTrade.OrderType = OrderType.Limit;
                    CurrentTrade.PendingOrder = EnterLongLimit(0, true, CalculatePositionSize(), CurrentTrade.Entry, "long entry");
                }
                else
                {
                    if (CurrentTrade.VWAPDistance < 20)
                    {
                        PrintDebug($"[LONG_STOP_ORDER_SKIPPED_VWAP] at {CurrentTrade.Entry}, distance from VWAP {CurrentTrade.VWAPDistance}");
                        return;
                    }

                    PrintDebug($"[PLACE_TRADE_STOP_MARKET] {CurrentTrade.Bias} at {CurrentTrade.Entry}, {Close[0]}, atr {atrCurrent}");
                    CurrentTrade.OrderType = OrderType.StopMarket;
                    CurrentTrade.PendingOrder = EnterLongStopMarket(0, true, CalculatePositionSize(), CurrentTrade.Entry, "long entry");
                }

                SetStopLoss("long entry", CalculationMode.Price, CurrentTrade.StopLoss, true);
                PrintDebug($"[STOP_LOSS] {CurrentTrade.StopLoss}");
                SetProfitTarget("long entry", CalculationMode.Price, CurrentTrade.TakeProfit);

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
                    PrintDebug($"[PLACE_TRADE_LIMIT] {CurrentTrade.Bias}, {CurrentTrade.Entry}, atr {atrCurrent}");
                    CurrentTrade.OrderType = OrderType.Limit;
                    CurrentTrade.PendingOrder = EnterShortLimit(0, true, CalculatePositionSize(), CurrentTrade.Entry, "short entry");
                }
                else
                {
                    if (CurrentTrade.VWAPDistance < 20)
                    {
                        PrintDebug($"[SHORT_STOP_ORDER_SKIPPED_VWAP] at {CurrentTrade.Entry}, distance from VWAP {CurrentTrade.VWAPDistance}");
                        return;
                    }

                    PrintDebug($"[PLACE_TRADE_STOP_MARKET] {CurrentTrade.Bias} at {CurrentTrade.Entry}, {Close[0]}, atr {atrCurrent}");
                    CurrentTrade.OrderType = OrderType.StopMarket;
                    CurrentTrade.PendingOrder = EnterShortStopMarket(0, true, CalculatePositionSize(), CurrentTrade.Entry, "short entry");
                }

                SetStopLoss("short entry", CalculationMode.Price, CurrentTrade.StopLoss, true);
                PrintDebug($"[STOP_LOSS] {CurrentTrade.StopLoss}");
                SetProfitTarget("short entry", CalculationMode.Price, CurrentTrade.TakeProfit);

                return;
            }
        }

        protected override void OnConnectionStatusUpdate(ConnectionStatusEventArgs e)
        {
            if (e.Status == ConnectionStatus.Connected)
            {
                isConnected = true;
                Print("[CONNECTED]");
            }
            else
            {
                isConnected = false;
                Print("[DISCONNECTED]");
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
            TradeLogs.Add($"{trade.Entry.Time},{CurrentTrade.Bias},{CurrentTrade.OrderType},{winLoss},{trade.ProfitCurrency},{trade.Quantity},{CurrentTrade.VWAPDistance},{CurrentTrade.VWAPLocation}");
            PrintTrade($"{trade.Entry.Time},{CurrentTrade.Bias},{CurrentTrade.OrderType},{winLoss},{trade.ProfitCurrency},{trade.Quantity},{CurrentTrade.VWAPDistance},{CurrentTrade.VWAPLocation}");

            if (trade.ProfitCurrency >= 0)
            {
                ProcessSharedBorderRange(false);
            }
            else
            {
                ProcessSharedBorderRange(true);
            }

            rangeBreakDetected = false;

            CurrentTrade.PendingOrder = null;
        }

        private void DetectBOS()
        {
            //---Bullish BOS ---
            if (fractalManager.Fractals.TryGetValue((5, FractalType.HighThreeBar), out (double Price, DateTime Time) lastFractalHigh))
            {
                if (Close[0] > lastFractalHigh.Price)
                {
                    BearishBOSDetected = false;
                    BullishBOSDetected = true;

                    tradeBias = TradeBias.Long;

                    PrintDebug($"[BULLISH_BOS] trade bias {tradeBias}");
                }
            }

            // --- Bearish BOS ---
            if (fractalManager.Fractals.TryGetValue((5, FractalType.LowThreeBar), out (double Price, DateTime Time) lastFractalLow))
            {
                if (Close[0] < lastFractalLow.Price)
                {
                    BearishBOSDetected = true;
                    BullishBOSDetected = false;

                    tradeBias = TradeBias.Short;

                    PrintDebug($"[BEARISH_BOS] trade bias {tradeBias}");
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

            rangeBias = RangeBias.Neutral;
            tradeBias = TradeBias.Neutral;

            BullishBOSDetected = false;
            BearishBOSDetected = false;

            dailyAtr ??= new DailyAtr();

            fractalManager.Fractals.Clear();

            CurrentTrade = null;

            TradingHalted = false;
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

            // TODO: Should everything be LONDON hours?
            var validEdgeHour = LondonTime().Hour == 1 || LondonTime().Hour == 3 || LondonTime().Hour == 9 || LondonTime().Hour == 11;
            if (validEdgeHour && atrCurrent <= 20)
            {
                positionSize = (int)(PositionSize * EdgeHourMultiplier);
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

            // It is possible that we jumped ranges and as such this cannot be processed further.
            if (current == null || previous == null)
                return;

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
    }
}
