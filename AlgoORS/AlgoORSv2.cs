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
        [Display(Name = "Session Start Time", Order = 6, GroupName = "Parameters")]
        public int SessionEndTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session Market Open Time", Order = 5, GroupName = "Parameters")]
        public int SessionMarketOpen { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stop Grace", Order = 7, GroupName = "Parameters")]
        public int StopGrace { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max RR", Order = 8, GroupName = "Parameters")]
        public double MaxRR { get; set; }

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

        private bool tradingComplete;

        private DailyRangeTracker dailyRangeTracker = new();
        private FractalManager fractalManager;
        private bool sessionInitialized;

        private double rangeLow;
        private DateTime rangeLowTime;

        private double rangeHigh;
        private DateTime rangeHighTime;

        private TradeRangeInitiator rangeInitiator;
        private int tradeCount;
        private TradeBias tradeBias;
        private bool breakEvenSet;

        private TradeDetails currentTrade;

        private Order currentLimitOrder;

        private StrategyRunner runner;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Fassa ORS v1";

                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;

                CandleBodyPercentage = 0.1;
                EntryPercentage = 0.3;
                MaxRR = 1.75; // This can be 3 if we set EntryPercentage to 0.3 
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
                runner = new StrategyRunner(this);

                SessionMarketOpen = AddHoursToTime(SessionMarketOpen, SessionTimeOffset);
                SessionStartTime = AddHoursToTime(SessionStartTime, SessionTimeOffset);
                SessionEndTime = AddHoursToTime(SessionEndTime, SessionTimeOffset);

                PrintDebug($"Market Open {SessionMarketOpen}");
                PrintDebug($"Session Start {SessionStartTime}");
                PrintDebug($"Session End {SessionEndTime}");
            }
        }

        protected override void OnBarUpdate()
        {
            if (Time[0].Month == 3 && Time[0].Day == 16)
            {
                return;
            }

            // Filter by event days, skip days etc.
            if (FilterAllDay() && Position.MarketPosition == MarketPosition.Flat)
                return;

            if (BarsInProgress == 0 && Bars.IsFirstBarOfSession)
            {
                PrintDebug($"Starting a new trading day.");

                breakEvenSet = false;
                dailyRangeTracker = new();
                sessionInitialized = false;
                rangeLow = 0;
                rangeHigh = 0;
                tradingComplete = false;
                tradeBias = TradeBias.Neutral;
                tradeCount = 0;
                rangeInitiator = TradeRangeInitiator.None;

                return;
            }

            var adjustedMarketOpen = SessionMarketOpen;
            var adjustedSessionStart = SessionStartTime;
            var adjustedSessionEnd = SessionEndTime;

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
                    if (currentTrade.Bias == TradeBias.Long && Low[0] < Position.AveragePrice &&
                        (
                            (Position.AveragePrice - Low[0] > currentTrade.MaxDrawdown)
                            || currentTrade.MaxDrawdown == 0
                        ))
                    {
                        currentTrade.MaxDrawdown = Position.AveragePrice - Low[0];
                        currentTrade.DrawdownStopPercentage = Math.Round(currentTrade.MaxDrawdown / (Position.AveragePrice - currentTrade.StopLoss), 2);
                    }

                    if (currentTrade.Bias == TradeBias.Short && High[0] > Position.AveragePrice &&
                        (
                            (High[0] - Position.AveragePrice > currentTrade.MaxDrawdown)
                            || currentTrade.MaxDrawdown == 0
                        ))
                    {
                        currentTrade.MaxDrawdown = High[0] - Position.AveragePrice;
                        currentTrade.DrawdownStopPercentage = Math.Round(currentTrade.MaxDrawdown / (currentTrade.StopLoss - Position.AveragePrice), 2);
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
            if (ToTime(Time[0]) >= adjustedSessionEnd || tradingComplete)
            {
                if (currentLimitOrder != null)
                {
                    CancelOrder(currentLimitOrder);
                    currentLimitOrder = null;
                }

                return;
            }

            fractalManager.DetectFractalThreeBar(5, rangeHigh, rangeLow);
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
                if (High[0] > rangeHigh && rangeInitiator != TradeRangeInitiator.RangeHighEngaged)
                {
                    PrintDebug($"[RANGE_HIGH_ENGAGED] {High[0]} > {rangeHigh}");
                    rangeInitiator = TradeRangeInitiator.RangeHighEngaged;
                }

                if (Low[0] < rangeLow && rangeInitiator != TradeRangeInitiator.RangeLowEngaged)
                {
                    PrintDebug($"[RANGE_LOW_ENGAGED] {Low[0]} < {rangeLow}");
                    rangeInitiator = TradeRangeInitiator.RangeLowEngaged;
                }
            }

            // A pending order is in play.
            if (currentLimitOrder != null)
                return;

            if (rangeInitiator == TradeRangeInitiator.RangeHighEngaged)
            {
                /* 
                * We can either close back in the range to go short or keep bullish momentum by going long
                */

                // LONG TRADE CONTINUATION
                // Wait for 2 consecutive bullish candles to form outside of the range
                if (ToTime(Time[0]) > AddMinutesToTime(adjustedSessionStart, 5) &&
                    BodyCandle(0) &&
                    BodyCandle(1) &&
                    BullishCandle(0) && Close[0] > rangeHigh &&
                    BullishCandle(1) && Close[1] > rangeHigh)
                {
                    var stopLoss = Low[0] - StopGrace;
                    var stopDistance = (Close[0] - Low[0]) + StopGrace;
                    var size = CalculatePositionSize(stopDistance);
                    if (size < 0.75)
                    {
                        PrintDebug($"[POSITION_SUBOPTIMAL_LONG] Position {size}, Stop {stopLoss}");
                        return;
                    }

                    var profitTarget = Close[0] + (stopDistance * TPMultiplier);
                    EnterLongTrade("LONG_ENTRY_CONTINUATION", false, TradeType.Continuation, (int)size, 1, stopLoss, profitTarget);
                }

                // SHORT TRADE REVERSAL
                // Wait for the first bearish candle to close inside the range
                if (ToTime(Time[0]) > adjustedSessionStart && Close[0] < rangeHigh && Open[0] - Close[0] >= 1)
                {
                    var stopLoss = fractalManager.RecentHigh();
                    var stopDistance = stopLoss.Price - Close[0];
                    double entry = 0;
                    if (EntryPercentage > 0)
                    {
                        entry = Math.Round(Close[0] + (stopDistance * EntryPercentage), 2);
                        stopDistance = Math.Round(stopLoss.Price - entry, 2);
                        PrintDebug($"[LIMIT_ADJUST_SHORT] stop distance {stopDistance}, entry {entry}");
                    }

                    var size = CalculatePositionSize(stopDistance);
                    if (size < 0.75)
                    {
                        PrintDebug($"[POSITION_SUBOPTIMAL_SHORT] Position {size}, Stop {stopLoss}");
                        return;
                    }

                    var rr = CalculateRR(stopDistance, Close[0] - rangeLow);
                    if (rr < 0.5)
                    {
                        PrintDebug($"[RR_SUBOPTIMAL_SHORT] RR {rr}");
                        return;
                    }

                    var takeProfit = rangeLow;
                    if (rr >= MaxRR)
                    {
                        rr = MaxRR;
                        takeProfit = Close[0] - (stopDistance * MaxRR);
                        PrintDebug($"[RR_ADJUST_SHORT] RR {rr}");
                    }

                    EnterShortTrade("SHORT_ENTRY_REVERSAL", EntryPercentage > 0, TradeType.Reversal, (int)size, rr, stopLoss.Price, takeProfit, entry);
                }
            }

            if (rangeInitiator == TradeRangeInitiator.RangeLowEngaged)
            {
                // LONG TRADE REVERSAL
                // Wait for the first bullish candle to close inside the range
                if (ToTime(Time[0]) > adjustedSessionStart && Close[0] > rangeLow && Close[0] - Open[0] >= 1)
                {
                    var stopLoss = fractalManager.RecentLow();
                    var stopDistance = Close[0] - stopLoss.Price;
                    double entry = 0;
                    if (EntryPercentage > 0)
                    {
                        entry = Math.Round(Close[0] - (stopDistance * EntryPercentage), 2);
                        stopDistance = entry - stopLoss.Price;
                        PrintDebug($"[LIMIT_ADJUST_LONG] stop distance {stopDistance}, entry {entry}");
                    }

                    var takeProfit = rangeHigh;
                    var size = CalculatePositionSize(stopDistance);
                    if (size < 0.75)
                    {
                        PrintDebug($"[POSITION_SUBOPTIMAL_LONG] Position {size}, Stop {stopLoss}");
                        return;
                    }

                    var rr = CalculateRR(stopDistance, rangeHigh - Close[0]);
                    if (rr < 0.5)
                    {
                        PrintDebug($"[RR_SUBOPTIMAL_LONG] RR {rr}");
                        return;
                    }

                    if (rr >= MaxRR)
                    {
                        PrintDebug($"[RR_ADJUST_LONG] RR {rr} to {MaxRR}");
                        rr = MaxRR;
                        takeProfit = Close[0] + (stopDistance * MaxRR);
                    }

                    EnterLongTrade("LONG_ENTRY_REVERSAL", EntryPercentage > 0, TradeType.Reversal, (int)size, rr, stopLoss.Price, takeProfit, entry);
                }

                // SHORT TRADE CONTINUATION
                // Wait for 2 consecutive bearish candles to form outside of the range AFTER session start
                if (ToTime(Time[0]) > AddMinutesToTime(adjustedSessionStart, 5) &&
                    BodyCandle(0) &&
                    BodyCandle(1) &&
                    BearishCandle(0) && Close[0] < rangeLow &&
                    BearishCandle(1) && Close[1] < rangeLow)
                {
                    var stopLoss = High[0] + StopGrace;
                    var stopDistance = (High[0] - Close[0]) + StopGrace;
                    var size = CalculatePositionSize(stopDistance);
                    if (size < 0.75)
                    {
                        PrintDebug($"[POSITION_SUBOPTIMAL_SHORT] Position {size}, Stop {stopLoss}");
                        return;
                    }

                    var profitTarget = Close[0] - (stopDistance * TPMultiplier);

                    EnterShortTrade("SHORT_ENTRY_CONTINUATION", false, TradeType.Continuation, (int)size, 1, stopLoss, profitTarget);
                }
            }
        }

        private void EnterShortTrade(string log, bool isLimit, TradeType tradeType, int positionSize, double rr, double stopLoss, double takeProfit, double entryPrice = 0)
        {
            if (isLimit)
                currentLimitOrder = EnterShortLimit(0, true, positionSize, entryPrice, "short");
            else
                EnterShort(positionSize, "short");

            SetStopLoss("short", CalculationMode.Price, stopLoss, true);
            SetProfitTarget("short", CalculationMode.Price, takeProfit, true);

            currentTrade = new TradeDetails
            {
                Bias = TradeBias.Short,
                Entry = Position.AveragePrice,
                EntryTime = Time[0],
                RangeInitiater = rangeInitiator,
                StopLoss = stopLoss,
                TakeProfit = takeProfit,
                Type = tradeType
            };

            PrintDebug($"[{log}] Size {positionSize} RR {rr} Stop Loss {stopLoss} Profit Target {takeProfit}");
        }

        private void EnterLongTrade(string log, bool isLimit, TradeType tradeType, int positionSize, double rr, double stopLoss, double takeProfit, double entryPrice = 0)
        {
            if (isLimit)
                currentLimitOrder = EnterLongLimit(0, true, positionSize, entryPrice, "long");
            else
                EnterLong(positionSize, "long");

            SetStopLoss("long", CalculationMode.Price, stopLoss, true);
            SetProfitTarget("long", CalculationMode.Price, takeProfit, true);

            currentTrade = new TradeDetails
            {
                Bias = TradeBias.Long,
                Entry = Position.AveragePrice,
                EntryTime = Time[0],
                RangeInitiater = rangeInitiator,
                StopLoss = stopLoss,
                TakeProfit = takeProfit,
                Type = tradeType
            };

            PrintDebug($"[{log}] Size {positionSize} RR {rr} Stop Loss {stopLoss} Profit Target {takeProfit}");
        }

        private double CalculateRR(double stopLossDistance, double profitTargetDistance)
        {
            return Math.Round(profitTargetDistance / stopLossDistance, 2);
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
                currentTrade.ProfitLoss = lastTrade.ProfitCurrency;

                currentLimitOrder = null;

                PrintTrade($"{currentTrade}");
            }

            if (execution.Order.Name == "Stop loss" && execution.Order.OrderState == OrderState.Filled && Position.MarketPosition == MarketPosition.Flat)
            {
                PrintDebug($"[LOSS_TRADE_FILLED] Price {price} Time {time}");

                tradeCount++;
                if (tradeCount >= MaxTrades)
                    tradingComplete = true;

                var lastTrade = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
                currentTrade.ProfitLoss = lastTrade.ProfitCurrency;
                currentTrade.MaxDrawdown = 0;
                currentTrade.DrawdownStopPercentage = 0;

                currentLimitOrder = null;

                PrintTrade($"{currentTrade}");
            }
        }

        private void InitializeTradeRange()
        {
            fractalManager.DetectDelayedFiveBar(15);
            fractalManager.DetectViaLookback(15);

            // LOW
            var lowestPoint = fractalManager.RecentLow();
            rangeLow = lowestPoint.Price;
            rangeLowTime = lowestPoint.Time;
            PrintDebug($"[RANGE_LOW_INIT] {rangeLow} at {rangeLowTime}");

            // HIGH
            var highestPoint = fractalManager.RecentHigh();
            rangeHigh = highestPoint.Price;
            rangeHighTime = highestPoint.Time;
            PrintDebug($"[RANGE_HIGH_INIT] {rangeHigh} at {rangeHighTime}");

            Draw.Line(
                this,
                "Last15SwingHigh_" + rangeHighTime.ToString("yyyyMMdd"),
                false,
                rangeHighTime,
                rangeHigh,
                rangeHighTime.AddHours(2),
                rangeHigh,
                Brushes.DodgerBlue,
                DashStyleHelper.Solid,
                2
            );

            Draw.Line(
                this,
                "Last15SwingLow_" + rangeLowTime.ToString("yyyyMMdd"),
                false,
                rangeLowTime,
                rangeLow,
                rangeLowTime.AddHours(2),
                rangeLow,
                Brushes.DodgerBlue,
                DashStyleHelper.Solid,
                2
            );
        }

        private double CalculatePositionSize(double stopLossDistance)
        {
            var risk = Risk;
            var stopLossTicks = Math.Round(stopLossDistance, 1, MidpointRounding.ToEven) / Instrument.MasterInstrument.TickSize;
            var tickValue = Instrument.MasterInstrument.PointValue * Instrument.MasterInstrument.TickSize;
            var position = Math.Round(risk / (stopLossTicks * tickValue), 2);
            return Math.Round(position, 2);
        }

        private bool BodyCandle(int index)
        {
            var result = false;
            double bodyPercentage = 0;
            if (Close[index] > Open[index])
            {
                var bodyRange = Close[index] - Open[index];
                var candleRange = High[0] - Low[index];
                bodyPercentage = Math.Round(bodyRange / candleRange, 2);
                result = bodyPercentage >= CandleBodyPercentage;
            }

            if (Close[index] < Open[index])
            {
                var bodyRange = Open[index] - Close[index];
                var candleRange = High[0] - Low[index];
                bodyPercentage = Math.Round(bodyRange / candleRange, 2);
                result = bodyPercentage >= CandleBodyPercentage;
            }

            if (!result)
                PrintDebug($"[BODY_CANDLE_SUBOP] {index} {bodyPercentage}");

            return result;
        }

        private bool BullishCandle(int index)
        {
            return Close[index] > Open[index];
        }

        private bool BearishCandle(int index)
        {
            return Close[index] < Open[index];
        }
    }
}