using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public class AlgoPO3_CVD_NewYork_v1 : AlgoPO3Base
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

        internal override List<(double Min, double Max)> LongBuckets
        {
            get
            {
                return [
                    (0, 10),
                    (30, 50),
                    (110, 170),
                    (180, 190),
                    (210, 230),
                    (300, 350),
                ];
            }
        }

        internal override List<(double Min, double Max)> LongEdgeBuckets
        {
            get
            {
                return
                [
                    (30, 40),
                ];
            }
        }

        internal override List<(double Min, double Max)> ShortBuckets
        {
            get
            {
                return
                [
                    (10, 20),
                    (60, 70),
                    (90, 110),
                    (120, 160),
                    (170, 180),
                    (190, 210),
                    (240, 325),
                    (375, 450),
                ];
            }
        }

        internal override List<(double Min, double Max)> ShortEdgeBuckets
        {
            get
            {
                return
                [
                    (60, 70),
                    (190, 210),
                ];
            }
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "CVD NY v1.4 (MBT)";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                IsExitOnSessionCloseStrategy = true;
                ConnectionLossHandling = ConnectionLossHandling.KeepRunning;

                PO3 = 729;
                Risk = 250;

                DailyMaxLoss = -600;
                DailyMaxProfit = 1200;

                LongEdgeMultiplier = 1.25;
                ShortEdgeMultiplier = 1.25;

                CVDEMADistanceFilteringEnabled = true;

                // New York Time
                SessionStartHour = 9;
                SessionEndHour = 11;

                PrintTo = PrintTo.OutputTab2;
            }
            else if (State == State.Configure)
            {
                Ranges = [];

                Events = NewsEvents.Events();

                NewYorkZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

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
                OnDataLoadedBase("CVD_NY");
            }
            else if (State == State.Terminated)
            {
                OnTerminatedBase("CVD_NY");
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
            if (NewYorkTime().TimeOfDay < new TimeSpan(8, 0, 0))
                return;

            if (!CheckDailyPnlLimits())
                return;

            // Skip all day events (easter etc).
            var eventForToday = GetNewsEventForToday();
            if (eventForToday != null &&
                eventForToday.EventTime.Date == Time[0].Date &&
                eventForToday.AllDay &&
                Position.MarketPosition == MarketPosition.Flat)
            {
                HaltTrading($"[TRADING_HALTED] Trading skipped for day due to all day event {eventForToday.EventTime}");
                return;
            }

            // TODO check this
            // Stop trading when the last candle is disproportionally big.
            //if (!CheckCandleVolatility())
            //    return;

            // If the session has ended stop processing.
            if (sessionStarted &&
                NewYorkTime().Hour >= SessionEndHour
                &&
                (CurrentTrade == null ||
                 (!CurrentTrade.Virtual && Position.MarketPosition == MarketPosition.Flat) ||
                 (CurrentTrade.Virtual && !CurrentTrade.VirtualOpen)))
            {
                HaltTrading("[TRADING_HALTED] Session ended.");
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
             * ----- STRATEGY START TIME ANCHORED TO New York-----
             * 
             * At the open we reset the range break.
            */

            if (NewYorkTime().TimeOfDay < new TimeSpan(SessionStartHour, 30, 0))
                return;

            if (NewYorkTime().TimeOfDay == new TimeSpan(SessionStartHour, 30, 0))
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

            if (CurrentTrade.StopLoss == 0)
            {
                PrintDebug($"[STOP_INVALID]");

                CurrentTrade.Virtual = false;
                CurrentTrade.VirtualOpen = false;
                CurrentTrade.VirtualTickIndex = 0;

                return;
            }

            /* ----- COLLECT ANY STATISTICS ----- */
            CollectStatistics();

            CurrentTrade.CVD_TradeType = CVD_TradeType;

            /* ----- TRADE INSPECTION BEFORE PLACEMENT ----- */

            if (CurrentTrade.Bias == TradeBias.Long)
            {
                if (CurrentTrade.CVD_EMA > CurrentTrade.CVD)
                {
                    PrintDebug($"[CVD_BIAS_FAIL_LONG] EMA {Math.Round(CurrentTrade.CVD_EMA)} > CVD {CurrentTrade.CVD}");
                    return;
                }

                if (CVDEMADistanceFilteringEnabled)
                {
                    if (!IsInBucket(CurrentTrade.CVD_EMA_Distance, LongBuckets))
                    {
                        PrintDebug($"[CVD_DISTANCE_FAIL_LONG] EMA DISTANCE {CurrentTrade.CVD_EMA_Distance}");
                        CurrentTrade.Virtual = true;
                        CurrentTrade.VirtualOpen = true;

                        if (ImportTickAndCvdEnabled)
                        {
                            CurrentTrade.VirtualTickIndex = FindTickIndex(Time[0].Ticks);
                        }
                    }
                }
            }

            if (CurrentTrade.Bias == TradeBias.Short)
            {
                if (CurrentTrade.CVD_EMA < CurrentTrade.CVD)
                {
                    PrintDebug($"[CVD_BIAS_FAIL_SHORT] EMA {Math.Round(CurrentTrade.CVD_EMA)} < CVD {CurrentTrade.CVD}");
                    return;
                }

                if (CVDEMADistanceFilteringEnabled)
                {
                    if (!IsInBucket(CurrentTrade.CVD_EMA_Distance, ShortBuckets))
                    {
                        PrintDebug($"[CVD_DISTANCE_FAIL_SHORT] EMA DISTANCE {CurrentTrade.CVD_EMA_Distance}");
                        CurrentTrade.Virtual = true;
                        CurrentTrade.VirtualOpen = true;

                        if (ImportTickAndCvdEnabled)
                        {
                            CurrentTrade.VirtualTickIndex = FindTickIndex(Time[0].Ticks);
                        }
                    }
                }
            }

            /* ----- PLACE THE TRADE ----- */

            CurrentTrade.PositionSize = CalculatePositionSizeWithEdge(Math.Abs(Close[0] - CurrentTrade.StopLoss), Risk);

            var rr = CalculateRiskReward(Math.Abs(Close[0] - CurrentTrade.StopLoss), Math.Abs(CurrentTrade.TakeProfit - Close[0]));
            if (rr < 0.5)
            {
                PrintDebug($"[RISK_REWARD_BELOW_THRESHOLD] {CurrentTrade.Bias} {rr}");
                rangeBreakDetected = false;
                CurrentTrade.Virtual = false;
                return;
            }

            if (CurrentTrade.Virtual)
            {
                PrintInfo($"[VIRTUAL_POSITION] {CurrentTrade.Bias} triggered at {CurrentTrade.Entry}, SL {CurrentTrade.StopLoss}, TP {CurrentTrade.TakeProfit}");
                return;
            }

            if (CurrentTrade.Bias == TradeBias.Long)
            {
                EnterLong(0, CurrentTrade.PositionSize, LONG_TRADE);
                PrintDebug($"[PLACE_TRADE_MARKET] {CurrentTrade.Bias} {CurrentTrade.CVD_TradeType}, stop {CurrentTrade.StopLoss}");

                SetStopLoss(LONG_TRADE, CalculationMode.Price, CurrentTrade.StopLoss, false);
                SetProfitTarget(LONG_TRADE, CalculationMode.Price, CurrentTrade.TakeProfit);

                return;
            }

            if (CurrentTrade.Bias == TradeBias.Short)
            {
                EnterShort(0, CurrentTrade.PositionSize, SHORT_TRADE);
                PrintDebug($"[PLACE_TRADE_MARKET] {CurrentTrade.Bias} {CurrentTrade.CVD_TradeType}, stop {CurrentTrade.StopLoss}");

                SetStopLoss(SHORT_TRADE, CalculationMode.Price, CurrentTrade.StopLoss, false);
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

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            // Ignore anything that isn't a filled execution
            if (execution.Order.OrderState != OrderState.Filled)
                return;

            // Wait until the entire position is closed
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            var tradePnl = BasePnlUpdates();

            UpdateCVDTradeType(tradePnl > 0);
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
