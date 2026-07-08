using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.BarsTypes;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public class AlgoPO3_CVD_Asia_v1 : AlgoPO3Base
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

        private EMA cvdEma;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Algo PO3 CVD Asia v1";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                IsExitOnSessionCloseStrategy = true;
                ConnectionLossHandling = ConnectionLossHandling.KeepRunning;
                IsOverlay = true;

                PO3 = 81;
                PositionSize = 5;
                IsOverlay = true;

                EdgeHourMultiplier = 1.5;
                SkipMonths = "8";
                StopGrace = 10;

                // Tokyo Time
                SessionEndTime = 12;
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
                FractalManager = new FractalManager(this);

                OrderFlowCVD = OrderFlowCumulativeDelta(CumulativeDeltaType.BidAsk, CumulativeDeltaPeriod.Session, 1);

                CVDSeries = new Series<double>(this);
                CvdSlopeSeries = new Series<double>(this);

                cvdEma = EMA(CVDSeries, 20);
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

            if (BarsInProgress == 0)
            {
                FractalManager.DetectFractalThreeBar(1);
                FractalManager.DetectFractalFiveBar(1);
            }

            // Always process fractal and BOS detection.
            if (BarsInProgress == 1)
            {
                dailyAtr.Calculate(High[0], Low[0], Close[0]);

                FractalManager.DetectFractalThreeBarWithPlatos(5);
                tradeBias = DetectBOS(5) ?? tradeBias;
            }

            if (TradingHalted) return;

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
            HaltTradingIfCandleBodyMassive();

            // If the session has ended stop processing.
            if (TokyoTime().Hour >= SessionEndTime &&
                Position.MarketPosition == MarketPosition.Flat)
            {
                CancelLimitOrder();
                PrintInfo($"Trading session ended.");
                TradingHalted = true;
                return;
            }

            // Update the CVD
            if (BarsInProgress == 2)
            {
                OrderFlowCVD.Update(OrderFlowCVD.BarsArray[1].Count - 1, 1);
                return;
            }

            // Only trade on the primary series (1min)
            if (BarsInProgress != 0)
                return;

            if (OrderFlowCVD.CurrentBar >= 0)
                CVDSeries[0] = OrderFlowCVD.DeltaClose[0];

            if (Bars.BarsSeries.BarsType is VolumetricBarsType barsType)
            {
                var volBar = barsType.Volumes[CurrentBar];
                PreviousBarDelta = volBar.BarDelta;
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
                    // Update Current Range's coverage as a candle could've open and closed into a new range.
                    CurrentRange.UpdateCoverage(Close[0]);

                    PreviousRange = CurrentRange;

                    CurrentRange = found;

                    rangeBreakDetected = true;

                    PrintDebug($"[RANGE_BREAK] new range {CurrentRange}");
                }
            }

            CurrentRange.UpdateCoverage(Close[0]);

            /* ----- STRATEGY START TIME ANCHORED TO TOKYO-----*/
            if (TokyoTime().TimeOfDay < new TimeSpan(9, 0, 0))
                return;

            if (TokyoTime().TimeOfDay == new TimeSpan(9, 0, 0))
                rangeBreakDetected = false;

            // If we are in a position then break even or close and stop processing.
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                BreakEvenTrade();
                return;
            }

            /*
             ### From here we are flat or waiting on a pending order to execute ###
            */

            if (DetectAndCheckInvalidRange())
                return;

            // Cancel limit orders close to end of sessions blocks.
            if (TokyoTime().Hour == 11 && Time[0].Minute >= 45)
            {
                CancelLimitOrder();
                return;
            }

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

            if (rangeBias == RangeBias.Bearish && tradeBias == TradeBias.Short)
            {
                CurrentTrade = new TradeDetails()
                {
                    Bias = TradeBias.Short,
                    Atr = atrCurrent,
                    StopLoss = CalculateStopLoss(),
                    TakeProfit = CurrentRange.Eq,
                    BreakEvenThreshold = CurrentRange.BR3,
                };

                var rangePosition = CurrentRange.GetPositionInRange(rangeBias, Close[0]);
                if (rangePosition == DealingRangePosition.Premium)
                {
                    CurrentTrade.StopLoss = CurrentRange.High;
                    CurrentTrade.TakeProfit = CurrentRange.Low;
                    CurrentTrade.BreakEvenThreshold = CurrentRange.OBCE1;
                    CurrentTrade.IsRangeContinuationTrade = true;
                }
            }

            if (rangeBias == RangeBias.Bullish && tradeBias == TradeBias.Long)
            {
                CurrentTrade = new TradeDetails()
                {
                    Bias = TradeBias.Long,
                    Atr = atrCurrent,
                    StopLoss = CalculateStopLoss(),
                    TakeProfit = CurrentRange.Eq,
                    BreakEvenThreshold = CurrentRange.BR2,
                };

                var rangePosition = CurrentRange.GetPositionInRange(rangeBias, Close[0]);
                if (rangePosition == DealingRangePosition.Premium)
                {
                    CurrentTrade.StopLoss = CurrentRange.Low;
                    CurrentTrade.TakeProfit = CurrentRange.High;
                    CurrentTrade.BreakEvenThreshold = CurrentRange.OBCE2;
                    CurrentTrade.IsRangeContinuationTrade = true;
                }
            }

            /***** TRADE INSPECTION BEFORE PLACEMENT *****/

            if (CurrentTrade.Bias == TradeBias.Long && cvdEma[0] > OrderFlowCVD.DeltaClose[0])
            {
                PrintDebug($"[CVD_INSPECTION_FAIL] EMA {Math.Round(cvdEma[0])} > CVD {OrderFlowCVD.DeltaClose[0]}");
                return;
            }

            if (CurrentTrade.Bias == TradeBias.Short && cvdEma[0] < OrderFlowCVD.DeltaClose[0])
            {
                PrintDebug($"[CVD_INSPECTION_FAIL] EMA {Math.Round(cvdEma[0])} < CVD {OrderFlowCVD.DeltaClose[0]}");
                return;
            }

            /*---------------------------------------------*/

            CalculateStatistics();

            if (CurrentTrade.Bias == TradeBias.Long)
            {
                var rr = CalculateRiskReward(Close[0] - CurrentTrade.StopLoss, CurrentTrade.TakeProfit - Close[0]);
                if (rr < 0.5)
                {
                    PrintDebug($"[RISK_REWARD_BELOW_THRESHOLD_LONG] {rr}");
                    rangeBreakDetected = false;
                    return;
                }

                EnterLong(0, CalculatePositionSize(), LONG_TRADE);
                PrintDebug($"[PLACE_TRADE_MARKET] {CurrentTrade.Bias}, {CurrentTrade.Entry}, atr {atrCurrent}");

                SetStopLoss(LONG_TRADE, CalculationMode.Price, CurrentTrade.StopLoss, true);
                PrintDebug($"[STOP_LOSS] {CurrentTrade.StopLoss}");
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
                    return;
                }

                EnterShort(0, CalculatePositionSize(), SHORT_TRADE);
                PrintDebug($"[PLACE_TRADE_MARKET] {CurrentTrade.Bias}, {CurrentTrade.Entry}, atr {atrCurrent}");

                SetStopLoss(SHORT_TRADE, CalculationMode.Price, CurrentTrade.StopLoss, true);
                PrintDebug($"[STOP_LOSS] {CurrentTrade.StopLoss}");
                SetProfitTarget(SHORT_TRADE, CalculationMode.Price, CurrentTrade.TakeProfit);

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
            if (marketPosition != MarketPosition.Flat)
                return;

            if (SystemPerformance.AllTrades.Count == 0)
                return;

            var trade = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
            var winLoss = trade.ProfitCurrency >= 0 ? "Win" : "Loss";
            var tradeCSV = $"{trade.Entry.Time},{CurrentTrade.Bias},{CurrentTrade.OrderType},{winLoss},{trade.ProfitCurrency},{trade.Quantity},{CurrentTrade.BarVolume},{CurrentTrade.BarVolumeDelta},{CurrentTrade.CvdSlopeFast},{CurrentTrade.CvdRSquaredFast},{CurrentTrade.CvdSlopeSlow},{CurrentTrade.CvdRSquaredSlow}";
            PrintTrade(tradeCSV);
            TradeLogs.Add(tradeCSV);

            //if (trade.ProfitCurrency >= 0)
            //    ProcessSharedBorderRange(false);
            //else
            //    ProcessSharedBorderRange(true);

            if (CurrentTrade.IsRangeContinuationTrade)
                rangeBreakDetected = false;
        }

        private double CalculateStopLoss()
        {
            var candidates = new List<double>();

            if (tradeBias == TradeBias.Short)
            {
                //FractalManager.Fractals.TryGetValue((1, FractalType.HighThreeBar), out var threeBarFractal);
                if (FractalManager.Fractals.TryGetValue((1, FractalType.HighFiveBar), out var fiveBarFractal))
                {
                    if (fiveBarFractal.Price > CurrentRange.ExtHigh)
                        return fiveBarFractal.Price;
                }

                return CurrentRange.ExtHigh;
            }

            if (tradeBias == TradeBias.Long)
            {
                if (FractalManager.Fractals.TryGetValue((1, FractalType.LowThreeBar), out var threeBarFractal))
                    if (threeBarFractal.Price < CurrentRange.ExtLow) candidates.Add(threeBarFractal.Price);

                if (FractalManager.Fractals.TryGetValue((1, FractalType.LowFiveBar), out var fiveBarFractal))
                    if (fiveBarFractal.Price < CurrentRange.ExtLow) candidates.Add(fiveBarFractal.Price);

                if (Open[0] < CurrentRange.ExtLow)
                    candidates.Add(Open[0]);

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

            FractalManager.Fractals.Clear();

            CurrentTrade = null;

            TradingHalted = false;
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
