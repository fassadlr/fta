using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.MarketAnalyzerColumns;
using NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA
{
    public abstract class AlgoBase : Strategy
    {
        [NinjaScriptProperty]
        [Display(Name = "Is Live", Order = 0, GroupName = "Parameters")]
        public bool IsLive { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Debug Logs", Order = 1, GroupName = "Parameters")]
        public bool DebugLogEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Trade Logs", Order = 2, GroupName = "Parameters")]
        public bool TradeLogEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Export Trade Logs", Order = 3, GroupName = "Parameters")]
        public bool ExportTradeLogsEnabled { get; set; }

        internal double LastFractalHigh = 0;
        internal DateTime LastFractalHighTime;

        internal double LastFractalLow = 0;
        internal DateTime LastFractalLowTime;

        internal double LastFractalFiveBarHigh = 0;
        internal DateTime LastFractalFiveBarHighTime;

        internal double LastFractalFiveBarLow = 0;
        internal DateTime LastFractalFiveBarLowTime;

        internal List<NewsEvent> Events = NewsEvents.Events();

        internal int AdjustedSessionStart = 0;
        internal int AdjustedSessionEnd = 0;

        internal TimeZoneInfo TokyoZone { get; set; }
        internal TimeZoneInfo NewYorkZone { get; set; }
        internal TimeZoneInfo LondonZone { get; set; }

        internal bool BearishBOSDetected = false;
        internal bool BullishBOSDetected = false;

        internal readonly List<string> TradeLogs = [];

        internal bool TradingHalted { get; set; }

        internal TradeDetails CurrentTrade { get; set; }

        internal OrderFlowCumulativeDelta OrderFlowCVD { get; set; }

        internal OrderFlowVWAP VWAP { get; set; }

        #region Volume Delta 

        internal Series<long> DeltaSeries { get; set; }

        internal double CurrentBid { get; set; }
        internal double CurrentAsk { get; set; }
        internal long BuyVolume { get; set; }
        internal long SellVolume { get; set; }
        internal long PreviousBarDelta { get; set; }

        #endregion

        internal int AdjustSessionEnd()
        {
            var adjustSessionEnd = AdjustedSessionEnd;
            if (IsDSTOverlap())
                adjustSessionEnd = AdjustedSessionEnd - 1;

            return adjustSessionEnd;
        }

        internal int AddMinutesToTime(int ninjaTime, int minutes)
        {
            var ts = TimeSpan.ParseExact(
                ninjaTime.ToString("D6"),
                "hhmmss",
                null
            );

            ts = ts.Add(TimeSpan.FromMinutes(minutes));

            return ts.Hours * 10000 +
                   ts.Minutes * 100 +
                   ts.Seconds;
        }

        internal int AddHoursToTime(int ninjaTime, int hours)
        {
            var ts = TimeSpan.ParseExact(
                ninjaTime.ToString("D6"),
                "hhmmss",
                null
            );

            ts = ts.Add(TimeSpan.FromHours(hours));

            return ts.Hours * 10000 +
                   ts.Minutes * 100 +
                   ts.Seconds;
        }

        internal bool BullishCandle(int index)
        {
            return Close[index] > Open[index];
        }

        internal bool BearishCandle(int index)
        {
            return Close[index] < Open[index];
        }

        internal double CalculateRiskReward(double stopLossDistance, double profitTargetDistance)
        {
            return Math.Round(profitTargetDistance / stopLossDistance, 2);
        }

        internal double CandleBodyRange(int index)
        {
            if (Open[index] > Close[index])
                return Open[index] - Close[index];
            else
                return Close[index] - Open[index];
        }

        internal bool CloseHigher()
        {
            return Close[0] > Close[1];
        }

        internal bool CloseLower()
        {
            return Close[0] < Close[1];
        }

        internal bool BodyCandle(int index, double candleBodyPercentage)
        {
            var result = false;
            double bodyPercentage = 0;
            if (Close[index] > Open[index])
            {
                var bodyRange = Close[index] - Open[index];
                var candleRange = High[0] - Low[index];
                bodyPercentage = Math.Round(bodyRange / candleRange, 2);
                result = bodyPercentage >= candleBodyPercentage;
            }

            if (Close[index] < Open[index])
            {
                var bodyRange = Open[index] - Close[index];
                var candleRange = High[0] - Low[index];
                bodyPercentage = Math.Round(bodyRange / candleRange, 2);
                result = bodyPercentage >= candleBodyPercentage;
            }

            if (!result)
                PrintDebug($"[BODY_CANDLE_SUBOP] {index} {bodyPercentage}");

            return result;
        }

        internal int CalculatePositionSize(double stopLossDistance, double risk)
        {
            var stopLossTicks = Math.Round(stopLossDistance, 1, MidpointRounding.ToEven) / Instrument.MasterInstrument.TickSize;
            var tickValue = Instrument.MasterInstrument.PointValue * Instrument.MasterInstrument.TickSize;
            PrintDebug($"[CALCULATE_POSITION_SIZE] {Math.Round(risk / (stopLossTicks * tickValue), 2)}");
            return (int)Math.Round(risk / (stopLossTicks * tickValue), MidpointRounding.ToEven);
        }

        internal void DetectFractalWithPlatos()
        {
            // Need at least 3 bars
            if (CurrentBar < 2)
                return;

            // --- FRACTAL HIGH LOGIC ---
            double h0 = High[0];
            double h1 = High[1];
            double h2 = High[2];

            // Must be >= both neighbors (allows equal highs)
            bool isPeakShape = h1 >= h0 && h1 >= h2;

            // At least one side must be strictly lower (prevents flat triple-equal highs)
            bool hasStrictEdge = h1 > h0 || h1 > h2;

            if (isPeakShape && hasStrictEdge)
            {
                LastFractalHigh = h1;
                LastFractalHighTime = Time[1];
                //PrintDebug($"[FRACTAL_5M_HIGH] {LastFractalHigh} upperBound {upperBound} h1 {h1} time {LastFractalHighTime}");
            }

            // --- FRACTAL LOW LOGIC ---
            double l0 = Low[0];
            double l1 = Low[1];
            double l2 = Low[2];

            // Must be <= both neighbors (allows equal lows)
            bool isValleyShape = l1 <= l0 && l1 <= l2;

            // At least one side must be strictly higher (prevents flat triple-equal lows)
            bool hasStrictEdgeLow = l1 < l0 || l1 < l2;

            if (isValleyShape && hasStrictEdgeLow)
            {
                LastFractalLow = l1;
                LastFractalLowTime = Time[1];
            }
        }

        internal bool IsDSTOverlap()
        {
            if ((Time[0].Date >= new DateTime(2024, 10, 27) && Time[0].Date <= new DateTime(2024, 11, 03)) ||
                (Time[0].Date >= new DateTime(2025, 3, 9) && Time[0].Date <= new DateTime(2025, 3, 30)) ||
                (Time[0].Date >= new DateTime(2025, 10, 26) && Time[0].Date <= new DateTime(2025, 11, 2)) ||
                (Time[0].Date >= new DateTime(2026, 3, 9) && Time[0].Date <= new DateTime(2026, 3, 29)))
            {
                //PrintDebug("Currently in DST overlap period, adjusting session end time to 12:00 to 13:00...");
                return true;
            }

            return false;
        }

        public void PrintInfo(string toPrint)
        {
            Print($"{Time[0]} {toPrint}");
        }

        public void PrintDebug(string toPrint)
        {
            if (DebugLogEnabled)
                Print($"{Time[0]} {toPrint}");
        }

        public void PrintTrade(string toPrint)
        {
            if (TradeLogEnabled)
                Print($"{toPrint}");
        }

        public NewsEvent GetNewsEventForToday()
        {
            var newsEvent = Events.FirstOrDefault(n => Time[0].Date == n.EventTime.Date);
            return newsEvent;
        }

        public bool FilterByEvents()
        {
            var newsEvent = Events.FirstOrDefault(n => Time[0].Date == n.EventTime.Date && Time[0].TimeOfDay >= n.EventTime.TimeOfDay && !n.BankHoliday);
            return newsEvent != null;
        }

        public bool FilterAllDay()
        {
            var newsEvent = Events.AllDay().FirstOrDefault(n => Time[0].Date == n.EventTime.Date);
            return newsEvent != null;
        }

        public bool FilterAllDayAndBankHolidays()
        {
            var newsEvent = Events.FirstOrDefault(n => (n.AllDay || n.BankHoliday) && Time[0].Date == n.EventTime.Date);
            return newsEvent != null;
        }

        public DateTime LondonTime()
        {
            return TimeZoneInfo.ConvertTime(Time[0], LondonZone);
        }

        public DateTime NewYorkTime()
        {
            return TimeZoneInfo.ConvertTime(Time[0], NewYorkZone);
        }

        public DateTime TokyoTime()
        {
            return TimeZoneInfo.ConvertTime(Time[0], TokyoZone);
        }

        public void EnterLongTrade(TradeDetails tradeDetails)
        {
            Order limitOrder = null;

            if (tradeDetails.IsLimit)
                limitOrder = EnterLongLimit(0, true, tradeDetails.PositionSize, tradeDetails.Entry, "long");
            else
                EnterLong(tradeDetails.PositionSize, "long");

            SetStopLoss("long", CalculationMode.Price, tradeDetails.StopLoss, true);
            SetProfitTarget("long", CalculationMode.Price, tradeDetails.TakeProfit, true);

            PrintDebug($"[{tradeDetails.Log}] Entry {tradeDetails.Entry} Size {tradeDetails.PositionSize} RR {tradeDetails.RiskReward} Stop Loss {tradeDetails.StopLoss} Profit Target {tradeDetails.TakeProfit}");

            tradeDetails.Entry = tradeDetails.IsLimit ? tradeDetails.Entry : Position.AveragePrice;
            tradeDetails.EntryTime = Time[0];
            tradeDetails.PendingOrder = limitOrder;

            CurrentTrade = tradeDetails;
        }

        public void EnterShortTrade(TradeDetails tradeDetails)
        {
            Order limitOrder = null;

            if (tradeDetails.IsLimit)
                limitOrder = EnterShortLimit(0, true, tradeDetails.PositionSize, tradeDetails.Entry, "short");
            else
                EnterShort(tradeDetails.PositionSize, "short");

            SetStopLoss("short", CalculationMode.Price, tradeDetails.StopLoss, true);
            SetProfitTarget("short", CalculationMode.Price, tradeDetails.TakeProfit, true);

            PrintDebug($"[{tradeDetails.Log}] Size {tradeDetails.PositionSize} RR {tradeDetails.RiskReward} Stop Loss {tradeDetails.StopLoss} Profit Target {tradeDetails.TakeProfit}");

            tradeDetails.Entry = tradeDetails.IsLimit ? tradeDetails.Entry : Position.AveragePrice;
            tradeDetails.EntryTime = Time[0];
            tradeDetails.PendingOrder = limitOrder;

            CurrentTrade = tradeDetails;
        }

        internal bool ImbalanceFormed(TradeBias tradeBias, DealingRange range)
        {
            if (tradeBias == TradeBias.Long)
            {
                if (Low[0] > High[2] &&         // IMB
                    Low[0] - High[2] >= 1 &&    // IMB gap distance
                    High[2] > range.ExtLow &&   // IMB must be inside range
                    Low[0] > range.Low &&
                    High[0] < range.Eq          // IMB must be in discount
                   )
                {
                    PrintDebug($"[BULLISH_IMB_FORMED] {tradeBias}: {High[2]} {Low[0]}");
                    return true;
                }
            }

            if (tradeBias == TradeBias.Short)
            {
                if (High[0] < Low[2] &&         // IMB
                    Low[2] - High[0] >= 1 &&    // IMB gap distance
                    Low[2] < range.ExtHigh &&   // IMB must be inside range
                    High[0] < range.High &&
                    Low[0] > range.Eq           // IMB must be in discount
                   )
                {
                    PrintDebug($"[BEARISH_IMB_FORMED] {tradeBias}: {High[0]} {Low[2]}");
                    return true;
                }
            }

            return false;
        }

        internal double GetBarDelta(int barsAgo)
        {
            return OrderFlowCVD.DeltaClose[barsAgo] - OrderFlowCVD.DeltaOpen[barsAgo];
        }

        internal double CalculateDeltaMomentum()
        {
            double recent = 0;
            double previous = 0;

            for (int i = 0; i < 5; i++)
                recent += GetBarDelta(i);

            for (int i = 5; i < 10; i++)
                previous += GetBarDelta(i);

            recent /= 5.0;
            previous /= 5.0;

            double deltaMomentum = recent - previous;

            return Math.Round(deltaMomentum, 2);
        }

        internal double CalculateCvdSlope(int lookback)
        {
            return Math.Round((OrderFlowCVD.DeltaClose[0] - OrderFlowCVD.DeltaClose[lookback]) / lookback, 2);
        }

        internal void ExportTradeLogs(string fields, string fileName)
        {
            if (TradeLogs.Count > 0)
            {
                string path = fileName;

                using (StreamWriter sw = new(path))
                {
                    sw.WriteLine(fields);
                    foreach (string trade in TradeLogs)
                    {
                        sw.WriteLine(trade);
                    }
                }

                Print($"Trade log written to {path}");
            }
        }

        internal (double Slope, double RSquared) CalculateRegression(ISeries<double> series, int lookback)
        {
            if (CurrentBar < lookback - 1)
                return (0, 0);

            double sumX = 0;
            double sumY = 0;
            double sumXY = 0;
            double sumX2 = 0;

            int x = 0;

            // ==========================
            // First pass - Calculate slope
            // ==========================
            for (int i = lookback - 1; i >= 0; i--)
            {
                double y = series[i];

                sumX += x;
                sumY += y;
                sumXY += x * y;
                sumX2 += x * x;

                x++;
            }

            double denominator = (lookback * sumX2) - (sumX * sumX);

            if (denominator == 0)
                return (0, 0);

            double slope = ((lookback * sumXY) - (sumX * sumY)) / denominator;
            double intercept = (sumY - slope * sumX) / lookback;

            // ==========================
            // Second pass - Calculate R²
            // ==========================
            double meanY = sumY / lookback;
            double ssRes = 0;
            double ssTot = 0;

            x = 0;

            for (int i = lookback - 1; i >= 0; i--)
            {
                double y = series[i];
                double predicted = intercept + slope * x;

                ssRes += Math.Pow(y - predicted, 2);
                ssTot += Math.Pow(y - meanY, 2);

                x++;
            }

            double rSquared = ssTot == 0
                ? 1.0
                : 1.0 - (ssRes / ssTot);

            return (Math.Round(slope, 2), Math.Round(rSquared, 4));
        }

        internal void CalculateVWAPDistance()
        {
            if (CurrentTrade.Entry > VWAP.VWAP[0])
            {
                CurrentTrade.VWAPLocation = "Above";
                CurrentTrade.VWAPDistance = Math.Round(CurrentTrade.Entry - VWAP.VWAP[0]);
            }
            else
            {
                CurrentTrade.VWAPLocation = "Below";
                CurrentTrade.VWAPDistance = Math.Round(VWAP.VWAP[0] - CurrentTrade.Entry);
            }
        }

        internal void CalculateStatistics()
        {
            CurrentTrade.DeltaMomentum = CalculateDeltaMomentum();

            var regressionResult = CalculateRegression(OrderFlowCVD.DeltaClose, 5);
            CurrentTrade.CvdSlopeFast = regressionResult.Slope;
            CurrentTrade.CvdRSquaredFast = regressionResult.RSquared;

            regressionResult = CalculateRegression(OrderFlowCVD.DeltaClose, 10);
            CurrentTrade.CvdSlopeSlow = regressionResult.Slope;
            CurrentTrade.CvdRSquaredSlow = regressionResult.RSquared;
            CurrentTrade.CvdAcceleration = CurrentTrade.CvdSlopeSlow - CurrentTrade.CvdRSquaredFast;

            CurrentTrade.BarVolume = Volume[0];
            CurrentTrade.BarVolumeDelta = PreviousBarDelta;
        }
    }

    public enum TradeBias
    {
        Long,
        Short,
        Neutral
    }
}
