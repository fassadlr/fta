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
    public class AlgoPO3v7London : AlgoPO3Base
    {
        [NinjaScriptProperty]
        [Display(Name = "Risk", Order = 20, GroupName = "Parameters")]
        public int Risk { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stop Grace", Order = 21, GroupName = "Parameters")]
        public int StopGrace { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "New York End Time", Order = 22, GroupName = "Parameters")]
        public int NewYorkEndTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max Profit", Order = 23, GroupName = "Parameters")]
        public int MaxProfit { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Filter News Days", Order = 24, GroupName = "Parameters")]
        public bool FilterNewsDays { get; set; }

        private List<DealingRange> ranges;

        private DealingRange CurrentRange { get; set; }
        private DealingRange PreviousRange { get; set; }

        private DateTime? lastInitialization;

        private TradeBias tradeBias;

        private bool rangeBreakDetected = false;

        private NewsFilter newsFilter;

        private DailyAtr dailyAtr;
        private double atrCurrent;
        private bool isConnected;

        private FractalManager fractalManager;

        private StrategyRunner_PO3 runner;
        private int lastTradeCount;
        private double dayPnL;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "PO3 Algo London v7";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                IsExitOnSessionCloseStrategy = true;
                ConnectionLossHandling = ConnectionLossHandling.KeepRunning;

                PO3 = 81;
                Risk = 300;
                IsOverlay = true;

                EdgeHourMultiplier = 1.5;
                SkipMonths = "8";
                StopGrace = 3;

                IncludeCPIWeek = true;
                IncludeFOMCWeek = true;
                IncludeNFPWeek = true;
                IncludeNoEventsWeek = true;

                NewYorkEndTime = 8;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Tick, 1);

                ranges = [];

                Events = NewsEvents.Events();

                tradeBias = TradeBias.Neutral;

                newsFilter = new NewsFilter(IncludeCPIWeek, IncludeFOMCWeek, IncludeNFPWeek, IncludeNoEventsWeek);

                LondonZone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
                NewYorkZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

                SkipMonthsSet = [.. SkipMonths.Split(',').Select(x => int.Parse(x.Trim()))];
            }
            else if (State == State.DataLoaded)
            {
                fractalManager = new FractalManager(this);

                VWAP = OrderFlowVWAP(
                        VWAPResolution.Standard,
                        Bars.TradingHours,
                        VWAPStandardDeviations.One,
                        1, 2, 3
                    );

                OrderFlowCVD = OrderFlowCumulativeDelta(CumulativeDeltaType.BidAsk, CumulativeDeltaPeriod.Session, 1);
            }
            else if (State == State.Terminated)
            {
                if (ExportTradeLogsEnabled)
                    ExportTradeLogs(
                        "EntryTime,Bias,OrderType,Result,Pnl,Quantity,VWAPDistance,DeltaMomentum,CvdSlope,CvdRSquared",
                        $@"C:\Users\fassa\Documents\NinjaTrader Testing\TradeLog{DateTime.Now:yyyyMMdd_HHmmss}.csv");
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

            if (SkipMonthsSet.Contains(Time[0].Month))
            {
                TradingHalted = true;
                return;
            }

            if (!Events.IsInWeek(newsFilter, Time[0]))
            {
                PrintInfo($"[TRADING_HALTED] Skipping as NFP {newsFilter.IncludeNFP} CPI {newsFilter.IncludeCPI} FOMC {newsFilter.IncludeFOMC} NoEvents {newsFilter.IncludeNoEvents}...");
                TradingHalted = true;
                return;
            }

            // Skip all day events (easter etc).
            var newsForToday = GetNewsEventForToday();
            if (newsForToday != null && Position.MarketPosition == MarketPosition.Flat &&
                (newsForToday.AllDay || (FilterNewsDays && (newsForToday.IsNFP || newsForToday.IsCPI))))
            {
                TradingHalted = true;
                PrintInfo($"[TRADING_HALTED] event {newsForToday}.");
                return;
            }

            // Stop trading when the last candle is disproportionally big.
            if (CandleBodyRange(0) > 300)
            {
                TradingHalted = true;
                PrintInfo($"[TRADING_HALTED] last candle {CandleBodyRange(0)} points.");
                return;
            }

            runner ??= StrategyRunner_PO3.Configure(this, fractalManager, new RunnerConfiguration_PO3()
            {
                Ranges = ranges,
                Risk = Risk,
                StopGrace = StopGrace,
            });

            // Start tracking context from 6am London time. 
            if (LondonTime().Hour < 6)
                return;

            // If the session has ended stop processing.
            if (NewYorkTime().Hour >= NewYorkEndTime && NewYorkTime().Date == Time[0].Date &&
                Position.MarketPosition == MarketPosition.Flat)
            {
                CancelLimitOrder();
                PrintInfo($"[TRADING_HALTED] session end");
                TradingHalted = true;
                return;
            }

            // Update the CVD after 6am up until the close
            if (BarsInProgress == 1)
            {
                OrderFlowCVD.Update(OrderFlowCVD.BarsArray[1].Count - 1, 1);
                return;
            }

            atrCurrent = dailyAtr.Atr;

            fractalManager.DetectFractalThreeBarWithPlatos(1);
            fractalManager.DetectFractalFiveBar(1);

            DetectBOS();

            // Create the initial dealing range, this will only happen once per session.
            if (CurrentRange == null)
            {
                CurrentRange = CreateDealingRange();
                return;
            }

            // Determine if we are still in the same dealing range.
            var found = ranges.FirstOrDefault(r => r.IsInRange(Close[0])) ?? CreateDealingRange();

            // Always process range breaks
            if (found != CurrentRange)
            {
                if (PreviousRange != CurrentRange)
                {
                    // Update Current Range's coverage as a candle could have open and closed into a new range.
                    CurrentRange.UpdateCoverage(Close[0]);

                    PreviousRange = CurrentRange;

                    CurrentRange = found;

                    rangeBreakDetected = true;
                    PrintDebug($"[RANGE_BREAK] new range {CurrentRange}");
                }
            }

            // We only start tracking range coverage from 6am London time.
            CurrentRange.UpdateCoverage(Close[0]);

            /* ----- STRATEGY START TIME ANCHORED TO LONDON-----*/
            if (LondonTime().TimeOfDay < new TimeSpan(8, 0, 0))
                return;

            // Reset the range break at London Open
            if (LondonTime().TimeOfDay == new TimeSpan(8, 0, 0))
                rangeBreakDetected = false;

            // Range bias detection should always run after the open.
            if (CurrentRange.Low == PreviousRange?.High &&
                runner.Configuration.RangeBias != RangeBias.Bullish)
            {
                runner.Configuration.RangeBias = RangeBias.Bullish;
                PrintDebug($"Range bias {runner.Configuration.RangeBias}: Trade bias {tradeBias}");
            }

            if (CurrentRange.High == PreviousRange?.Low &&
                runner.Configuration.RangeBias != RangeBias.Bearish)
            {
                runner.Configuration.RangeBias = RangeBias.Bearish;
                PrintDebug($"Range bias {runner.Configuration.RangeBias}: Trade bias {tradeBias}");
            }

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
            var validSkipHour = LondonTime().Hour == 10;
            if (validSkipHour)
            {
                CancelLimitOrder();
                rangeBreakDetected = false;
                return;
            }

            // Cancel limit orders close to end of sessions blocks.
            // TODO: We need to retest these once done.
            var validBlockEndHour = LondonTime().Hour == 9 || LondonTime().Hour == 12;
            if (validBlockEndHour && Time[0].Minute >= 55)
            {
                CancelLimitOrder();
                return;
            }

            // We have to wait for the first range break before any trades are taken.
            if (!rangeBreakDetected)
                return;

            // If we set a limit order and price gets to TP we need to clear the limit order
            // Always return as we are waiting for the PendingOrder to fill or be cancelled.
            if (CurrentTrade?.PendingOrder != null)
            {
                var shouldCancel = false;

                if ((CurrentTrade.Bias == TradeBias.Long && High[0] >= CurrentTrade.TradeMissedThreshold) ||
                   (CurrentTrade.Bias == TradeBias.Short && Low[0] <= CurrentTrade.TradeMissedThreshold))
                {
                    PrintDebug($"[LIMIT_ORDER_CANCEL] {CurrentTrade.Bias} price went to TP threshold {CurrentTrade.TradeMissedThreshold}.");
                    shouldCancel = true;
                }

                if (shouldCancel)
                {
                    var tradeCSV = $"{CurrentTrade.EntryTime},{CurrentTrade.Bias},{CurrentTrade.OrderType},Win,0,{CurrentTrade.PositionSize},{CurrentTrade.VWAPDistance},{CurrentTrade.DeltaMomentum},{CurrentTrade.CvdSlope},{CurrentTrade.CvdRSquared}";
                    TradeLogs.Add(tradeCSV);

                    CancelLimitOrder();
                    rangeBreakDetected = false;
                }

                return;
            }

            /* Execute Strategy Runner */
            ExecuteStrategies();
        }

        private void ExecuteStrategies()
        {
            if (dayPnL > MaxProfit)
            {
                TradingHalted = true;
                PrintInfo($"[TRADING_HALTED] Max Profit reached {dayPnL}");
                return;
            }

            if (IsLive && State != State.Realtime)
                return;

            if (IsLive && !isConnected)
                return;

            if (LondonTime().Hour == 10)
                return;

            if (DetectAndCheckInvalidRange())
                return;

            var tradeDetails = runner.Run();

            if (tradeDetails == null || tradeDetails.TradeDetailsSubOptimal)
                return;

            if (CurrentTrade != null && CurrentTrade.Bias == tradeDetails.Bias && CurrentTrade.Entry == tradeDetails.Entry)
                return;

            tradeDetails.DeltaMomentum = CalculateDeltaMomentum();

            var regressionResult = CalculateRegression(OrderFlowCVD.DeltaClose, 5);
            tradeDetails.CvdSlope = regressionResult.Slope;
            tradeDetails.CvdRSquared = regressionResult.RSquared;

            if (tradeDetails.Bias == TradeBias.Short)
                EnterShortTrade(tradeDetails);

            if (tradeDetails.Bias == TradeBias.Long)
                EnterLongTrade(tradeDetails);
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


            if (SystemPerformance.AllTrades.Count > lastTradeCount)
            {
                var lastTrade = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];

                dayPnL += lastTrade.ProfitCurrency;
                lastTradeCount = SystemPerformance.AllTrades.Count;
            }

            var trade = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
            var winLoss = trade.ProfitCurrency >= 0 ? "Win" : "Loss";
            var tradeCSV = $"{trade.Entry.Time},{CurrentTrade.Bias},{CurrentTrade.OrderType},{winLoss},{trade.ProfitCurrency},{trade.Quantity},{CurrentTrade.VWAPDistance},{CurrentTrade.DeltaMomentum},{CurrentTrade.CvdSlope},{CurrentTrade.CvdRSquared}";
            TradeLogs.Add(tradeCSV);
            PrintTrade(tradeCSV);

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

            // It possible that after all processing a trade could have exited
            // but a valid scenario has appeared on the same bar.
            ExecuteStrategies();
        }

        private void DetectBOS()
        {
            //---Bullish BOS ---
            if (fractalManager.Fractals.TryGetValue((1, FractalType.HighFiveBar), out (double Price, DateTime Time) lastFractalHigh))
            {
                if (Close[0] > lastFractalHigh.Price)
                {
                    runner.Configuration.BearishBOSDetected = false;
                    runner.Configuration.BullishBOSDetected = true;

                    PrintDebug($"[BULLISH_BOS]");
                }
            }

            // --- Bearish BOS ---
            if (fractalManager.Fractals.TryGetValue((1, FractalType.LowFiveBar), out (double Price, DateTime Time) lastFractalLow))
            {
                if (Close[0] < lastFractalLow.Price)
                {
                    runner.Configuration.BearishBOSDetected = true;
                    runner.Configuration.BullishBOSDetected = false;

                    PrintDebug($"[BEARISH_BOS]");
                }
            }
        }

        private void PerformCleanupBeforeSessionStart()
        {
            PrintDebug("[SESSION_START] resetting state");

            ranges.Clear();

            CancelLimitOrder();

            CurrentRange = null;
            PreviousRange = null;
            rangeBreakDetected = false;

            InvalidRange = null;
            SharedBorderTrade = null;

            tradeBias = TradeBias.Neutral;

            BullishBOSDetected = false;
            BearishBOSDetected = false;

            dailyAtr ??= new DailyAtr();

            fractalManager.Fractals.Clear();

            CurrentTrade = null;

            TradingHalted = false;

            dayPnL = 0;
            lastTradeCount = 0;
        }

        private void BreakEvenOrCloseTrade()
        {
            if (NewYorkTime().Hour == 14 && Time[0].Minute == 29)
            {
                PrintDebug($"[CLOSE_TRADE] market open, PnL {Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0])}");

                if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("short");

                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("long");
            }
            else
            {
                if (CurrentTrade.StopLoss == Position.AveragePrice)
                    return;

                var breakEvenDirection = "";

                if (CurrentTrade.Bias == TradeBias.Long && High[0] >= CurrentTrade.BreakEvenThreshold)
                {
                    breakEvenDirection = "long";
                    PrintDebug($"[BREAK_EVEN_TP] threshold reached");
                }

                if (CurrentTrade.Bias == TradeBias.Short && Low[0] <= CurrentTrade.BreakEvenThreshold)
                {
                    breakEvenDirection = "short";
                    PrintDebug($"[BREAK_EVEN_TP] threshold reached");
                }

                if (!string.IsNullOrEmpty(breakEvenDirection))
                {
                    SetStopLoss(breakEvenDirection, CalculationMode.Price, CurrentTrade.Bias == TradeBias.Long ? Position.AveragePrice + 0 : Position.AveragePrice - 0, true);
                    CurrentTrade.StopLoss = Position.AveragePrice;
                    PrintDebug($"[BREAK_EVEN] set {Position.AveragePrice}");
                }
            }
        }

        private void ProcessSharedBorderRange(bool isLoss)
        {
            var current = ranges.FirstOrDefault(r => r.IsInRange(CurrentTrade.OriginalStopLoss));
            var previous = ranges.FirstOrDefault(r => r.IsInRange(CurrentTrade.TakeProfit));

            // It is possible that we jumped ranges and as such this cannot be processed further.
            if (current == null || previous == null)
                return;

            if (isLoss == false)
            {
                current = ranges.FirstOrDefault(r => r.IsInRange(CurrentTrade.TakeProfit));
                previous = ranges.FirstOrDefault(r => r.IsInRange(CurrentTrade.OriginalStopLoss));
            }

            var currentSharedBorderRange = GetSharedBorderRange(current, isLoss, CurrentTrade.Bias);
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

        private static double GetSharedBorderRange(DealingRange applicable, bool isLoss, TradeBias tradeBias)
        {
            if (tradeBias == TradeBias.Short)
                return isLoss ? applicable.Low : applicable.High;

            if (tradeBias == TradeBias.Long)
                return isLoss ? applicable.High : applicable.Low;

            return 0;
        }

        private void CreateInvalidRange(DealingRange current, DealingRange previous, bool isLoss, TradeBias lastTradeBias)
        {
            if (InvalidRange != null)
                return;

            if (lastTradeBias == TradeBias.Short)
                InvalidRange = new InvalidRange(isLoss ? current.BR3 : previous.BR3, isLoss ? previous.BR2 : current.BR2);

            if (lastTradeBias == TradeBias.Long)
                InvalidRange = new InvalidRange(isLoss ? previous.BR3 : current.BR3, isLoss ? current.BR2 : previous.BR2);

            PrintDebug($"[RANGE_INVALID_CREATED] Invalid range created loss:{isLoss} rangeBias: {runner.Configuration.RangeBias}  last trade: {lastTradeBias} {InvalidRange}");
        }

        private DealingRange CreateDealingRange()
        {
            var newDealingRange = new DealingRange(PO3, Close[0]);
            ranges.Add(newDealingRange);
            PrintDebug($"Creating new range: {newDealingRange}");
            return newDealingRange;
        }
    }
}
