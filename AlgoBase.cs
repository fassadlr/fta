using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
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
        public bool ExportTradeLogs { get; set; }

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
    }

    public enum TradeBias
    {
        Long,
        Short,
        Neutral
    }
}
