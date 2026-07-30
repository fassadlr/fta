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
    public class AlgoPO3_Asia_v7_4 : AlgoPO3Base
    {
        [NinjaScriptProperty]
        [Display(Name = "Risk", Order = 20, GroupName = "Parameters")]
        public int Risk { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stop Grace", Order = 21, GroupName = "Parameters")]
        public int StopGrace { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session End Hour", Order = 22, GroupName = "Parameters")]
        public int SessionEndHour { get; set; }

        private bool rangeBreakDetected = false;

        private DailyAtr dailyAtr;
        private double atrCurrent;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Asia Alpha v7.4";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                IsExitOnSessionCloseStrategy = true;
                ConnectionLossHandling = ConnectionLossHandling.KeepRunning;

                PO3 = 81;
                Risk = 200;

                LongEdgeMultiplier = 1.35;
                ShortEdgeMultiplier = 1.35;
                SkipMonths = "0";
                StopGrace = 10;

                DailyMaxLoss = -600;
                DailyMaxProfit = 800;

                // Tokyo Time
                SessionEndHour = 12;

                PrintTo = PrintTo.OutputTab1;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 5);

                if (ImportTickAndCvdEnabled)
                {
                    DataSetValidation = new Dictionary<int, bool> { { 0, false }, { 1, false } };
                }
                else
                {
                    AddDataSeries(BarsPeriodType.Tick, 1);
                    DataSetValidation = new Dictionary<int, bool> { { 0, false }, { 1, false }, { 2, false } };
                }

                Ranges = [];

                Events = NewsEvents.Events();

                TokyoZone = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
                NewYorkZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }
            else if (State == State.DataLoaded)
            {
                VWAP = OrderFlowVWAP(VWAPResolution.Standard, Bars.TradingHours, VWAPStandardDeviations.One, 1, 2, 3);

                OnDataLoadedBase("Asia_Alpha");
            }
            else if (State == State.Terminated)
            {
                OnTerminatedBase("Asia_Alpha");
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
                DataSetValidation[1] = false;

                if (!ImportTickAndCvdEnabled)
                    DataSetValidation[2] = false;
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

            if (!ImportTickAndCvdEnabled && BarsInProgress == 2 && !DataSetValidation[2])
            {
                DataSetValidation[2] = true;
                PrintInfo("[DATASET_VALIDATION] Third series validated.");
            }

            if (!DataSetValidation.All(v => v.Value))
                return;

            if (!CheckDailyPnlLimits())
                return;

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
            if (!CheckCandleVolatility())
                return;

            // If the session has ended stop processing.
            if (TokyoTime().Hour >= SessionEndHour && (Position.MarketPosition == MarketPosition.Flat || (CurrentTrade != null && CurrentTrade.Virtual && !CurrentTrade.VirtualOpen)))
            {
                CancelLimitOrder();
                PrintInfo($"Trading session ended.");
                TradingHalted = true;
                return;
            }

            // Always process fractal and BOS detection.
            if (BarsInProgress == 1)
            {
                dailyAtr.Calculate(High[0], Low[0], Close[0]);

                FractalManager.DetectFractalThreeBarWithPlatos(5);

                TradeBias = DetectBOS(5) ?? TradeBias;
            }

            // If a BIP 2 exists that means the tick series was added so process them.
            if (BarsInProgress == 2)
            {
                if (ExportTickAndCVDFlag)
                    TickDataSeriesExport.Add((Time[0].Ticks, Close[0]));

                OrderFlowCVDIndicator.Update(OrderFlowCVDIndicator.BarsArray[1].Count - 1, 1);

                ProcessVirtualTrade();

                return;
            }

            if (BarsInProgress != 0)
                return;

            if (!ImportTickAndCvdEnabled && OrderFlowCVDIndicator.CurrentBar >= 0)
                CVDSeries[0] = OrderFlowCVDIndicator.DeltaClose[0];

            if (ExportTickAndCVDFlag)
            {
                CvdDeltaCloseExport.Add((Time[0].ToString(), OrderFlowCVDIndicator.DeltaClose[0]));
                CvdEmaExport.Add((Time[0].ToString(), CVDEmaIndicator[0]));
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
                    PrintDebug($"[RANGE_BREAK_DETECTED] {CurrentRange}");
                }
            }

            CurrentRange.UpdateCoverage(Close[0]);

            /* ----- STRATEGY START TIME ANCHORED TO TOKYO-----*/
            if (TokyoTime().TimeOfDay < new TimeSpan(9, 0, 0))
                return;

            /* ----- WE ARE IN A POSITION  ----- */
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            /* ----- WE ARE IN A VIRTUAL POSITION  ----- */
            if (CurrentTrade != null && CurrentTrade.Virtual && CurrentTrade.VirtualOpen)
            {
                ProcessVirtualTrade();
                return;
            }

            /*
             ### From here we are flat or waiting on a pending order to execute ###
            */

            if (DetectAndCheckInvalidRange())
                return;

            // Cancel limit orders close to end of sessions blocks.
            // TODO: Test this properly with Python.
            if (TokyoTime().Hour == 11 && Time[0].Minute >= 45)
            {
                CancelLimitOrder();
                return;
            }

            if (!rangeBreakDetected)
                return;

            // If we set a limit order and price gets to TP we need to clear the limit order
            // Always return as we are waiting for the PendingOrder to fill or be cancelled.
            //
            // If the pending order is virtual also do the same processing.
            if (CurrentTrade != null && (CurrentTrade.PendingOrder != null || CurrentTrade.Virtual))
            {
                var shouldCancel = false;
                if (CurrentTrade.Bias != TradeBias)
                {
                    shouldCancel = true;
                    PrintDebug($"[LIMIT_ORDER_CANCEL] Trade bias changed from {CurrentTrade.Bias} to {TradeBias}.");
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
                    RangeBias = RangeBias.Neutral;
                }

                // If we are on the 1min and we are importing tick and CVD data 
                // then we need to process the virtual trade here.
                if (ImportTickAndCvdEnabled)
                    ProcessVirtualTrade();

                return;
            }

            if (BullishBOSDetected && CurrentRange.Low == PreviousRange.High)
            {
                RangeBias = RangeBias.Bullish;
                PrintDebug($"Range bias {RangeBias}: Trade bias {TradeBias}");
            }

            if (BullishBOSDetected && CurrentRange.High == PreviousRange.Low)
            {
                RangeBias = RangeBias.Bearish;
                PrintDebug($"Range bias {RangeBias}: Trade bias {TradeBias}");
            }

            if (BearishBOSDetected && CurrentRange.High == PreviousRange.Low)
            {
                RangeBias = RangeBias.Bearish;
                PrintDebug($"Range bias {RangeBias}: Trade bias {TradeBias}");
            }

            if (BearishBOSDetected && CurrentRange.Low == PreviousRange.High)
            {
                RangeBias = RangeBias.Bullish;
                PrintDebug($"Range bias {RangeBias}: Trade bias {TradeBias}");
            }

            if (RangeBias == RangeBias.Neutral)
                return;

            if (IsLive && State != State.Realtime)
                return;

            if (IsLive && !IsConnected)
                return;

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

            CurrentTrade = new TradeDetails()
            {
                Atr = atrCurrent,
                Bias = TradeBias,
            };

            if (RangeBias == RangeBias.Bearish)
            {
                if (CurrentTrade.Bias == TradeBias.Long)
                {
                    CurrentTrade.Entry = CurrentRange.OBCE2 + TickSize;
                    CurrentTrade.StopLoss = atrCurrent >= 19 ? CurrentTrade.Entry - Math.Round(atrCurrent) : CurrentRange.BR4 - StopGrace;
                    CurrentTrade.TradeMissedThreshold = PreviousRange.BR3;
                    CurrentTrade.TakeProfit = PreviousRange.MBCE1;

                    CurrentTrade.OrderType = Close[0] < CurrentTrade.Entry ? OrderType.StopMarket : OrderType.Limit;
                }

                if (CurrentTrade.Bias == TradeBias.Short)
                {
                    CurrentTrade.Entry = CurrentRange.ExtensionHighCE - TickSize;
                    CurrentTrade.StopLoss = atrCurrent >= 19 ? CurrentTrade.Entry + Math.Round(atrCurrent) : PreviousRange.BR1 + StopGrace;
                    CurrentTrade.TradeMissedThreshold = CurrentRange.BR2;
                    CurrentTrade.TakeProfit = CurrentRange.MBCE2;

                    CurrentTrade.OrderType = Close[0] > CurrentTrade.Entry ? OrderType.StopMarket : OrderType.Limit;
                }
            }

            if (RangeBias == RangeBias.Bullish)
            {
                if (TradeBias == TradeBias.Long)
                {
                    CurrentTrade.Entry = CurrentRange.ExtensionLowCE + TickSize;
                    CurrentTrade.StopLoss = atrCurrent >= 19 ? CurrentTrade.Entry - Math.Round(atrCurrent) : PreviousRange.BR4 - StopGrace;
                    CurrentTrade.TradeMissedThreshold = CurrentRange.BR3;
                    CurrentTrade.TakeProfit = CurrentRange.MBCE1;

                    CurrentTrade.OrderType = Close[0] < CurrentTrade.Entry ? OrderType.StopMarket : OrderType.Limit;
                }

                if (TradeBias == TradeBias.Short)
                {
                    CurrentTrade.Entry = CurrentRange.OBCE1 - TickSize;
                    CurrentTrade.StopLoss = atrCurrent >= 19 ? CurrentTrade.Entry + Math.Round(atrCurrent) : CurrentRange.BR1 + StopGrace;
                    CurrentTrade.TradeMissedThreshold = PreviousRange.BR2;
                    CurrentTrade.TakeProfit = PreviousRange.MBCE2;

                    CurrentTrade.OrderType = Close[0] > CurrentTrade.Entry ? OrderType.StopMarket : OrderType.Limit;
                }
            }

            CurrentTrade.Entry = Math.Round(CurrentTrade.Entry / 0.25) * 0.25;

            /* ----- COLLECT ANY STATISTICS ----- */

            CalculateVWAPDistance();

            CollectStatistics();

            /* ---------------------------------- */

            /* ----- TRADE INSPECTION BEFORE PLACEMENT ----- */

            if (CurrentTrade.CVD_EMA == 0 || CurrentTrade.CVD == 0)
            {
                TradingHalted = true;
                PrintInfo($"[DATA_ERROR] cvdEma {CurrentTrade.CVD_EMA} | cvdDeltaClose {CurrentTrade.CVD}");
                return;
            }

            if (CurrentTrade.Bias == TradeBias.Long)
            {
                // If the entry is below/above the trade missed threshold then skip.
                if (Close[0] > CurrentTrade.TradeMissedThreshold)
                {
                    PrintDebug($"[TRADE_SKIPPED] {CurrentTrade.Bias}, entry {CurrentTrade.Entry} above threshold {CurrentTrade.TradeMissedThreshold}");
                    return;
                }

                if (CurrentTrade.CVD_EMA > CurrentTrade.CVD)
                {
                    PrintDebug($"[CVD_BIAS_FAIL_LONG] EMA {Math.Round(CurrentTrade.CVD_EMA)} > CVD {CurrentTrade.CVD}");
                    return;
                }

                var CVD_EMA_DistanceCheck = CurrentTrade.CVD_EMA_Distance < 1000;
                if (!CVD_EMA_DistanceCheck)
                {
                    PrintDebug($"[CVD_DISTANCE_FAIL_LONG] EMA DISTANCE {CurrentTrade.CVD_EMA_Distance}");
                    CurrentTrade.Virtual = true;

                    if (ImportTickAndCvdEnabled)
                    {
                        CurrentTrade.VirtualTickIndex = FindTickIndex(Time[0].Ticks);
                    }
                }
            }

            if (CurrentTrade.Bias == TradeBias.Short)
            {
                // If the entry is below/above the trade missed threshold then skip.
                if (Close[0] < CurrentTrade.TradeMissedThreshold)
                {
                    PrintDebug($"[TRADE_SKIPPED] {CurrentTrade.Bias}, entry {CurrentTrade.Entry} below threshold {CurrentTrade.TradeMissedThreshold}");
                    return;
                }

                var CVD_EMA_DistanceCheck =
                    (CurrentTrade.CVD_EMA_Distance < 1000) ||
                    (CurrentTrade.CVD_EMA_Distance >= 1200 & CurrentTrade.CVD_EMA_Distance < 1600) ||
                    (CurrentTrade.CVD_EMA_Distance >= 1800);

                if (!CVD_EMA_DistanceCheck)
                {
                    PrintDebug($"[CVD_DISTANCE_FAIL_SHORT] EMA DISTANCE {CurrentTrade.CVD_EMA_Distance}");
                    CurrentTrade.Virtual = true;

                    if (ImportTickAndCvdEnabled)
                    {
                        CurrentTrade.VirtualTickIndex = FindTickIndex(Time[0].Ticks);
                    }
                }
            }

            /* Bias independant */

            CalculateTakeProfitBasedOnMaxDailyProfit();

            CurrentTrade.PositionSize = CalculatePositionSize(Math.Abs(CurrentTrade.Entry - CurrentTrade.StopLoss), Risk);

            ApplyEdgeToPosition();

            if (Close[0] == CurrentTrade.Entry)
            {
                PrintDebug($"[TRADE_SKIPPED_AS_PRICE_EQ_TO_ENTRY] {CurrentTrade.Bias} at {CurrentTrade.Entry}");

                CurrentTrade.Virtual = false;
                rangeBreakDetected = false;

                return;
            }

            if (CurrentTrade.OrderType == OrderType.StopMarket && CurrentTrade.VWAPDistance < 20)
            {
                PrintDebug($"[STOP_MARKET_ORDER_SKIPPED_VWAP] {CurrentTrade.Bias} at {CurrentTrade.Entry}, distance {CurrentTrade.VWAPDistance}");
                CurrentTrade.Virtual = false;

                return;
            }

            /* ---------------- */

            if (CurrentTrade.Bias == TradeBias.Long)
            {
                if (atrCurrent < 5 || (atrCurrent >= 35 && atrCurrent < 40))
                {
                    PrintDebug($"[ATR_INVALID] {atrCurrent}");
                    CurrentTrade.Virtual = true;

                    if (ImportTickAndCvdEnabled)
                        CurrentTrade.VirtualTickIndex = FindTickIndex(Time[0].Ticks);
                }

                if (CurrentTrade.Virtual)
                {
                    PrintInfo($"[VIRTUAL_POSITION] {CurrentTrade.Bias} {CurrentTrade.OrderType} submittted at {CurrentTrade.Entry}, SL {CurrentTrade.StopLoss}, TP {CurrentTrade.TakeProfit}");

                    // Process trade immediately as it could have triggered within this minute.
                    ProcessVirtualTrade();

                    return;
                }

                if (Close[0] > CurrentTrade.Entry)
                {
                    PrintDebug($"[PLACE_TRADE_LIMIT] {CurrentTrade.Bias}, {CurrentTrade.Entry}, atr {atrCurrent}");
                    CurrentTrade.PendingOrder = EnterLongLimit(0, true, CurrentTrade.PositionSize, CurrentTrade.Entry, LONG_TRADE);
                }

                if (Close[0] < CurrentTrade.Entry)
                {
                    PrintDebug($"[PLACE_TRADE_STOP_MARKET] {CurrentTrade.Bias} at {CurrentTrade.Entry}, {Close[0]}, atr {atrCurrent}");
                    CurrentTrade.PendingOrder = EnterLongStopMarket(0, true, CurrentTrade.PositionSize, CurrentTrade.Entry, LONG_TRADE);
                }
            }

            if (CurrentTrade.Bias == TradeBias.Short)
            {
                if (CurrentTrade.Virtual)
                {
                    PrintInfo($"[VIRTUAL_POSITION] {CurrentTrade.Bias} {CurrentTrade.OrderType} submittted at {CurrentTrade.Entry}, SL {CurrentTrade.StopLoss}, TP {CurrentTrade.TakeProfit}");

                    // Process trade immediately as it could have triggered within this minute.
                    ProcessVirtualTrade();

                    return;
                }

                if (Close[0] < CurrentTrade.Entry)
                {
                    PrintDebug($"[PLACE_TRADE_LIMIT] {CurrentTrade.Bias}, {CurrentTrade.Entry}, atr {atrCurrent}");
                    CurrentTrade.PendingOrder = EnterShortLimit(0, true, CurrentTrade.PositionSize, CurrentTrade.Entry, SHORT_TRADE);
                }

                if (Close[0] > CurrentTrade.Entry)
                {
                    PrintDebug($"[PLACE_TRADE_STOP_MARKET] {CurrentTrade.Bias} at {CurrentTrade.Entry}, {Close[0]}, atr {atrCurrent}");
                    CurrentTrade.PendingOrder = EnterShortStopMarket(0, true, CurrentTrade.PositionSize, CurrentTrade.Entry, SHORT_TRADE);
                }
            }

            var signal = CurrentTrade.Bias == TradeBias.Short ? SHORT_TRADE : LONG_TRADE;
            SetStopLoss(signal, CalculationMode.Price, CurrentTrade.StopLoss, false);
            PrintDebug($"[STOP_LOSS] {CurrentTrade.StopLoss}");
            SetProfitTarget(signal, CalculationMode.Price, CurrentTrade.TakeProfit);
            //}
            //    catch (Exception ex)
            //    {
            //        Print(ex);
        }

        private void CalculateTakeProfitBasedOnMaxDailyProfit()
        {
            if (DayPnL <= 0 || DailyMaxProfit == 0)
            {
                PrintDebug($"[TAKE_PROFIT_SET] {CurrentTrade.Bias} target {CurrentTrade.TakeProfit}");
                return;
            }

            // Calculate the Pnl of the trade.
            var contractPointValue = Instrument.MasterInstrument.PointValue * CurrentTrade.PositionSize;

            var remainingPnl = DailyMaxProfit - DayPnL;
            var targetDistance = Math.Abs(CurrentTrade.TakeProfit - CurrentTrade.Entry);
            var targetPnl = Math.Round(targetDistance * contractPointValue);

            if (CurrentTrade.Bias == TradeBias.Long)
            {
                // If the target Pnl is less than the remaining daily max profit then set the take profit to the target.
                if (targetPnl <= remainingPnl)
                {
                    CurrentTrade.TakeProfit = CurrentTrade.TakeProfit;
                    PrintDebug($"[TAKE_PROFIT_SET] {CurrentTrade.Bias} target {CurrentTrade.TakeProfit} targetPnl {targetPnl} remainingPnl {remainingPnl}");
                }
                else
                {
                    // Calculate the take profit based on the remaining daily max profit.
                    var remainingDistance = Math.Round(remainingPnl / contractPointValue);
                    var newTarget = CurrentTrade.Entry + remainingDistance + 1;
                    PrintInfo($"[TAKE_PROFIT_ADJUSTED] {CurrentTrade.Bias} new target {newTarget} old target {CurrentTrade.TakeProfit} targetPnl {targetPnl} remainingPnl {remainingPnl}");
                    CurrentTrade.TakeProfit = newTarget;
                }
            }

            if (CurrentTrade.Bias == TradeBias.Short)
            {
                // If the target Pnl is less than the remaining daily max profit then set the take profit to the target.
                if (targetPnl <= remainingPnl)
                {
                    CurrentTrade.TakeProfit = CurrentTrade.TakeProfit;
                    PrintDebug($"[TAKE_PROFIT_ADJUST_TARGET] {CurrentTrade.Bias} target {CurrentTrade.TakeProfit} targetPnl {targetPnl} remainingPnl {remainingPnl}");
                }
                else
                {
                    // Calculate the take profit based on the remaining daily max profit.
                    var remainingDistance = Math.Round(remainingPnl / contractPointValue);
                    var newTarget = CurrentTrade.Entry - remainingDistance - 1;
                    PrintInfo($"[TAKE_PROFIT_ADJUSTED] {CurrentTrade.Bias} new target {newTarget} old target {CurrentTrade.TakeProfit} targetPnl {targetPnl} remainingPnl {remainingPnl}");
                    CurrentTrade.TakeProfit = newTarget;
                }
            }
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
            var tradeCSV = $"{trade.Entry.Time},{CurrentTrade.Bias},{CurrentTrade.OrderType},{winLoss},{trade.ProfitCurrency},{trade.Quantity},{CurrentTrade.CVD},{CurrentTrade.CVD_EMA},{CurrentTrade.CVD_EMA_Alignment},{CurrentTrade.Atr}";
            TradeLogs.Add(tradeCSV);

            PrintTrade(tradeCSV);

            if (trade.ProfitCurrency >= 0)
                ProcessSharedBorderRange(false);
            else
                ProcessSharedBorderRange(true);

            rangeBreakDetected = false;

            CurrentTrade.PendingOrder = null;
        }

        private void PerformCleanupBeforeSessionStart()
        {
            PrintInfo("[SESSION_INIT]");

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

            dailyAtr ??= new DailyAtr();

            FractalManager.Fractals.Clear();

            CurrentTrade = null;

            TradingHalted = false;

            DayPnL = 0;
            DayPnLVirtual = 0;
            LastTradeCount = 0;
        }

        private void ApplyEdgeToPosition()
        {
            if (atrCurrent >= 10 && atrCurrent < 15 || atrCurrent >= 20 && atrCurrent < 25)
            {
                CurrentTrade.PositionSize = (int)(CurrentTrade.PositionSize * LongEdgeMultiplier);
                PrintDebug($"[POSITION_ADJUSTED_EDGE] new position {CurrentTrade.PositionSize} atr {atrCurrent}");
            }
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

            var currentSharedBorderRange = GetSharedBorderRange(current, isLoss, RangeBias, CurrentTrade.Bias);
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

            if (RangeBias == RangeBias.Bearish && lastTradeBias == TradeBias.Short)
                InvalidRange = new InvalidRange(isLoss ? current.BR3 : previous.BR3, isLoss ? previous.BR2 : current.BR2);

            if (RangeBias == RangeBias.Bearish && lastTradeBias == TradeBias.Long)
                InvalidRange = new InvalidRange(isLoss ? previous.BR3 : current.BR3, isLoss ? current.BR2 : previous.BR2);

            if (RangeBias == RangeBias.Bullish && lastTradeBias == TradeBias.Long)
                InvalidRange = new InvalidRange(isLoss ? previous.BR3 : current.BR3, isLoss ? current.BR2 : previous.BR2);

            if (RangeBias == RangeBias.Bullish && lastTradeBias == TradeBias.Short)
                InvalidRange = new InvalidRange(isLoss ? current.BR3 : previous.BR3, isLoss ? previous.BR2 : current.BR2);

            PrintDebug($"[RANGE_INVALID_CREATED] Invalid range created {RangeBias} {lastTradeBias} {InvalidRange}");
        }
    }
}
