#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoOPR
{
    public class AlgoOPRv1 : AlgoBase
    {
        [NinjaScriptProperty]
        [Display(Name = "Stop Grace", Order = 4, GroupName = "Parameters")]
        public int StopGrace { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Target Grace", Order = 5, GroupName = "Parameters")]
        public int TargetGrace { get; set; }

        private double openingPrice;

        private FractalManager fractalManager;

        private double lastBearishBOSLocation;
        private double lastBullishBOSLocation;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "AlgoOPRv1";

                Calculate = Calculate.OnBarClose;

                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;

                StopGrace = 5;
                TargetGrace = 5;

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

                PrintDebug($"[INITIALIZING_SESSION] {Time[0]} Opening Price: {openingPrice}");
            }

            //--------------------------------------------------
            // Need enough bars for 3-bar fractal detection
            //--------------------------------------------------
            if (CurrentBar < 2)
                return;

            fractalManager.DetectFractalThreeBar(5);

            DetectBOS();

            //--------------------------------------------------
            // Stop taking NEW entries at NY Open
            //--------------------------------------------------
            if (NewYorkTime().TimeOfDay >= new TimeSpan(09, 30, 0) && NewYorkTime().Date == Time[0].Date)
                return;

            //--------------------------------------------------
            // One position at a time
            //--------------------------------------------------
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            //--------------------------------------------------
            // Need both fractals available
            //--------------------------------------------------
            if (!fractalManager.Fractals.TryGetValue((5, FractalType.HighThreeBar), out var lastFractalHigh) || !fractalManager.Fractals.TryGetValue((5, FractalType.LowThreeBar), out var lastFractalLow))
                return;

            //if (Low[0] <= openingPrice && High[0] >= openingPrice)
            //{
            //    PrintDebug($"[PRICE_AT_OPEN] Price touched the open. {Low[0]}, High: {High[0]}, Open: {openingPrice}");
            //    //ResetBOS();

            //    return;
            //}

            /* LONG */
            //if (BullishBOSDetected && openingPrice > Close[0] && lastBearishBOSLocation < openingPrice)
            if (BullishBOSDetected && openingPrice > Close[0])
            {
                var stopDistance = Close[0] - lastFractalLow.Price - StopGrace;
                var riskReward = CalculateRiskReward(stopDistance, openingPrice - Close[0]);
                if (riskReward < 0.75)
                {
                    PrintDebug($"[BULLISH_ENTRY] Skipping trade due to low risk-reward: {riskReward}");
                    ResetBOS();
                    return;
                }

                SetStopLoss("Long", CalculationMode.Price, lastFractalLow.Price - StopGrace, false);
                SetProfitTarget("Long", CalculationMode.Price, openingPrice - TargetGrace);

                PrintDebug($"[BULLISH_ENTRY] Entry: {Close[0]}, Stop: {lastFractalLow.Price}, Target: {openingPrice}");

                var position = CalculatePositionSize(stopDistance, 300);
                EnterLong(position, "Long");
            }

            /* SHORT */
            //if (BearishBOSDetected && openingPrice < Close[0] && lastBearishBOSLocation > openingPrice)
            if (BearishBOSDetected && openingPrice < Close[0])
            {
                var stopDistance = (lastFractalHigh.Price + StopGrace) - Close[0];
                var riskReward = CalculateRiskReward(stopDistance, Close[0] - openingPrice);
                if (riskReward < 0.75)
                {
                    PrintDebug($"[BEARISH_ENTRY] Skipping trade due to low risk-reward: {riskReward}");
                    ResetBOS();
                    return;
                }

                SetStopLoss("Short", CalculationMode.Price, lastFractalHigh.Price + StopGrace, false);
                SetProfitTarget("Short", CalculationMode.Price, openingPrice + TargetGrace);

                PrintDebug($"[BEARISH_ENTRY] Entry: {Close[0]}, Stop: {lastFractalHigh.Price}, Target: {openingPrice}");

                var position = CalculatePositionSize(stopDistance, 300);
                EnterShort(position, "Short");
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
            PrintTrade($"{trade.Entry.Time},{trade.Entry.MarketPosition},{"Market"},{winLoss},{trade.ProfitCurrency},{trade.Quantity}");

            //ResetBOS();
        }

        private void DetectBOS()
        {
            //---Bullish BOS ---
            if (fractalManager.Fractals.TryGetValue((5, FractalType.HighThreeBar), out (double Price, DateTime Time) lastFractalHigh))
            {
                if (Close[0] > lastFractalHigh.Price)
                //if (!BullishBOSDetected && Close[0] > lastFractalHigh.Price)
                //if (Close[0] > lastFractalHigh.Price && lastBullishBOSLocation != lastFractalHigh.Price)
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
                if (Close[0] < lastFractalLow.Price)
                //if (!BearishBOSDetected && Close[0] < lastFractalLow.Price)
                //if (Close[0] < lastFractalLow.Price && lastBearishBOSLocation != lastFractalLow.Price)
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