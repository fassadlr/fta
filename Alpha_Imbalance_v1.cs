
#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.FTA
{
    public class Alpha_Imbalance_v1 : AlgoBase
    {
        private SMA smaIndicator;

        private string signal;

        private Order pendingOrder;

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Daily SMA Period", Order = 11, GroupName = "Parameters")]
        public int SmaPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Minimum FVG Ticks", Order = 12, GroupName = "Parameters")]
        public int MinimumFvgTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Risk", Order = 13, GroupName = "Parameters")]
        public int Risk { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Alpha Imbalance v1";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;

                IsExitOnSessionCloseStrategy = false;
                IsInstantiatedOnEachOptimizationIteration = true;

                StartBehavior = StartBehavior.WaitUntilFlat;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;

                BarsRequiredToTrade = 3;

                SmaPeriod = 14;
                MinimumFvgTicks = 1;

                ImportFile = string.Empty;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 5);

                TokyoZone = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
            }
            else if (State == State.DataLoaded)
            {
                smaIndicator = SMA(Closes[1], SmaPeriod);

                TokyoZone = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 0 && Bars.IsFirstBarOfSession)
            {
                PerformCleanupBeforeSessionStart();
            }

            if (CurrentBars[0] < SmaPeriod || CurrentBars[1] < 1)
                return;

            if (TradingHalted)
                return;

            if (TokyoTime().TimeOfDay < new TimeSpan(9, 5, 0))
                return;

            if (!CheckDailyPnlLimits())
                return;

            // If the session has ended stop processing.
            if (TokyoTime().Hour >= 12 && Position.MarketPosition == MarketPosition.Flat)
            {
                HaltTrading("[TRADING_HALTED] Session ended.");
                return;
            }

            if (BarsInProgress == 1)
            {
                var imbFormed = false;

                if (ImbalanceFormed(TradeBias.Long, MinimumFvgTicks))
                {
                    if (Close[0] < smaIndicator[0])
                    {
                        PrintDebug($"[SMA_BIAS_FAIL] long {Close[0]} {smaIndicator[0]}");
                        return;
                    }

                    CurrentTrade = new Shared.TradeDetails
                    {
                        Bias = TradeBias.Long,
                        Entry = Low[0],
                        StopLoss = Low[2],
                        TakeProfit = Low[0] + (Low[0] - Low[2])
                    };

                    imbFormed = true;
                }

                if (ImbalanceFormed(TradeBias.Short, MinimumFvgTicks))
                {
                    if (Close[0] > smaIndicator[0])
                    {
                        PrintDebug($"[SMA_BIAS_FAIL] short {Close[0]} {smaIndicator[0]}");
                        return;
                    }

                    CurrentTrade = new Shared.TradeDetails
                    {
                        Bias = TradeBias.Short,
                        Entry = High[0],
                        StopLoss = High[2],
                        TakeProfit = High[0] - (High[2] - High[0])
                    };

                    imbFormed = true;
                }

                if (!imbFormed)
                    return;

                if (pendingOrder != null)
                {
                    PrintDebug($"[CANCEL_LIMIT_ORDER] {CurrentTrade.Bias}, new IMB formed.");
                    CancelOrder(pendingOrder);
                    pendingOrder = null;
                }
            }

            if (BarsInProgress != 0)
                return;

            if (TokyoTime().TimeOfDay == new TimeSpan(9, 5, 0))
                PrintInfo($"[SESSION_START]");

            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (pendingOrder != null)
                return;

            if (CurrentTrade == null)
                return;

            CurrentTrade.PositionSize = CalculatePositionSize(Math.Abs(CurrentTrade.Entry - CurrentTrade.StopLoss), Risk);

            if (CurrentTrade.Bias == TradeBias.Long)
            {
                signal = LONG_TRADE;
                pendingOrder = EnterLongLimit(0, true, CurrentTrade.PositionSize, CurrentTrade.Entry, signal);
            }

            if (CurrentTrade.Bias == TradeBias.Short)
            {
                signal = SHORT_TRADE;
                pendingOrder = EnterShortLimit(0, true, CurrentTrade.PositionSize, CurrentTrade.Entry, signal);
            }

            PrintDebug($"[SUBMIT_LIMIT_ORDER] {CurrentTrade.Bias} Entry: {CurrentTrade.Entry}, StopLoss: {CurrentTrade.StopLoss}, TakeProfit: {CurrentTrade.TakeProfit}");

            SetStopLoss(signal, CalculationMode.Price, CurrentTrade.StopLoss, false);
            SetProfitTarget(signal, CalculationMode.Price, CurrentTrade.TakeProfit);
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            // Entry filled
            if ((execution.Order.Name == LONG_TRADE || execution.Order.Name == SHORT_TRADE) &&
                execution.Order.OrderState == OrderState.Filled &&
                Position.MarketPosition != MarketPosition.Flat)
            {
                CurrentTrade.EntryTime = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0);
                return;
            }

            // Exit completed
            if (execution.Order.OrderState == OrderState.Filled && Position.MarketPosition == MarketPosition.Flat)
            {
                BasePnlUpdates(time, execution.Order.Name.ToLowerInvariant() == "stop loss");

                CurrentTrade.PendingOrder = null;
            }
        }

        private void PerformCleanupBeforeSessionStart()
        {
            PrintInfo("[SESSION_INIT]");

            CancelOrder(pendingOrder);

            pendingOrder = null;

            CurrentTrade = null;

            TradingHalted = false;

            DayPnL = 0;
            DayPnLVirtual = 0;
            LastTradeCount = 0;
        }
    }
}
