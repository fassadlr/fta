#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoOPR
{
    public class AlgoOPRv1 : AlgoBase
    {
        [NinjaScriptProperty]
        [Display(Name = "End Hour", Order = 4, GroupName = "Parameters")]
        public int NewYorkEndHour { get; set; }

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

        private double openingPrice;

        private FractalManager fractalManager;

        private double lastBearishBOSLocation;
        private double lastBullishBOSLocation;

        private (MarketPosition LastTradeBias, double LastTradeProfit)? lastTradeResult;
        private int consecutiveLosses;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "AlgoOPRv1";

                Calculate = Calculate.OnBarClose;

                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;

                NewYorkEndHour = 9;
                StopGrace = 2;
                TargetGrace = 2;
                RiskRewardMin = 0.5;
                MaxLossesPerDay = 3;

                IsExitOnSessionCloseStrategy = false;
            }
            else if (State == State.Configure)
            {
                TokyoZone = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
                LondonZone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
                NewYorkZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }
            else if (State == State.DataLoaded)
            {
                fractalManager = new FractalManager(this);
            }
            else if (State == State.Terminated)
            {
                if (TradeLogs.Count > 0 && ExportTradeLogsEnabled)
                {
                    string path = $@"C:\Users\fassa\Documents\NinjaTrader Testing\OPRevTradeLog{DateTime.Now:yyyyMMdd_HHmmss}.csv";

                    using (StreamWriter sw = new(path))
                    {
                        sw.WriteLine("EntryTime,Bias,Result,Pnl,Quantity,OPDistance");
                        foreach (string trade in TradeLogs)
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
            //--------------------------------------------------
            // New Session
            //--------------------------------------------------
            if (Bars.IsFirstBarOfSession)
            {
                openingPrice = Open[0];

                fractalManager.Fractals.Clear();
                lastBearishBOSLocation = 0;
                lastBullishBOSLocation = 0;

                consecutiveLosses = 0;
                lastTradeResult = null;

                PrintDebug($"[INITIALIZING_SESSION] {Time[0]} Opening Price: {openingPrice}");
            }

            // Filter by news events (TODO, not sure if this still needed)
            if (FilterByEvents() && Position.MarketPosition == MarketPosition.Flat)
                return;

            //--------------------------------------------------
            // Need enough bars for 3-bar fractal detection
            //--------------------------------------------------
            if (CurrentBar < 5)
                return;

            fractalManager.DetectFractalThreeBar(5);
            fractalManager.DetectFractalFiveBar(5);

            DetectBOS();

            //--------------------------------------------------
            // Stop taking NEW entries at NY Open
            //--------------------------------------------------
            if (NewYorkTime().TimeOfDay >= new TimeSpan(NewYorkEndHour, 0, 0) && NewYorkTime().Date == Time[0].Date)
                return;

            //--------------------------------------------------
            // Start when Tokyo opens
            //--------------------------------------------------
            if (TokyoTime().TimeOfDay < new TimeSpan(09, 00, 0))
                return;

            //--------------------------------------------------
            // One position at a time
            //--------------------------------------------------
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (consecutiveLosses >= MaxLossesPerDay)
                return;

            //--------------------------------------------------
            // Need both fractals available
            //--------------------------------------------------
            if (!fractalManager.Fractals.TryGetValue((5, FractalType.HighThreeBar), out var fr3BarHigh) ||
                !fractalManager.Fractals.TryGetValue((5, FractalType.LowThreeBar), out var fr3BarLow) ||
                !fractalManager.Fractals.TryGetValue((5, FractalType.HighFiveBar), out var fr5BarHigh) ||
                !fractalManager.Fractals.TryGetValue((5, FractalType.LowFiveBar), out var fr5BarLow))
                return;

            //if (Low[0] <= openingPrice && High[0] >= openingPrice)
            //{
            //    PrintDebug($"[PRICE_AT_OPEN] Price touched the open. {Low[0]}, High: {High[0]}, Open: {openingPrice}");
            //    //ResetBOS();

            //    return;
            //}

            /* LONG */
            //if (BullishBOSDetected && openingPrice > Close[0] && lastBearishBOSLocation < openingPrice)
            if (BullishBOSDetected && openingPrice > Close[0] && Open[0] > fr5BarLow.Price)
            {
                if (lastTradeResult != null && lastTradeResult?.LastTradeBias == MarketPosition.Long && lastTradeResult?.LastTradeProfit > 0)
                {
                    PrintDebug($"[BULLISH_ENTRY_SKIP] last trade was a win");
                    ResetBOS();
                    return;
                }

                var stopPrice = fr5BarLow.Price - StopGrace;
                var stopDistance = Close[0] - stopPrice;
                var riskReward = CalculateRiskReward(stopDistance, openingPrice - Close[0]);
                if (riskReward < RiskRewardMin)
                {
                    PrintDebug($"[BULLISH_ENTRY_SKIP] risk-reward: {riskReward}, Stop: {stopPrice}");
                    ResetBOS();
                    return;
                }

                SetStopLoss("Long", CalculationMode.Price, stopPrice, false);

                var tp = openingPrice - Close[0] < 20 ? openingPrice : openingPrice - TargetGrace;
                SetProfitTarget("Long", CalculationMode.Price, tp);

                PrintDebug($"[BULLISH_ENTRY] Entry: {Close[0]}, Stop: {stopPrice}, Target: {tp}, RR: {riskReward}");

                var positionSize = CalculatePositionSize(stopDistance, 300);
                EnterLong(positionSize, "Long");

                CurrentTrade = new TradeDetails()
                {
                    Bias = TradeBias.Long,
                    Entry = Position.AveragePrice,
                    StopLoss = stopPrice,
                    TakeProfit = tp,
                    Quantity = positionSize,
                    OpeningPriceDistance = openingPrice - Close[0],
                };
            }

            /* SHORT */
            //if (BearishBOSDetected && openingPrice < Close[0] && lastBearishBOSLocation > openingPrice)
            if (BearishBOSDetected && openingPrice < Close[0] && Open[0] < fr5BarHigh.Price)
            {
                if (lastTradeResult != null && lastTradeResult?.LastTradeBias == MarketPosition.Short && lastTradeResult?.LastTradeProfit > 0)
                {
                    PrintDebug($"[BEARISH_ENTRY_SKIP] last trade was a win");
                    ResetBOS();
                    return;
                }

                var stopPrice = fr5BarHigh.Price + StopGrace;
                var stopDistance = stopPrice - Close[0];
                var riskReward = CalculateRiskReward(stopDistance, Close[0] - openingPrice);
                if (riskReward < RiskRewardMin)
                {
                    PrintDebug($"[BEARISH_ENTRY_SKIP] risk-reward: {riskReward}, Stop: {stopPrice}");
                    ResetBOS();
                    return;
                }

                SetStopLoss("Short", CalculationMode.Price, stopPrice, false);

                var tp = Close[0] - openingPrice < 20 ? openingPrice : openingPrice + TargetGrace;
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
                    OpeningPriceDistance = Close[0] - openingPrice,
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
            TradeLogs.Add($"{trade.Entry.Time},{CurrentTrade.Bias},{winLoss},{trade.ProfitCurrency},{CurrentTrade.Quantity},{CurrentTrade.OpeningPriceDistance}");
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
            if (fractalManager.Fractals.TryGetValue((5, FractalType.HighThreeBar), out (double Price, DateTime Time) lastFractalHigh))
            {
                if (Close[0] > lastFractalHigh.Price && lastBullishBOSLocation != lastFractalHigh.Price)
                {
                    lastBullishBOSLocation = lastFractalHigh.Price;
                    BearishBOSDetected = false;

                    // Only set BOS if price is still above the open.
                    if (Close[0] < openingPrice)
                        BullishBOSDetected = true;

                    PrintDebug($"[BULLISH_BOS] LastBOSLocation: {lastBearishBOSLocation}");
                }
            }

            // --- Bearish BOS ---
            if (fractalManager.Fractals.TryGetValue((5, FractalType.LowThreeBar), out (double Price, DateTime Time) lastFractalLow))
            {
                if (Close[0] < lastFractalLow.Price && lastBearishBOSLocation != lastFractalLow.Price)
                {
                    lastBearishBOSLocation = lastFractalLow.Price;

                    // Only set BOS if price is still above the open.
                    if (Close[0] > openingPrice)
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