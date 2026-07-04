using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public class AlgoPO3_Asia_v7_3 : AlgoPO3Base
    {
        [NinjaScriptProperty]
        [Display(Name = "Position Size", Order = 20, GroupName = "Parameters")]
        public int PositionSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stop Grace", Order = 21, GroupName = "Parameters")]
        public int StopGrace { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session End Time", Order = 22, GroupName = "Parameters")]
        public int SessionEndTime { get; set; }

        private List<DealingRange> Ranges { get; set; }

        private DealingRange CurrentRange { get; set; }
        private DealingRange PreviousRange { get; set; }
        private DateTime? lastInitialization;

        private RangeBias rangeBias;
        private TradeBias tradeBias;

        private bool rangeBreakDetected = false;

        private DailyAtr dailyAtr;
        private double atrCurrent;
        private bool isConnected;

        private FractalManager fractalManager;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Algo PO3 Asia v7.3";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                IsExitOnSessionCloseStrategy = true;
                ConnectionLossHandling = ConnectionLossHandling.KeepRunning;

                PO3 = 81;
                PositionSize = 5;
                IsOverlay = true;

                EdgeHourMultiplier = 1.5;
                SkipMonths = "8";
                StopGrace = 10;

                SessionEndTime = 12; // Tokyo Time
            }
            else if (State == State.Configure)
            {
                AddDataSeries("MNQ 09-26", BarsPeriodType.Minute, 5);
                AddDataSeries(BarsPeriodType.Tick, 1);

                Ranges = [];

                Events = NewsEvents.Events();

                tradeBias = TradeBias.Neutral;
                rangeBias = RangeBias.Neutral;

                TokyoZone = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
                LondonZone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
                NewYorkZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }
            else if (State == State.DataLoaded)
            {
                fractalManager = new FractalManager(this);

                VWAP = OrderFlowVWAP(VWAPResolution.Standard, Bars.TradingHours, VWAPStandardDeviations.One, 1, 2, 3);
                OrderFlowCVD = OrderFlowCumulativeDelta(CumulativeDeltaType.BidAsk, CumulativeDeltaPeriod.Session, 1);

                DeltaSeries = new Series<long>(this);
            }
            else if (State == State.Terminated)
            {
                if (ExportTradeLogsEnabled)
                    ExportTradeLogs(
                        "EntryTime,Bias,OrderType,Result,Pnl,Quantity,BarVolume,BarVolumeDelta,CvdSlopeFast,CvdRSquaredFast,CvdSlopeSlow,CvdRSquaredSlow",
                        $@"C:\Users\fassa\Documents\NinjaTrader Testing\TradeLog_AlgoPO3v7Asia_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
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

            // Always process fractal and BOS detection.
            if (BarsInProgress == 1)
            {
                dailyAtr.Calculate(High[0], Low[0], Close[0]);

                fractalManager.DetectFractalThreeBarWithPlatos(5);

                DetectBOS();
            }

            if (BarsInProgress == 2 && IsFirstTickOfBar)
            {
                // Save completed bar
                PreviousBarDelta = BuyVolume - SellVolume;

                //Print(string.Format(
                //    "{0}  Delta={1}  Buy={2}  Sell={3}",
                //    Time[1],
                //    PreviousBarDelta,
                //    BuyVolume,
                //    SellVolume));

                // Reset for new bar
                BuyVolume = 0;
                SellVolume = 0;
            }

            // Update the CVD
            if (BarsInProgress == 2)
            {
                OrderFlowCVD.Update(OrderFlowCVD.BarsArray[1].Count - 1, 1);
                return;
            }

            if (TradingHalted) return;

            if (SkipMonths.Split(',').Contains(Time[0].Month.ToString()))
            {
                TradingHalted = true;
                return;
            }

            // Skip all day events (easter etc).
            var eventForToday = GetNewsEventForToday();
            if (eventForToday != null &&
                eventForToday.EventTime.Date == Time[0].Date &&
                eventForToday.AllDay && Position.MarketPosition == MarketPosition.Flat)
            {
                TradingHalted = true;
                PrintInfo($"Trading skipped for day due to all day event {eventForToday.EventTime}");
                return;
            }

            // Stop trading when the last candle is disproportionally big.
            if (CandleBodyRange(0) > 300)
            {
                TradingHalted = true;
                PrintInfo($"Trading halted as last candle was {CandleBodyRange(0)} points.");
                return;
            }

            if (BarsInProgress != 0)
                return;

            // If the session has ended stop processing.
            if (TokyoTime().Hour >= SessionEndTime &&
                Position.MarketPosition == MarketPosition.Flat)
            {
                CancelLimitOrder();
                PrintInfo($"Trading session ended.");
                TradingHalted = true;
                return;
            }

            atrCurrent = dailyAtr.Atr;

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
            if (TokyoTime().TimeOfDay == new TimeSpan(9, 0, 0))
                rangeBreakDetected = false;

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

            //if (DetectAndCheckInvalidRange())
            //    return;

            // Cancel limit orders close to end of sessions blocks.
            // TODO: Test this properly with Python.
            //if (TokyoTime().Hour == 11 && Time[0].Minute >= 45)
            //{
            //    CancelLimitOrder();
            //    return;
            //}

            if (!rangeBreakDetected)
                return;

            if (CurrentRange.Low == PreviousRange.High)
            {
                rangeBias = RangeBias.Bullish;
                tradeBias = TradeBias.Long;
                PrintDebug($"Range bias {rangeBias}: Trade bias {tradeBias}");
            }

            if (CurrentRange.High == PreviousRange.Low)
            {
                rangeBias = RangeBias.Bearish;
                tradeBias = TradeBias.Short;
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

            if (tradeBias == TradeBias.Long)
            {
                CurrentTrade = new TradeDetails()
                {
                    Bias = TradeBias.Long,
                    Atr = atrCurrent,
                    StopLoss = CurrentRange.ExtLow,
                    TakeProfit = CurrentRange.Eq,
                    TradeMissedThreshold = CurrentRange.Eq,
                    BreakEvenThreshold = CurrentRange.BR2,
                };
            }

            if (tradeBias == TradeBias.Short)
            {
                CurrentTrade = new TradeDetails()
                {
                    Bias = TradeBias.Short,
                    Atr = atrCurrent,
                    StopLoss = CurrentRange.ExtHigh,
                    TakeProfit = CurrentRange.Eq,
                    TradeMissedThreshold = CurrentRange.Eq,
                    BreakEvenThreshold = CurrentRange.BR3,
                };
            }

            /***** TRADE INSPECTION BEFORE PLACEMENT *****/

            // If the entry is below/above the trade missed threshold then skip.
            if (CurrentTrade.Bias == TradeBias.Short && Close[0] < CurrentTrade.TradeMissedThreshold)
            {
                PrintDebug($"[TRADE_SKIPPED] {CurrentTrade.Bias}, entry {CurrentTrade.Entry} below threshold {CurrentTrade.TradeMissedThreshold}");
                rangeBreakDetected = false;
                return;
            }

            if (CurrentTrade.Bias == TradeBias.Long && Close[0] > CurrentTrade.TradeMissedThreshold)
            {
                PrintDebug($"[TRADE_SKIPPED] {CurrentTrade.Bias}, entry {CurrentTrade.Entry} above threshold {CurrentTrade.TradeMissedThreshold}");
                rangeBreakDetected = false;
                return;
            }

            /*---------------------------------------------*/

            CalculateVWAPDistance();

            CurrentTrade.DeltaMomentum = CalculateDeltaMomentum();

            var regressionResult = CalculateRegression(OrderFlowCVD.DeltaClose, 5);
            CurrentTrade.CvdSlopeFast = regressionResult.Slope;
            CurrentTrade.CvdRSquaredFast = regressionResult.RSquared;

            regressionResult = CalculateRegression(OrderFlowCVD.DeltaClose, 10);
            CurrentTrade.CvdSlopeSlow = regressionResult.Slope;
            CurrentTrade.CvdRSquaredSlow = regressionResult.RSquared;
            CurrentTrade.CvdAcceleration = CurrentTrade.CvdSlopeSlow - CurrentTrade.CvdRSquaredFast;

            CurrentTrade.BarVolume = Volume[0];
            CurrentTrade.BarVolumeDelta = PreviousBarDelta;

            if (CurrentTrade.Bias == TradeBias.Long)
            {
                EnterLong(0, CalculatePositionSize(), "long entry");
                PrintDebug($"[PLACE_TRADE_MARKET] {CurrentTrade.Bias}, {CurrentTrade.Entry}, atr {atrCurrent}");

                SetStopLoss("long entry", CalculationMode.Price, CurrentTrade.StopLoss, true);
                PrintDebug($"[STOP_LOSS] {CurrentTrade.StopLoss}");
                SetProfitTarget("long entry", CalculationMode.Price, CurrentTrade.TakeProfit);

                return;
            }

            if (CurrentTrade.Bias == TradeBias.Short)
            {
                EnterShort(0, CalculatePositionSize(), "short entry");
                PrintDebug($"[PLACE_TRADE_MARKET] {CurrentTrade.Bias}, {CurrentTrade.Entry}, atr {atrCurrent}");

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

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (CurrentTrade.Entry == 0)
                CurrentTrade.Entry = position.AveragePrice;

            if (marketPosition != MarketPosition.Flat)
                return;

            if (SystemPerformance.AllTrades.Count == 0)
                return;

            var trade = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
            var winLoss = trade.ProfitCurrency >= 0 ? "Win" : "Loss";
            var tradeCSV = $"{trade.Entry.Time},{CurrentTrade.Bias},{CurrentTrade.OrderType},{winLoss},{trade.ProfitCurrency},{trade.Quantity},{CurrentTrade.BarVolume},{CurrentTrade.BarVolumeDelta},{CurrentTrade.CvdSlopeFast},{CurrentTrade.CvdRSquaredFast},{CurrentTrade.CvdSlopeSlow},{CurrentTrade.CvdRSquaredSlow}";
            TradeLogs.Add(tradeCSV);

            if (trade.ProfitCurrency >= 0)
                ProcessSharedBorderRange(false);
            else
                ProcessSharedBorderRange(true);

            rangeBreakDetected = false;

            CurrentTrade.PendingOrder = null;
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            switch (e.MarketDataType)
            {
                case MarketDataType.Bid:
                    CurrentBid = e.Price;
                    break;

                case MarketDataType.Ask:
                    CurrentAsk = e.Price;
                    break;

                case MarketDataType.Last:

                    if (double.IsNaN(CurrentBid) || double.IsNaN(CurrentAsk))
                        return;

                    if (e.Price >= CurrentAsk)
                    {
                        BuyVolume += e.Volume;
                    }
                    else if (e.Price <= CurrentBid)
                    {
                        SellVolume += e.Volume;
                    }

                    break;
            }
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

                    //tradeBias = TradeBias.Long;

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

                    //tradeBias = TradeBias.Short;

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
            if (NewYorkTime().Hour == 14 && Time[0].Minute == 29)
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

            // TODO: Test this with Python
            var validEdgeHour = TokyoTime().Hour == 10;
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
