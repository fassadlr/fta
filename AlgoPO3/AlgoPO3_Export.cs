using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public class AlgoPO3_Export : AlgoPO3Base
    {
        [NinjaScriptProperty]
        [Display(Name = "Risk", Order = 20, GroupName = "Parameters")]
        public int Risk { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session Start Hour", Order = 21, GroupName = "Parameters")]
        public int SessionStartHour { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session End Hour", Order = 22, GroupName = "Parameters")]
        public int SessionEndHour { get; set; }

        private bool sessionStarted;

        private bool rangeBreakDetected = false;

        internal override List<(double Min, double Max)> LongBuckets
        {
            get
            {
                return
                [
                ];
            }
        }

        internal override List<(double Min, double Max)> LongEdgeBuckets
        {
            get
            {
                return
                [
                ];
            }
        }

        internal override List<(double Min, double Max)> ShortBuckets
        {
            get
            {
                return
                [

                ];
            }
        }

        internal override List<(double Min, double Max)> ShortEdgeBuckets
        {
            get
            {
                return
                [
                ];
            }
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Exporter";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                IsExitOnSessionCloseStrategy = true;
                ConnectionLossHandling = ConnectionLossHandling.KeepRunning;

                PO3 = 81;
                Risk = 300;

                DailyMaxLoss = 0;
                DailyMaxProfit = 1000;

                LongEdgeMultiplier = 1;
                ShortEdgeMultiplier = 1;

                // London Time
                SessionStartHour = 8;
                SessionEndHour = 10;

                PrintTo = PrintTo.OutputTab2;
            }
            else if (State == State.Configure)
            {
                Ranges = [];

                Events = NewsEvents.Events();

                LondonZone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

                if (ImportTickAndCvdEnabled)
                {
                    DataSetValidation = new Dictionary<int, bool> { { 0, false } };
                }
                else
                {
                    //AddDataSeries(BarsPeriodType.Tick, 1);
                    DataSetValidation = new Dictionary<int, bool> { { 0, false } };
                }
            }
            else if (State == State.DataLoaded)
            {
                //VWAP = OrderFlowVWAP(VWAPResolution.Standard, Bars.TradingHours, VWAPStandardDeviations.One, 1, 2, 3);

                OnDataLoadedBase("London_CVD");
            }
            else if (State == State.Terminated)
            {
                OnTerminatedBase("London_CVD");
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 0 && Bars.IsFirstBarOfSession)
            {
                DataSetValidation[0] = false;
            }

            if (TradingHalted)
                return;

            if (BarsInProgress == 0 && !DataSetValidation[0])
            {
                DataSetValidation[0] = true;
                PrintInfo("[DATASET_VALIDATION] Primary series validated.");
            }

            if (!DataSetValidation.All(v => v.Value))
                return;

            // Skip all day events (easter etc).
            var eventForToday = GetNewsEventForToday();
            if (eventForToday != null &&
                eventForToday.EventTime.Date == Time[0].Date &&
                eventForToday.AllDay && Position.MarketPosition == MarketPosition.Flat)
            {
                HaltTrading($"Trading skipped for day due to all day event {eventForToday.EventTime}");
                return;
            }

            if (BarsInProgress == 0)
            {
                DateTime platformTime = DateTime.SpecifyKind(Time[0], DateTimeKind.Unspecified);

                DateTime utcTime = TimeZoneInfo.ConvertTimeToUtc(
                    platformTime,
                    Core.Globals.GeneralOptions.TimeZoneInfo);

                OHLCDataSeriesExport.Add((
                    utcTime.ToString("O"),
                    Open[0],
                    High[0],
                    Low[0],
                    Close[0],
                    (long)Volume[0]));
            }
        }
    }
}
