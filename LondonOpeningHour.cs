
using System;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;

namespace NinjaTrader.NinjaScript.Strategies.FTA
{
    public class LondonOpeningHour : AlgoBase
    {
        private double rangeHigh;
        private double rangeLow;
        private double breakoutHigh;
        private double breakoutLow;
        private double breakoutClose;

        private bool buildingRange;
        private bool rangeComplete;
        private bool traded;

        private DateTime sessionDate = DateTime.MinValue;

        [NinjaScriptProperty]
        [Range(1, 240)]
        [Display(Name = "Opening Range Minutes", Order = 10, GroupName = "Parameters")]
        public int OpeningRangeMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 240)]
        [Display(Name = "Breakout Window Minutes", Order = 11, GroupName = "Parameters")]
        public int BreakoutWindowMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Risk", Order = 12, GroupName = "Parameters")]
        public int Risk { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10)]
        [Display(Name = "Reward Risk", Order = 13, GroupName = "Parameters")]
        public double RewardRisk { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "LondonOpeningHour";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                BarsRequiredToTrade = 1;

                ImportFile = string.Empty;

                OpeningRangeMinutes = 5;
                BreakoutWindowMinutes = 5;
                Risk = 200;
                RewardRisk = 1.0;
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            if (Bars.IsFirstBarOfSession)
                TradingHalted = false;

            if (TradingHalted || CurrentBar < 1)
                return;

            if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 1)
                return;

            DateTime time = Time[0];

            if (sessionDate != time.Date)
            {
                sessionDate = time.Date;
                buildingRange = false;
                rangeComplete = false;
                traded = false;
            }

            var eventForToday = GetNewsEventForToday();

            if (eventForToday != null &&
                eventForToday.EventTime.Date == time.Date &&
                eventForToday.AllDay &&
                Position.MarketPosition == MarketPosition.Flat)
            {
                HaltTrading($"[TRADING_HALTED] Trading skipped for day due to all day event {eventForToday.EventTime}");
                return;
            }

            DateTime rangeStart = time.Date.AddHours(8);
            DateTime rangeEnd = rangeStart.AddMinutes(OpeningRangeMinutes);

            // Build the opening range.
            if (time == rangeStart.AddMinutes(1))
            {
                rangeHigh = High[0];
                rangeLow = Low[0];
                buildingRange = true;
            }

            if (buildingRange && time > rangeStart && time <= rangeEnd)
            {
                rangeHigh = Math.Max(rangeHigh, High[0]);
                rangeLow = Math.Min(rangeLow, Low[0]);
            }

            if (time == rangeEnd && buildingRange)
            {
                buildingRange = false;
                rangeComplete = true;

                PrintDebug($"Opening Range: High={rangeHigh}, Low={rangeLow}");
                return;
            }

            if (!rangeComplete || traded || time <= rangeEnd)
                return;

            // Group subsequent 1-minute bars into breakout candles.
            int minutesSinceRange = (int)(time - rangeEnd).TotalMinutes;
            int minuteInWindow = (minutesSinceRange - 1) % BreakoutWindowMinutes;

            if (minuteInWindow == 0)
            {
                breakoutHigh = High[0];
                breakoutLow = Low[0];
            }
            else
            {
                breakoutHigh = Math.Max(breakoutHigh, High[0]);
                breakoutLow = Math.Min(breakoutLow, Low[0]);
            }

            breakoutClose = Close[0];

            // Only evaluate at the close of each complete breakout candle.
            if (minutesSinceRange % BreakoutWindowMinutes != 0)
                return;

            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            double entry = breakoutClose;
            double stop;
            double target;
            double stopDistance;
            int positionSize;

            if (breakoutClose > rangeHigh)
            {
                stop = Instrument.MasterInstrument.RoundToTickSize(breakoutLow);
                stopDistance = entry - stop;

                if (stopDistance <= 0)
                    return;

                target = Instrument.MasterInstrument.RoundToTickSize(entry + stopDistance * RewardRisk);
                positionSize = CalculatePositionSize(stopDistance, Risk);

                if (positionSize <= 0)
                    return;

                SetStopLoss("LondonLong", CalculationMode.Price, stop, false);
                SetProfitTarget("LondonLong", CalculationMode.Price, target);

                PrintDebug($"Long Breakout: Entry={entry}, Stop={stop}, Target={target}, Size={positionSize}");

                EnterLong(positionSize, "LondonLong");
                traded = true;
            }
            else if (breakoutClose < rangeLow)
            {
                stop = Instrument.MasterInstrument.RoundToTickSize(breakoutHigh);
                stopDistance = stop - entry;

                if (stopDistance <= 0)
                    return;

                target = Instrument.MasterInstrument.RoundToTickSize(entry - stopDistance * RewardRisk);
                positionSize = CalculatePositionSize(stopDistance, Risk);

                if (positionSize <= 0)
                    return;

                SetStopLoss("LondonShort", CalculationMode.Price, stop, false);
                SetProfitTarget("LondonShort", CalculationMode.Price, target);

                PrintDebug($"Short Breakout: Entry={entry}, Stop={stop}, Target={target}, Size={positionSize}");

                EnterShort(positionSize, "LondonShort");
                traded = true;
            }
        }
    }
}
