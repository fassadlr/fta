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
    public class AlgoPO3_CVD_Asia_v1 : AlgoPO3Base
    {
        [NinjaScriptProperty]
        [Display(Name = "Risk", Order = 20, GroupName = "Parameters")]
        public int Risk { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session End Hour", Order = 21, GroupName = "Parameters")]
        public int SessionEndHour { get; set; }

        private bool rangeBreakDetected = false;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Algo PO3 CVD Asia v1.1";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                IsExitOnSessionCloseStrategy = true;
                ConnectionLossHandling = ConnectionLossHandling.KeepRunning;
                IsOverlay = true;

                PO3 = 81;
                Risk = 400;

                SkipMonths = "8";

                DailyMaxLoss = 0;
                DailyMaxProfit = 1000;

                // Tokyo Time
                SessionEndHour = 12;

                PrintTo = PrintTo.OutputTab2;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Tick, 1);

                Ranges = [];

                Events = NewsEvents.Events();

                TokyoZone = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

                DataSetValidation = new Dictionary<int, bool> { { 0, false }, { 1, false } };
            }
            else if (State == State.DataLoaded)
            {
                FractalManager = new FractalManager(this);

                OrderFlowCVD = OrderFlowCumulativeDelta(CumulativeDeltaType.BidAsk, CumulativeDeltaPeriod.Session, 1);

                CVDSeries = new Series<double>(this);
                CvdSlopeSeries = new Series<double>(this);

                CVDEma = EMA(CVDSeries, 20);
            }
            else if (State == State.Terminated)
            {
                if (ExportTradeLogsEnabled)
                    ExportTradeLogs(
                        "EntryTime,Bias,OrderType,Result,Pnl,Quantity,CVD,CVD_EMA,CVD_TradeType",
                        $@"C:\Users\fassa\Documents\NinjaTrader Testing\AlgoPO3_CVD_Asia_v1_1_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            }
        }

        protected override void OnBarUpdate()
        {
            //try
            //{

            if (TradingHalted && BarsInProgress == 0 && Bars.IsFirstBarOfSession)
            {
                PerformCleanupBeforeSessionStart();

                DataSetValidation[0] = false;
                DataSetValidation[1] = false;
            }

            if (TradingHalted)
                return;

            if (BarsInProgress == 0 && !DataSetValidation[0])
            {
                DataSetValidation[0] = true;
                PrintInfo("[DATASET_VALIDATION] Primary series validated.");
            }

            if (BarsInProgress == 1 && !DataSetValidation[1])
            {
                DataSetValidation[1] = true;
                PrintInfo("[DATASET_VALIDATION] Secondary series validated.");
            }

            if (!DataSetValidation.All(v => v.Value))
                return;

            /* ----- SESSION INITIALIZATION ----- */
            if (TokyoTime().TimeOfDay < new TimeSpan(8, 0, 0))
                return;

            BasePnlChecks();

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
            BaseCandleVolatilityChecks();

            // If the session has ended stop processing.
            if (TokyoTime().Hour >= SessionEndHour && (Position.MarketPosition == MarketPosition.Flat || (CurrentTrade != null && !CurrentTrade.IsVirtual)))
            {
                PrintInfo($"Trading session ended.");
                TradingHalted = true;
                return;
            }

            // Update the CVD
            if (BarsInProgress == 1)
            {
                OrderFlowCVD.Update(OrderFlowCVD.BarsArray[1].Count - 1, 1);

                ProcessVirtualTrade();

                return;
            }

            // Only trade on the primary series (1min)
            if (BarsInProgress != 0)
                return;

            // Process fractals on the 1min
            FractalManager.DetectFractalThreeBar(1);
            FractalManager.DetectFractalFiveBar(1);

            if (OrderFlowCVD.CurrentBar >= 0)
                CVDSeries[0] = OrderFlowCVD.DeltaClose[0];

            /* 
             * ----- STRATEGY START TIME ANCHORED TO TOKYO-----
             * 
             * At the open we reset the range break.
            */

            if (TokyoTime().TimeOfDay < new TimeSpan(9, 0, 0))
                return;

            if (TokyoTime().TimeOfDay == new TimeSpan(9, 0, 0))
                PrintInfo($"[SESSION_START]");

            /* ----- PROCESS RANGE INFORMATION -----*/

            if (CurrentRange == null)
            {
                CurrentRange = CreateDealingRange();
                return;
            }

            var found = Ranges.FirstOrDefault(r => r.IsInRange(Close[0])) ?? CreateDealingRange();
            if (found != CurrentRange)
            {
                if (PreviousRange != CurrentRange)
                {
                    PreviousRange = CurrentRange;

                    CurrentRange = found;

                    rangeBreakDetected = true;

                    PrintDebug($"[RANGE_BREAK_DETECTED] {CurrentRange}");
                }
            }

            if (PreviousRange == null)
                return;

            /* ----- SET TRADE BIAS  ----- */
            if (CurrentRange.Low == PreviousRange.High)
            {
                RangeBias = RangeBias.Bullish;
                TradeBias = TradeBias.Long;
                PrintDebug($"Range bias {RangeBias}: Trade bias {TradeBias}");
            }

            if (CurrentRange.High == PreviousRange.Low)
            {
                RangeBias = RangeBias.Bearish;
                TradeBias = TradeBias.Short;
                PrintDebug($"Range bias {RangeBias}: Trade bias {TradeBias}");
            }

            /* ----- WE ARE IN A POSITION  ----- */
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            /* ----- WE ARE IN A VIRTUAL POSITION  ----- */
            if (CurrentTrade != null && CurrentTrade.IsVirtual)
                return;

            /* ----- WE ARE WAITING FOR AN ENTRY ----- */

            if (!rangeBreakDetected)
                return;

            // This still needs to be checked as ranges can be jumped with large candles.
            if (RangeBias == RangeBias.Neutral)
                return;

            if (IsLive && State != State.Realtime)
                return;

            if (IsLive && !IsConnected)
                return;

            if (CVD_TradeType == CVD_TradeType_Enum.Normal && CurrentRange.GetPositionInRange(RangeBias, Close[0]) == DealingRangePosition.Premium)
            {
                PrintDebug($"[TRADE_STATE_FAIL] skipping trade, not in discount.");
                rangeBreakDetected = false;
                return;
            }

            if (CVD_TradeType == CVD_TradeType_Enum.Continuation && CurrentRange.GetPositionInRange(RangeBias, Close[0]) == DealingRangePosition.Discount)
            {
                PrintDebug($"[TRADE_STATE_FAIL] skipping trade, not in premium.");
                return;
            }

            if (RangeBias == RangeBias.Bearish && TradeBias == TradeBias.Short)
            {
                CurrentTrade = new TradeDetails()
                {
                    Bias = TradeBias.Short,
                    StopLoss = CalculateStopLoss(),
                    TakeProfit = CurrentRange.Eq,
                    BreakEvenThreshold = CurrentRange.BR3,
                };

                var rangePosition = CurrentRange.GetPositionInRange(RangeBias, Close[0]);
                if (rangePosition == DealingRangePosition.Premium)
                {
                    CurrentTrade.StopLoss = CurrentRange.High;
                    CurrentTrade.TakeProfit = CurrentRange.Low;
                    CurrentTrade.BreakEvenThreshold = CurrentRange.OBCE1;
                }
            }

            if (RangeBias == RangeBias.Bullish && TradeBias == TradeBias.Long)
            {
                CurrentTrade = new TradeDetails()
                {
                    Bias = TradeBias.Long,
                    StopLoss = CalculateStopLoss(),
                    TakeProfit = CurrentRange.Eq,
                    BreakEvenThreshold = CurrentRange.BR2,
                };

                var rangePosition = CurrentRange.GetPositionInRange(RangeBias, Close[0]);
                if (rangePosition == DealingRangePosition.Premium)
                {
                    CurrentTrade.StopLoss = CurrentRange.Low;
                    CurrentTrade.TakeProfit = CurrentRange.High;
                    CurrentTrade.BreakEvenThreshold = CurrentRange.OBCE2;
                }
            }

            /* ----- TRADE INSPECTION BEFORE PLACEMENT ----- */

            if (CurrentTrade.Bias == TradeBias.Long)
            {
                if (CVDEma[0] > OrderFlowCVD.DeltaClose[0])
                {
                    PrintDebug($"[CVD_BIAS_FAIL_LONG] EMA {Math.Round(CVDEma[0])} > CVD {OrderFlowCVD.DeltaClose[0]}");
                    return;
                }

                var CVD_EMA_Distance = Math.Round(OrderFlowCVD.DeltaClose[0] - CVDEma[0]);
                var CVD_EMA_DistanceCheck =
                    (CVD_EMA_Distance >= 100 && CVD_EMA_Distance < 150) ||
                    (CVD_EMA_Distance >= 200 && CVD_EMA_Distance < 800) ||
                    (CVD_EMA_Distance >= 1400 && CVD_EMA_Distance < 1600);

                if (!CVD_EMA_DistanceCheck)
                {
                    PrintDebug($"[CVD_DISTANCE_FAIL_LONG] EMA DISTANCE {CVD_EMA_Distance}");
                    PrintInfo($"[VIRTUAL_POSITION_OPENED] {CurrentTrade.Bias}");
                    CurrentTrade.IsVirtual = true;
                    CurrentTrade.Entry = Close[0];
                }
            }

            if (CurrentTrade.Bias == TradeBias.Short)
            {
                if (CVDEma[0] < OrderFlowCVD.DeltaClose[0])
                {
                    PrintDebug($"[CVD_BIAS_FAIL_SHORT] EMA {Math.Round(CVDEma[0])} < CVD {OrderFlowCVD.DeltaClose[0]}");
                    return;
                }

                var CVD_EMA_Distance = Math.Round(CVDEma[0] - OrderFlowCVD.DeltaClose[0]);
                var CVD_EMA_DistanceCheck =
                    (CVD_EMA_Distance >= 100 & CVD_EMA_Distance < 600) ||
                    (CVD_EMA_Distance >= 800 & CVD_EMA_Distance < 1000) ||
                    (CVD_EMA_Distance >= 1200 & CVD_EMA_Distance < 1600);

                if (!CVD_EMA_DistanceCheck)
                {
                    PrintDebug($"[CVD_DISTANCE_FAIL_SHORT] EMA DISTANCE {CVD_EMA_Distance}");
                    PrintInfo($"[VIRTUAL_POSITION_OPENED] {CurrentTrade.Bias}");
                    CurrentTrade.IsVirtual = true;
                    CurrentTrade.Entry = Close[0];
                }
            }

            /* ----- COLLECT ANY STATISTICS ----- */
            CollectStatistics();

            CurrentTrade.CVD_TradeType = CVD_TradeType;

            /* ----- PLACE THE TRADE ----- */
            if (CurrentTrade.Bias == TradeBias.Long)
            {
                var rr = CalculateRiskReward(Close[0] - CurrentTrade.StopLoss, CurrentTrade.TakeProfit - Close[0]);
                if (rr < 0.5)
                {
                    PrintDebug($"[RISK_REWARD_BELOW_THRESHOLD_LONG] {rr}");
                    rangeBreakDetected = false;
                    CurrentTrade.IsVirtual = false;
                    return;
                }

                if (CurrentTrade.IsVirtual)
                    return;

                EnterLong(0, CalculatePositionSize(Close[0] - CurrentTrade.StopLoss, Risk), LONG_TRADE);
                PrintDebug($"[PLACE_TRADE_MARKET] {CurrentTrade.Bias} stop {CurrentTrade.StopLoss}");

                SetStopLoss(LONG_TRADE, CalculationMode.Price, CurrentTrade.StopLoss, true);
                SetProfitTarget(LONG_TRADE, CalculationMode.Price, CurrentTrade.TakeProfit);

                return;
            }

            if (CurrentTrade.Bias == TradeBias.Short)
            {
                var rr = CalculateRiskReward(CurrentTrade.StopLoss - Close[0], Close[0] - CurrentTrade.TakeProfit);
                if (rr < 0.5)
                {
                    PrintDebug($"[RISK_REWARD_BELOW_THRESHOLD_SHORT] {rr}");
                    rangeBreakDetected = false;
                    CurrentTrade.IsVirtual = false;
                    return;
                }

                if (CurrentTrade.IsVirtual)
                    return;

                EnterShort(0, CalculatePositionSize(CurrentTrade.StopLoss - Close[0], Risk), SHORT_TRADE);
                PrintDebug($"[PLACE_TRADE_MARKET] {CurrentTrade.Bias} stop {CurrentTrade.StopLoss}");

                SetStopLoss(SHORT_TRADE, CalculationMode.Price, CurrentTrade.StopLoss, true);
                SetProfitTarget(SHORT_TRADE, CalculationMode.Price, CurrentTrade.TakeProfit);

                return;
            }
            //}
            //catch (Exception ex) { Print($"Error in OnBarUpdate: {ex}"); }
        }

        protected override void OnConnectionStatusUpdate(ConnectionStatusEventArgs e)
        {
            if (e.Status == ConnectionStatus.Connected)
            {
                IsConnected = true;
                Print("[CONNECTED]");
            }
            else
            {
                IsConnected = false;
                Print("[DISCONNECTED]");
            }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice, int quantity, MarketPosition marketPosition)
        {
            if (marketPosition != MarketPosition.Flat)
                return;

            if (SystemPerformance.AllTrades.Count == 0)
                return;

            BasePnlUpdates();

            var trade = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
            var winLoss = trade.ProfitCurrency >= 0 ? "Win" : "Loss";
            var tradeCSV = $"{trade.Entry.Time},{CurrentTrade.Bias},{CurrentTrade.OrderType},{winLoss},{trade.ProfitCurrency},{trade.Quantity},{CurrentTrade.CVD},{CurrentTrade.CVD_EMA},{CurrentTrade.CVD_TradeType}";
            PrintTrade(tradeCSV);
            TradeLogs.Add(tradeCSV);

            UpdateCVDTradeType(trade.ProfitCurrency > 0);
        }

        private double CalculateStopLoss()
        {
            var candidates = new List<double>();

            if (TradeBias == TradeBias.Short)
            {
                if (FractalManager.Fractals.TryGetValue((1, FractalType.HighThreeBar), out var threeBarFractal))
                    if (threeBarFractal.Price > CurrentRange.ExtHigh) candidates.Add(threeBarFractal.Price);

                if (FractalManager.Fractals.TryGetValue((1, FractalType.HighFiveBar), out var fiveBarFractal))
                    if (fiveBarFractal.Price > CurrentRange.ExtHigh) candidates.Add(fiveBarFractal.Price);

                if (High[0] > Math.Round(CurrentRange.ExtHigh))
                    candidates.Add(High[0]);

                if (ImbalanceFormed(TradeBias.Short, out double imbalanceStart))
                    if (imbalanceStart > CurrentRange.ExtHigh) candidates.Add(imbalanceStart);

                // which one is the closest to the ext high 
                if (candidates.Any())
                    return candidates.Min();

                return CurrentRange.ExtLow;
            }

            if (TradeBias == TradeBias.Long)
            {
                if (FractalManager.Fractals.TryGetValue((1, FractalType.LowThreeBar), out var threeBarFractal))
                    if (threeBarFractal.Price < CurrentRange.ExtLow) candidates.Add(threeBarFractal.Price);

                if (FractalManager.Fractals.TryGetValue((1, FractalType.LowFiveBar), out var fiveBarFractal))
                    if (fiveBarFractal.Price < CurrentRange.ExtLow) candidates.Add(fiveBarFractal.Price);

                if (Low[0] < Math.Round(CurrentRange.ExtLow))
                    candidates.Add(Low[0]);

                if (ImbalanceFormed(TradeBias.Long, out double imbalanceStart))
                    if (imbalanceStart < CurrentRange.ExtLow) candidates.Add(imbalanceStart);

                // which one is the closest to the ext low
                if (candidates.Any())
                    return candidates.Max();

                return CurrentRange.ExtLow;
            }

            return 0;
        }

        private void PerformCleanupBeforeSessionStart()
        {
            PrintDebug("[SESSION_INIT]");

            Ranges.Clear();

            CancelLimitOrder();

            CurrentRange = null;
            PreviousRange = null;
            rangeBreakDetected = false;

            InvalidRange = null;
            SharedBorderTrade = null;

            RangeBias = RangeBias.Neutral;
            TradeBias = TradeBias.Neutral;

            BullishBOSDetected = false;
            BearishBOSDetected = false;

            FractalManager.Fractals.Clear();

            CurrentTrade = null;

            TradingHalted = false;

            DayPnL = 0;
            LastTradeCount = 0;

            CVD_TradeType = CVD_TradeType_Enum.Normal;
        }
    }
}
