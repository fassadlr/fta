using System;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS
{
    public class AlgoORSv2 : AlgoBase
    {
        [NinjaScriptProperty]
        [Display(Name = "Session Start Time", Order = 5, GroupName = "Parameters")]
        public int SessionStartTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session End Time", Order = 6, GroupName = "Parameters")]
        public int SessionEndTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session Market Open Time", Order = 5, GroupName = "Parameters")]
        public int SessionMarketOpen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stop Grace", Order = 7, GroupName = "Parameters")]
        public int StopGrace { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max RR", Order = 8, GroupName = "Parameters")]
        public double MaxRiskReward { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max Trades", Order = 9, GroupName = "Parameters")]
        public double MaxTrades { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Take Profit Multiplier", Order = 10, GroupName = "Parameters")]
        public double TPMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Candle Body Percentage", Order = 11, GroupName = "Parameters")]
        public double CandleBodyPercentage { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Entry Percentage", Order = 12, GroupName = "Parameters")]
        public double EntryPercentage { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Risk", Order = 13, GroupName = "Parameters")]
        public int Risk { get; set; }

        private bool tradingComplete;

        private DailyRangeTracker dailyRangeTracker = new();
        private FractalManager fractalManager;
        private bool sessionInitialized;

        //private double rangeLow;
        //private DateTime rangeLowTime;

        //private double rangeHigh;
        //private DateTime rangeHighTime;

        //private TradeRangeInitiator rangeInitiator;
        private int tradeCount;
        private TradeBias tradeBias;
        private bool breakEvenSet;

        //private Order currentLimitOrder;

        private StrategyRunner runner;

        private int adjustedMarketOpen = 0;
        private int adjustedSessionStart = 0;
        private int adjustedSessionEnd = 0;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Fassa ORS v2";

                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;

                CandleBodyPercentage = 0.1;
                EntryPercentage = 0;
                MaxRiskReward = 1.75; // This can be 3 if we set EntryPercentage to 0.3 
                MaxTrades = 1;
                TPMultiplier = 1;

                SessionTimeOffset = 0;
                SessionMarketOpen = 143000;
                SessionStartTime = 144500;
                SessionEndTime = 160000;
                StopGrace = 10;
            }
            else if (State == State.Configure)
            {
                // Main chart should be 1-minute
                AddDataSeries(BarsPeriodType.Minute, 5);
                AddDataSeries(BarsPeriodType.Minute, 15);
            }
            else if (State == State.DataLoaded)
            {
                fractalManager = new FractalManager(this);

                dailyRangeTracker = new();

                adjustedMarketOpen = AddHoursToTime(SessionMarketOpen, SessionTimeOffset);
                adjustedSessionStart = AddHoursToTime(SessionStartTime, SessionTimeOffset);
                adjustedSessionEnd = AddHoursToTime(SessionEndTime, SessionTimeOffset);

                PrintDebug($"Market Open {adjustedMarketOpen}");
                PrintDebug($"Session Start {adjustedSessionStart}");
                PrintDebug($"Session End {adjustedSessionEnd}");
            }
        }

        protected override void OnBarUpdate()
        {
            if (Time[0].Month == 3 && Time[0].Day == 16)
            {
                return;
            }

            runner ??= StrategyRunner.Configure(this, fractalManager, new StrategySettings()
            {
                CandleBodyPercentage = CandleBodyPercentage,
                EntryPercentage = EntryPercentage,
                MaxRiskReward = MaxRiskReward,
                Risk = Risk,
                StopGrace = StopGrace,
                TPMultiplier = TPMultiplier,
            });

            // Filter by event days, skip days etc.
            if (FilterAllDay() && Position.MarketPosition == MarketPosition.Flat)
                return;

            if (BarsInProgress == 0 && Bars.IsFirstBarOfSession)
            {
                PrintDebug($"Starting a new trading day.");

                breakEvenSet = false;
                dailyRangeTracker = new();
                sessionInitialized = false;
                tradingComplete = false;
                tradeBias = TradeBias.Neutral;
                tradeCount = 0;

                runner.Settings.RangeLow = 0;
                runner.Settings.RangeHigh = 0;
                runner.Settings.RangeInitiator = TradeRangeInitiator.None;

                return;
            }

            if (IsDSTOverlap())
            {
                adjustedMarketOpen = AddHoursToTime(SessionMarketOpen, -1);
                adjustedSessionStart = AddHoursToTime(SessionStartTime, -1);
                adjustedSessionEnd = AddHoursToTime(SessionEndTime, -1);

                //PrintDebug($"DST Market Open {adjustedMarketOpen}");
                //PrintDebug($"DST Session Start {adjustedSessionStart}");
                //PrintDebug($"DST Session End {adjustedSessionEnd}");
            }

            // --------------------------------------------------
            // 15-minute series only
            // --------------------------------------------------
            if (BarsInProgress == 2 && ToTime(Times[2][0]) <= adjustedMarketOpen)
            {
                fractalManager.DetectFractalThreeBar(15);
                fractalManager.DetectFractalFiveBar(15);
                return;
            }

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                // Calc max drawdown on the 1 min series
                if (BarsInProgress == 0)
                {
                    if (runner.CurrentTrade.Bias == TradeBias.Long && Low[0] < Position.AveragePrice &&
                        (
                            (Position.AveragePrice - Low[0] > runner.CurrentTrade.MaxDrawdown)
                            || runner.CurrentTrade.MaxDrawdown == 0
                        ))
                    {
                        runner.CurrentTrade.MaxDrawdown = Position.AveragePrice - Low[0];
                        runner.CurrentTrade.DrawdownStopPercentage = Math.Round(runner.CurrentTrade.MaxDrawdown / (Position.AveragePrice - runner.CurrentTrade.StopLoss), 2);
                    }

                    if (runner.CurrentTrade.Bias == TradeBias.Short && High[0] > Position.AveragePrice &&
                        (
                            (High[0] - Position.AveragePrice > runner.CurrentTrade.MaxDrawdown)
                            || runner.CurrentTrade.MaxDrawdown == 0
                        ))
                    {
                        runner.CurrentTrade.MaxDrawdown = High[0] - Position.AveragePrice;
                        runner.CurrentTrade.DrawdownStopPercentage = Math.Round(runner.CurrentTrade.MaxDrawdown / (runner.CurrentTrade.StopLoss - Position.AveragePrice), 2);
                    }
                }

                // Break Even Logic
                if (!breakEvenSet &&
                    ToTime(Time[0]) >= SessionEndTime &&
                    Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]) > 0)
                {
                    var tradeType = tradeBias == TradeBias.Long ? "long" : "short";
                    SetStopLoss(tradeType, CalculationMode.Price, Position.AveragePrice, true);
                    PrintDebug($"[BREAK_EVEN] {Position.AveragePrice}");

                    breakEvenSet = true;
                }

                return;
            }

            // --------------------------------------------------
            // 5-minute primary chart
            // --------------------------------------------------
            if (BarsInProgress == 0 || BarsInProgress == 2)
                return;

            // No more processing after session end
            // We can still look at the previous candles at the open of the 16:00 candle
            if (ToTime(Time[0]) > adjustedSessionEnd || tradingComplete)
            {
                if (runner.LimitOrder != null)
                {
                    CancelOrder(runner.LimitOrder);
                    runner.LimitOrder = null;
                }

                return;
            }

            fractalManager.DetectFractalThreeBar(5, runner.Settings.RangeHigh, runner.Settings.RangeLow);
            fractalManager.DetectFractalFiveBar(5);

            if (ToTime(Time[0]) < adjustedMarketOpen)
            {
                // Keep track of the daily range up until session start.
                dailyRangeTracker.Update(High[0], Low[0], Time[0]);
                //PrintDebug(dailyRangeTracker.ToString());
                //return;
            }

            // Initialize the trade range at open
            if (ToTime(Time[0]) == adjustedMarketOpen && !sessionInitialized)
            {
                InitializeTradeRange();

                sessionInitialized = true;
            }

            if (ToTime(Time[0]) > adjustedMarketOpen)
            {
                if (High[0] > runner.Settings.RangeHigh && runner.Settings.RangeInitiator != TradeRangeInitiator.RangeHighEngaged)
                {
                    PrintDebug($"[RANGE_HIGH_ENGAGED] {High[0]} > {runner.Settings.RangeHigh}");
                    runner.Settings.RangeInitiator = TradeRangeInitiator.RangeHighEngaged;
                }

                if (Low[0] < runner.Settings.RangeLow && runner.Settings.RangeInitiator != TradeRangeInitiator.RangeLowEngaged)
                {
                    PrintDebug($"[RANGE_LOW_ENGAGED] {Low[0]} < {runner.Settings.RangeLow}");
                    runner.Settings.RangeInitiator = TradeRangeInitiator.RangeLowEngaged;
                }
            }

            // A pending order is in play.
            if (runner.LimitOrder != null)
                return;

            runner.Run();
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            base.OnExecutionUpdate(execution, executionId, price, quantity, marketPosition, orderId, time);

            if (execution.Order.Name == "Profit target" && execution.Order.OrderState == OrderState.Filled && Position.MarketPosition == MarketPosition.Flat)
            {
                PrintDebug($"[WIN_TRADE_FILLED] Price {price} Time {time}");

                // Stop if the trade is a win.
                tradingComplete = true;

                var lastTrade = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
                runner.CurrentTrade.ProfitLoss = lastTrade.ProfitCurrency;

                runner.LimitOrder = null;

                PrintTrade($"{runner.CurrentTrade}");
            }

            if (execution.Order.Name == "Stop loss" && execution.Order.OrderState == OrderState.Filled && Position.MarketPosition == MarketPosition.Flat)
            {
                PrintDebug($"[LOSS_TRADE_FILLED] Price {price} Time {time}");

                tradeCount++;
                if (tradeCount >= MaxTrades)
                    tradingComplete = true;

                var lastTrade = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
                runner.CurrentTrade.ProfitLoss = lastTrade.ProfitCurrency;
                runner.CurrentTrade.MaxDrawdown = 0;
                runner.CurrentTrade.DrawdownStopPercentage = 0;

                runner.LimitOrder = null;

                PrintTrade($"{runner.CurrentTrade}");
            }
        }

        private void InitializeTradeRange()
        {
            fractalManager.DetectDelayedFiveBar(15);
            fractalManager.DetectViaLookback(15);

            // LOW
            var lowestPoint = fractalManager.RecentLow();
            PrintDebug($"[RANGE_LOW_INIT] {lowestPoint.Price} at {lowestPoint.Time}");

            // HIGH
            var highestPoint = fractalManager.RecentHigh();
            PrintDebug($"[RANGE_HIGH_INIT] {highestPoint.Price} at {highestPoint.Time}");

            runner.UpdateTradeRange(highestPoint.Price, lowestPoint.Price);

            Draw.Line(
                this,
                "Last15SwingHigh_" + highestPoint.Time.ToString("yyyyMMdd"),
                false,
                highestPoint.Time,
                highestPoint.Price,
                highestPoint.Time.AddHours(2),
                highestPoint.Price,
                Brushes.DodgerBlue,
                DashStyleHelper.Solid,
                2
            );

            Draw.Line(
                this,
                "Last15SwingLow_" + lowestPoint.Time.ToString("yyyyMMdd"),
                false,
                lowestPoint.Time,
                lowestPoint.Price,
                lowestPoint.Time.AddHours(2),
                lowestPoint.Price,
                Brushes.DodgerBlue,
                DashStyleHelper.Solid,
                2
            );
        }
    }
}