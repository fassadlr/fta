#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoOPR
{
    public class AlgoOPRv2DAX : AlgoBase
    {
        [NinjaScriptProperty]
        [Display(Name = "Start Hour", Order = 4, GroupName = "Parameters")]
        public int SessionStartHour { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "End Hour", Order = 4, GroupName = "Parameters")]
        public int SessionEndHourNewYork { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stop Grace", Order = 5, GroupName = "Parameters")]
        public int StopGrace { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Target Grace", Order = 6, GroupName = "Parameters")]
        public int TargetGrace { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Risk Reward Min", Order = 7, GroupName = "Parameters")]
        public double RiskRewardMin { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max Losses Per Day", Order = 8, GroupName = "Parameters")]
        public double MaxLossesPerDay { get; set; }

        private double sessionPriceShortTarget;
        private double sessionPriceLongTarget;

        private FractalManager fractalManager;

        private double lastBearishBOSLocation;
        private double lastBullishBOSLocation;

        private (MarketPosition LastTradeBias, double LastTradeProfit)? lastTradeResult;
        private int consecutiveLosses;

        private DateTime? tradingSkippedForDay;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Algo OPR DAX v1";

                Calculate = Calculate.OnBarClose;

                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;

                SessionEndHourNewYork = 11;
                StopGrace = 2;
                TargetGrace = 2;
                RiskRewardMin = 0.5;
                MaxLossesPerDay = 3;

                IsExitOnSessionCloseStrategy = true;
            }
            else if (State == State.Configure)
            {
                LondonZone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
                NewYorkZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }
            else if (State == State.DataLoaded)
            {
                fractalManager = new FractalManager(this);
            }
            else if (State == State.Terminated)
            {
                if (TradeCSVs.Count > 0 && ExportTradesCSVEnabled)
                {
                    string path = $@"C:\Users\fassa\Documents\NinjaTrader Testing\OPRevTradeLog{DateTime.Now:yyyyMMdd_HHmmss}.csv";

                    using (StreamWriter sw = new(path))
                    {
                        sw.WriteLine("EntryTime,Bias,Result,Pnl,Quantity,OPDistance");
                        foreach (string trade in TradeCSVs)
                        {
                            sw.WriteLine(trade);
                        }
                    }

                    Print($"Trade log written to {path}");
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (tradingSkippedForDay != null && Time[0].Date == tradingSkippedForDay.Value.Date)
                return;

            //--------------------------------------------------
            // Reset Session
            //--------------------------------------------------
            if (Bars.IsFirstBarOfSession)
            {
                sessionPriceShortTarget = Close[0] > Open[0] ? Close[0] : Open[0];
                sessionPriceLongTarget = Close[0] > Open[0] ? Open[0] : Close[0];

                fractalManager.Fractals.Clear();

                lastBearishBOSLocation = 0;
                lastBullishBOSLocation = 0;

                PrintDebug($"[INITIALIZING_SESSION] Short Target: {sessionPriceShortTarget}, Long Target: {sessionPriceLongTarget}");

                consecutiveLosses = 0;

                lastTradeResult = null;

                tradingSkippedForDay = null;

                var news = GetNewsEventForToday();
                if (news == null)
                    return;

                if (news.AllDay)
                {
                    PrintDebug($"[SKIPPING_SESSION] AllDay: {news.AllDay}, BankHoliday: {news.BankHoliday}");
                    tradingSkippedForDay = Time[0].Date;
                    return;
                }
            }

            //--------------------------------------------------
            // End after .
            //--------------------------------------------------
            if (NewYorkTime().TimeOfDay >= new TimeSpan(SessionEndHourNewYork, 0, 0))
                return;

            //--------------------------------------------------
            // Need enough bars for 3-bar fractal detection
            //--------------------------------------------------
            if (CurrentBar < 5)
                return;

            fractalManager.DetectFractalThreeBar(1);
            fractalManager.DetectFractalFiveBar(1);

            DetectBOS();

            //--------------------------------------------------
            // One position at a time
            //--------------------------------------------------
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (consecutiveLosses >= MaxLossesPerDay)
                return;

            if (sessionPriceShortTarget == 0 || sessionPriceLongTarget == 0)
                return;

            //--------------------------------------------------
            // Need both fractals available
            //--------------------------------------------------

            if (!fractalManager.Fractals.TryGetValue((1, FractalType.HighThreeBar), out var fr3BarHigh) ||
                !fractalManager.Fractals.TryGetValue((1, FractalType.LowThreeBar), out var fr3BarLow) ||
                !fractalManager.Fractals.TryGetValue((1, FractalType.HighFiveBar), out var fr5BarHigh) ||
                !fractalManager.Fractals.TryGetValue((1, FractalType.LowFiveBar), out var fr5BarLow))
            {
                PrintDebug("[FRACTALS_MISSING]");
                return;
            }

            //if (Low[0] <= openingPrice && High[0] >= openingPrice)
            //{
            //    PrintDebug($"[PRICE_AT_OPEN] Price touched the open. {Low[0]}, High: {High[0]}, Open: {openingPrice}");
            //    //ResetBOS();

            //    return;
            //}

            /* LONG */
            //if (BullishBOSDetected && openingPrice > Close[0] && lastBearishBOSLocation < openingPrice)
            if (BullishBOSDetected && sessionPriceLongTarget > Close[0] && Open[0] > fr5BarLow.Price)
            {
                if (lastTradeResult != null && lastTradeResult?.LastTradeBias == MarketPosition.Long && lastTradeResult?.LastTradeProfit > 0)
                {
                    PrintDebug($"[BULLISH_ENTRY_SKIP] last trade was a win");
                    ResetBOS();
                    return;
                }

                var stopPrice = fr5BarLow.Price - StopGrace;
                var stopDistance = Close[0] - stopPrice;
                var riskReward = CalculateRiskReward(stopDistance, sessionPriceLongTarget - Close[0]);
                if (riskReward < RiskRewardMin)
                {
                    PrintDebug($"[BULLISH_ENTRY_SKIP] risk-reward: {riskReward}, Stop: {stopPrice}");
                    ResetBOS();
                    return;
                }

                SetStopLoss("Long", CalculationMode.Price, stopPrice, false);

                var tp = sessionPriceLongTarget - Close[0] < 20 ? sessionPriceLongTarget : sessionPriceLongTarget - TargetGrace;
                SetProfitTarget("Long", CalculationMode.Price, tp);

                PrintDebug($"[BULLISH_ENTRY] Entry: {Close[0]}, 5 bar Low: {fr5BarLow} Stop: {stopPrice}, Target: {tp}, RR: {riskReward}");

                var positionSize = CalculatePositionSize(stopDistance, 300);
                EnterLong(positionSize, "Long");

                CurrentTrade = new TradeDetails()
                {
                    Bias = TradeBias.Long,
                    Entry = Position.AveragePrice,
                    StopLoss = stopPrice,
                    TakeProfit = tp,
                    Quantity = positionSize,
                    OpeningPriceDistance = sessionPriceLongTarget - Close[0],
                };
            }

            /* SHORT */
            //if (BearishBOSDetected && openingPrice < Close[0] && lastBearishBOSLocation > openingPrice)
            if (BearishBOSDetected && sessionPriceShortTarget < Close[0] && Open[0] < fr5BarHigh.Price)
            {
                if (lastTradeResult != null && lastTradeResult?.LastTradeBias == MarketPosition.Short && lastTradeResult?.LastTradeProfit > 0)
                {
                    PrintDebug($"[BEARISH_ENTRY_SKIP] last trade was a win");
                    ResetBOS();
                    return;
                }

                var stopPrice = fr5BarHigh.Price + StopGrace;
                var stopDistance = stopPrice - Close[0];
                var riskReward = CalculateRiskReward(stopDistance, Close[0] - sessionPriceShortTarget);
                if (riskReward < RiskRewardMin)
                {
                    PrintDebug($"[BEARISH_ENTRY_SKIP] risk-reward: {riskReward}, Stop: {stopPrice}");
                    ResetBOS();
                    return;
                }

                SetStopLoss("Short", CalculationMode.Price, stopPrice, false);

                var tp = Close[0] - sessionPriceShortTarget < 20 ? sessionPriceShortTarget : sessionPriceShortTarget + TargetGrace;
                SetProfitTarget("Short", CalculationMode.Price, tp);

                PrintDebug($"[BEARISH_ENTRY] Entry: {Close[0]}, Stop: {stopPrice}, Target: {tp}, RR: {riskReward}");

                var positionSize = CalculatePositionSize(stopDistance, 300);
                EnterShort(positionSize, "Short");

                CurrentTrade = new TradeDetails()
                {
                    Bias = TradeBias.Short,
                    Entry = Position.AveragePrice,
                    StopLoss = stopPrice,
                    TakeProfit = tp,
                    Quantity = positionSize,
                    OpeningPriceDistance = Close[0] - sessionPriceShortTarget,
                };
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
            TradeCSVs.Add($"{trade.Entry.Time},{CurrentTrade.Bias},{winLoss},{trade.ProfitCurrency},{CurrentTrade.Quantity},{CurrentTrade.OpeningPriceDistance}");
            PrintTrade($"{trade.Entry.Time},{CurrentTrade.Bias},{winLoss},{trade.ProfitCurrency},{CurrentTrade.Quantity},{CurrentTrade.OpeningPriceDistance}");

            if (trade.ProfitCurrency < 0)
                consecutiveLosses++;

            if (trade.ProfitCurrency > 0)
                consecutiveLosses = 0;

            if (consecutiveLosses >= MaxLossesPerDay)
                PrintDebug($"[MAX_LOSSES_REACHED] Consecutive Losses: {consecutiveLosses}");

            lastTradeResult = (trade.Entry.MarketPosition, trade.ProfitCurrency);

            ResetBOS();
        }

        private void DetectBOS()
        {
            //---Bullish BOS ---
            if (fractalManager.Fractals.TryGetValue((1, FractalType.HighThreeBar), out (double Price, DateTime Time) lastFractalHigh))
            {
                if (Close[0] > lastFractalHigh.Price && lastBullishBOSLocation != lastFractalHigh.Price)
                {
                    lastBullishBOSLocation = lastFractalHigh.Price;
                    BearishBOSDetected = false;

                    // Only set BOS if price is still above the open.
                    if (Close[0] < sessionPriceLongTarget)
                        BullishBOSDetected = true;

                    PrintDebug($"[BULLISH_BOS] LastBOSLocation: {lastBearishBOSLocation}");
                }
            }

            // --- Bearish BOS ---
            if (fractalManager.Fractals.TryGetValue((1, FractalType.LowThreeBar), out (double Price, DateTime Time) lastFractalLow))
            {
                if (Close[0] < lastFractalLow.Price && lastBearishBOSLocation != lastFractalLow.Price)
                {
                    lastBearishBOSLocation = lastFractalLow.Price;

                    // Only set BOS if price is still above the open.
                    if (Close[0] > sessionPriceShortTarget)
                        BearishBOSDetected = true;

                    BullishBOSDetected = false;

                    PrintDebug($"[BEARISH_BOS] LastBOSLocation: {lastBearishBOSLocation}");
                }
            }
        }

        private void ResetBOS()
        {
            BearishBOSDetected = false;
            BullishBOSDetected = false;
        }
    }
}