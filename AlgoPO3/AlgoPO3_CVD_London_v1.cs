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
    public class AlgoPO3_CVD_London_v1 : AlgoPO3Base
    {
        [NinjaScriptProperty]
        [Display(Name = "Risk", Order = 20, GroupName = "Parameters")]
        public int Risk { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session Start Hour", Order = 21, GroupName = "Parameters")]
        public int SessionStartHour { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session End Hour", Order = 22, GroupName = "Parameters")]
        public int SessionEndHour { get; set; }

        private bool sessionStarted;

        private bool rangeBreakDetected = false;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "London CVD v1.2";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                IsExitOnSessionCloseStrategy = true;
                ConnectionLossHandling = ConnectionLossHandling.KeepRunning;

                PO3 = 81;
                Risk = 300;

                SkipMonths = "0";

                DailyMaxLoss = 0;
                DailyMaxProfit = 1000;

                LongEdgeMultiplier = 1;
                ShortEdgeMultiplier = 1;

                // London Time
                SessionStartHour = 8;
                SessionEndHour = 10;

                PrintTo = PrintTo.OutputTab2;
            }
            else if (State == State.Configure)
            {
                Ranges = [];

                Events = NewsEvents.Events();

                LondonZone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

                if (ImportTickAndCvdEnabled)
                {
                    DataSetValidation = new Dictionary<int, bool> { { 0, false } };
                }
                else
                {
                    AddDataSeries(BarsPeriodType.Tick, 1);
                    DataSetValidation = new Dictionary<int, bool> { { 0, false }, { 1, false } };
                }
            }
            else if (State == State.DataLoaded)
            {
                VWAP = OrderFlowVWAP(VWAPResolution.Standard, Bars.TradingHours, VWAPStandardDeviations.One, 1, 2, 3);

                OnDataLoadedBase("London_CVD");
            }
            else if (State == State.Terminated)
            {
                OnTerminatedBase("London_CVD");
            }
        }

        protected override void OnBarUpdate()
        {
            //try
            //{
            if (BarsInProgress == 0 && Bars.IsFirstBarOfSession)
            {
                PerformCleanupBeforeSessionStart();

                DataSetValidation[0] = false;

                if (!ImportTickAndCvdEnabled)
                    DataSetValidation[1] = false;
            }

            if (TradingHalted)
                return;

            if (BarsInProgress == 0 && !DataSetValidation[0])
            {
                DataSetValidation[0] = true;
                PrintInfo("[DATASET_VALIDATION] Primary series validated.");
            }

            if (!ImportTickAndCvdEnabled && BarsInProgress == 1 && !DataSetValidation[1])
            {
                DataSetValidation[1] = true;
                PrintInfo("[DATASET_VALIDATION] Secondary series validated.");
            }

            if (!DataSetValidation.All(v => v.Value))
                return;

            /* ----- SESSION INITIALIZATION ----- */
            if (LondonTime().TimeOfDay < new TimeSpan(7, 0, 0))
                return;

            if (!CheckDailyPnlLimits())
                return;

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
            if (!CheckCandleVolatility())
                return;

            // If the session has ended stop processing.
            if (sessionStarted && LondonTime().TimeOfDay >= new TimeSpan(SessionEndHour, 0, 0) &&
                (Position.MarketPosition == MarketPosition.Flat || (CurrentTrade != null && !CurrentTrade.Virtual)))
            {
                PrintInfo($"Trading session ended.");
                TradingHalted = true;
                return;
            }

            // If a BIP 1 exists that means the tick series was added so process them.
            if (BarsInProgress == 1)
            {
                if (ExportTickAndCVDFlag)
                    TickDataSeriesExport.Add((Time[0].Ticks, Close[0]));

                OrderFlowCVDIndicator.Update(OrderFlowCVDIndicator.BarsArray[1].Count - 1, 1);

                ProcessVirtualTrade();

                return;
            }

            // Only trade on the primary series (1min)
            if (BarsInProgress != 0)
                return;

            // Process fractals on the 1min
            FractalManager.DetectFractalThreeBar(1);
            FractalManager.DetectFractalFiveBar(1);

            if (!ImportTickAndCvdEnabled && OrderFlowCVDIndicator.CurrentBar >= 0)
                CVDSeries[0] = OrderFlowCVDIndicator.DeltaClose[0];

            if (ExportTickAndCVDFlag)
            {
                CvdDeltaCloseExport.Add((Time[0].ToString(), OrderFlowCVDIndicator.DeltaClose[0]));
                CvdEmaExport.Add((Time[0].ToString(), CVDEmaIndicator[0]));
            }

            /* 
             * ----- STRATEGY START TIME ANCHORED TO LONDON-----
             * 
             * At the open we reset the range break.
            */

            if (LondonTime().TimeOfDay < new TimeSpan(SessionStartHour, 0, 0))
                return;

            if (LondonTime().TimeOfDay == new TimeSpan(SessionStartHour, 0, 0))
            {
                PrintInfo($"[SESSION_START]");
                sessionStarted = true;
            }

            if (!sessionStarted) return;

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
            if (CurrentRange.Low == PreviousRange.High && RangeBias != RangeBias.Bullish)
            {
                RangeBias = RangeBias.Bullish;
                TradeBias = TradeBias.Long;
                PrintDebug($"Range bias {RangeBias}: Trade bias {TradeBias}");
            }

            if (CurrentRange.High == PreviousRange.Low && RangeBias != RangeBias.Bearish)
            {
                RangeBias = RangeBias.Bearish;
                TradeBias = TradeBias.Short;
                PrintDebug($"Range bias {RangeBias}: Trade bias {TradeBias}");
            }

            /* ----- WE ARE IN A POSITION  ----- */
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            /* ----- WE ARE IN A VIRTUAL POSITION  ----- */
            if (CurrentTrade != null && CurrentTrade.Virtual && CurrentTrade.VirtualOpen)
            {
                ProcessVirtualTrade();
                return;
            }

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

            CreateCurrentTradeCVD();

            /* ----- COLLECT ANY STATISTICS ----- */
            CollectStatistics();

            CalculateVWAPDistance();

            CurrentTrade.CVD_TradeType = CVD_TradeType;

            /* ----- TRADE INSPECTION BEFORE PLACEMENT ----- */

            if (CurrentTrade.Bias == TradeBias.Long)
            {
                if (CurrentTrade.CVD_EMA > CurrentTrade.CVD)
                {
                    PrintDebug($"[CVD_BIAS_FAIL_LONG] EMA {Math.Round(CurrentTrade.CVD_EMA)} > CVD {CurrentTrade.CVD}");
                    return;
                }

                //var CVD_EMA_DistanceCheck =
                //    (CurrentTrade.CVD_EMA_Distance >= 50 && CurrentTrade.CVD_EMA_Distance < 150) ||
                //    (CurrentTrade.CVD_EMA_Distance >= 200 && CurrentTrade.CVD_EMA_Distance < 500) ||
                //    (CurrentTrade.CVD_EMA_Distance >= 600 && CurrentTrade.CVD_EMA_Distance < 900);

                //if (!CVD_EMA_DistanceCheck)
                //{
                //    PrintDebug($"[CVD_DISTANCE_FAIL_LONG] EMA DISTANCE {CurrentTrade.CVD_EMA_Distance}");
                //    CurrentTrade.Entry = Close[0];

                //    CurrentTrade.Virtual = true;
                //    CurrentTrade.VirtualOpen = true;

                //    if (ImportTickAndCvdEnabled)
                //    {
                //        CurrentTrade.VirtualTickIndex = FindTickIndex(Time[0].Ticks);
                //    }

                //}
            }

            if (CurrentTrade.Bias == TradeBias.Short)
            {
                if (CurrentTrade.CVD_EMA < CurrentTrade.CVD)
                {
                    PrintDebug($"[CVD_BIAS_FAIL_SHORT] EMA {Math.Round(CurrentTrade.CVD_EMA)} < CVD {CurrentTrade.CVD}");
                    return;
                }

                //var CVD_EMA_DistanceCheck =
                //    (CurrentTrade.CVD_EMA_Distance >= 0 && CurrentTrade.CVD_EMA_Distance < 100) ||
                //    (CurrentTrade.CVD_EMA_Distance >= 150 && CurrentTrade.CVD_EMA_Distance < 300) ||
                //    (CurrentTrade.CVD_EMA_Distance >= 500 && CurrentTrade.CVD_EMA_Distance < 600) ||
                //    (CurrentTrade.CVD_EMA_Distance >= 700 && CurrentTrade.CVD_EMA_Distance < 800);

                //if (!CVD_EMA_DistanceCheck)
                //{
                //    PrintDebug($"[CVD_DISTANCE_FAIL_SHORT] EMA DISTANCE {CurrentTrade.CVD_EMA_Distance}");
                //    CurrentTrade.Entry = Close[0];

                //    CurrentTrade.Virtual = true;
                //    CurrentTrade.VirtualOpen = true;

                //    if (ImportTickAndCvdEnabled)
                //    {
                //        CurrentTrade.VirtualTickIndex = FindTickIndex(Time[0].Ticks);
                //    }

                //}
            }

            /* ----- PLACE THE TRADE ----- */
            CurrentTrade.PositionSize = CalculatePositionSizeWithEdge(Math.Abs(Close[0] - CurrentTrade.StopLoss), Risk);

            if (CurrentTrade.Virtual)
            {
                PrintInfo($"[VIRTUAL_POSITION] {CurrentTrade.Bias} {CurrentTrade.OrderType} submittted at {CurrentTrade.Entry}, SL {CurrentTrade.StopLoss}, TP {CurrentTrade.TakeProfit}");
                return;
            }

            if (CurrentTrade.Bias == TradeBias.Long)
            {
                var rr = CalculateRiskReward(Close[0] - CurrentTrade.StopLoss, CurrentTrade.TakeProfit - Close[0]);
                if (rr < 0.5)
                {
                    PrintDebug($"[RISK_REWARD_BELOW_THRESHOLD_LONG] {rr}");
                    rangeBreakDetected = false;
                    CurrentTrade.Virtual = false;
                    return;
                }

                EnterLong(0, CurrentTrade.PositionSize, LONG_TRADE);
                PrintDebug($"[PLACE_TRADE_MARKET] {CurrentTrade.Bias} stop {CurrentTrade.StopLoss}");

                SetStopLoss(LONG_TRADE, CalculationMode.Price, CurrentTrade.StopLoss, false);
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
                    CurrentTrade.Virtual = false;
                    return;
                }

                EnterShort(0, CurrentTrade.PositionSize, SHORT_TRADE);
                PrintDebug($"[PLACE_TRADE_MARKET] {CurrentTrade.Bias} stop {CurrentTrade.StopLoss}");

                SetStopLoss(SHORT_TRADE, CalculationMode.Price, CurrentTrade.StopLoss, false);
                SetProfitTarget(SHORT_TRADE, CalculationMode.Price, CurrentTrade.TakeProfit);

                return;
            }
            //}
            //catch (Exception ex) { Print($"Error in OnBarUpdate: {ex}"); }
        }

        private int CalculatePositionSizeWithEdge(double stopDistance, int risk)
        {
            var positionSize = CalculatePositionSize(stopDistance, risk);

            // TODO

            return positionSize;
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

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if ((execution.Order.Name == LONG_TRADE || execution.Order.Name == SHORT_TRADE) &&
                execution.Order.OrderState == OrderState.Filled && Position.MarketPosition != MarketPosition.Flat)
            {
                CurrentTrade.EntryTime = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0);
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
            var tradeCSV = $"{CurrentTrade.EntryTime},{CurrentTrade.Bias},{CurrentTrade.OrderType},{winLoss},{trade.ProfitCurrency},{trade.Quantity},{CurrentTrade.CVD},{CurrentTrade.CVD_EMA},{CurrentTrade.CVD_EMA_Alignment},{CurrentTrade.CVD_TradeType},{CurrentTrade.VWAPDistance}";
            TradeCSVs.Add(tradeCSV);

            PrintTrade(tradeCSV);

            UpdateCVDTradeType(trade.ProfitCurrency > 0);
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
            DayPnLVirtual = 0;
            LastTradeCount = 0;

            CVD_TradeType = CVD_TradeType_Enum.Normal;

            sessionStarted = false;
        }
    }
}
