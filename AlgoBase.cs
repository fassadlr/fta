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
        [Display(Name = "Is Live", Order = 0, GroupName = "Base")]
        public bool IsLive { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Debug Logs", Order = 1, GroupName = "Base")]
        public bool DebugLogEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display Trade Logs", Order = 2, GroupName = "Base")]
        public bool TradeLogEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Export Trades CSV", Order = 3, GroupName = "Base")]
        public bool ExportTradesCSVEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Export Strategy Logs", Order = 4, GroupName = "Base")]
        public bool ExportStrategyLogsEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Import Tick and CVD", Order = 5, GroupName = "Base")]
        public bool ImportTickAndCvdEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Export Tick and CVD", Order = 6, GroupName = "Base")]
        public bool ExportTickAndCVDFlag { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Import File", Order = 7, GroupName = "Base")]
        public string ImportFile { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Daily Max Loss", Order = 8, GroupName = "Base")]
        public double DailyMaxLoss { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Daily Max Profit", Order = 9, GroupName = "Base")]
        public double DailyMaxProfit { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Daily Max Consecutive Wins", Order = 10, GroupName = "Base")]
        public double DailyMaxConsecutiveWins { get; set; }

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
        internal double BOSPrice = 0;

        internal readonly List<string> TradeCSVs = [];
        internal readonly List<string> StrategyLogs = [];

        internal TradeDetails CurrentTrade { get; set; }

        internal OrderFlowCumulativeDelta OrderFlowCVDIndicator { get; set; }
        internal EMA CVDEmaIndicator { get; set; }

        internal OrderFlowVWAP VWAP { get; set; }

        internal FractalManager FractalManager { get; set; }

        internal int LastTradeCount { get; set; }
        internal double DayPnL { get; set; }
        internal double DayPnLVirtual { get; set; }

        internal DateTime? LastInitialization { get; set; }

        internal bool IsConnected { get; set; }

        internal Dictionary<int, bool> DataSetValidation { get; set; }

        #region Trading Halt

        internal bool TradingHalted { get; set; }

        internal void HaltTrading(string haltMessage)
        {
            PrintInfo(haltMessage);

            TradingHalted = true;

            if (ExportStrategyLogsEnabled)
                ExportStrategyLogs(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "StrategyLogs", $@"{Account.Name}_{Instrument.FullName}_{DateTime.Now:yyyyMMdd}.csv"));
        }

        #endregion

        #region OnStateChange

        internal void OnDataLoadedBase(string algoName)
        {
            FractalManager = new FractalManager(this);

            if (!ImportTickAndCvdEnabled)
            {
                OrderFlowCVDIndicator = OrderFlowCumulativeDelta(CumulativeDeltaType.BidAsk, CumulativeDeltaPeriod.Session, 1);
                CVDSeries = new Series<double>(this);
                CVDEmaIndicator = EMA(CVDSeries, 20);
            }

            if (ImportTickAndCvdEnabled)
            {
                TickDataSeriesExport = [];

                if (!string.IsNullOrEmpty(ImportFile))
                {
                    ImportCVDData("EMA", $@"C:\Users\fassa\Documents\GitHub\fassadlr\ftalabs\research\Data\{algoName}_{Instrument.FullName}_CVDEma_{ImportFile}.csv");
                    ImportCVDData("DeltaClose", $@"C:\Users\fassa\Documents\GitHub\fassadlr\ftalabs\research\Data\{algoName}_{Instrument.FullName}_CVD_{ImportFile}.csv");
                    ImportTickData($@"C:\Users\fassa\Documents\GitHub\fassadlr\ftalabs\research\Data\{algoName}_{Instrument.FullName}_Ticks_{ImportFile}.bin");
                }
                else
                {
                    ImportCVDData("EMA", $@"C:\Users\fassa\Documents\GitHub\fassadlr\ftalabs\research\Data\{algoName}_{Instrument.FullName}_CVDEma_{DateTime.Now:yyyyMMdd}.csv");
                    ImportCVDData("DeltaClose", $@"C:\Users\fassa\Documents\GitHub\fassadlr\ftalabs\research\Data\{algoName}_{Instrument.FullName}_CVD_{DateTime.Now:yyyyMMdd}.csv");
                    ImportTickData($@"C:\Users\fassa\Documents\GitHub\fassadlr\ftalabs\research\Data\{algoName}_{Instrument.FullName}_Ticks_{DateTime.Now:yyyyMMdd}.bin");
                }
            }

            if (ExportTickAndCVDFlag)
            {
                TickDataSeriesExport = [];
                OHLCDataSeriesExport = [];
            }
        }

        internal void OnTerminatedBase(string algoName)
        {
            if (ExportTradesCSVEnabled)
            {
                ExportTradeLogs(
                    "EntryTime,ExitTime,Bias,OrderType,Result,Pnl,Quantity,CVD,CVD_EMA,CVD_EMA_Alignment,CVD_TradeType,VWAP_Distance,Atr",
                    $@"C:\Users\fassa\Documents\GitHub\fassadlr\ftalabs\research\data\{algoName}_{Instrument.FullName}_{DateTime.Now:yyyyMMdd}.csv");
            }

            if (ExportTickAndCVDFlag)
            {
                ExportTickData($@"C:\Users\fassa\Documents\GitHub\fassadlr\ftalabs\research\data\{algoName}_{Instrument.FullName}_Ticks_{DateTime.Now:yyyyMMdd}.bin");
                //ExportOHLCVData($@"C:\Users\fassa\Documents\GitHub\fassadlr\ftalabs\research\data\{algoName}_MNQ_1m_OHLC_Data_{DateTime.Now:yyyyMMdd}.csv");
                ExportCVD(CvdEmaExport, $@"C:\Users\fassa\Documents\GitHub\fassadlr\ftalabs\research\data\{algoName}_{Instrument.FullName}_CVDEma_{DateTime.Now:yyyyMMdd}.csv");
                ExportCVD(CvdDeltaCloseExport, $@"C:\Users\fassa\Documents\GitHub\fassadlr\ftalabs\research\data\{algoName}_{Instrument.FullName}_CVD_{DateTime.Now:yyyyMMdd}.csv");
            }
        }

        #endregion

        #region Constants

        public string LONG_TRADE = "long";
        public string SHORT_TRADE = "short";

        #endregion

        #region Volume Delta 

        internal Series<long> DeltaSeries { get; set; }

        internal double CurrentBid { get; set; }
        internal double CurrentAsk { get; set; }
        internal long BuyVolume { get; set; }
        internal long SellVolume { get; set; }
        internal long PreviousBarDelta { get; set; }

        internal Series<double> CvdSlopeSeries { get; set; }

        internal Series<double> CVDSeries { get; set; }

        #endregion

        #region Tick Data

        internal List<(long Ticks, double Price)> TickDataSeriesExport { get; set; }
        internal (long Ticks, double Price)[] TickDataSeriesImport { get; set; }

        internal void ImportTickData(string fileName)
        {
            Print($"{DateTime.Now} Reading tick data from {fileName}.");

            using var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);   // 1MB buffer
            {
                long recordCount = fs.Length / 16;

                TickDataSeriesImport = new (long Ticks, double Price)[recordCount];

                using var br = new BinaryReader(fs);

                for (long i = 0; i < recordCount; i++)
                {
                    TickDataSeriesImport[i] =
                    (
                        br.ReadInt64(),
                        br.ReadDouble()
                    );
                }
            }

            Print($"{DateTime.Now} Tick Data read from {fileName}.");
            Print($"{DateTime.Now} Loaded {TickDataSeriesImport.Length:N0} ticks.");
        }

        internal void ExportTickData(string fileName)
        {
            if (!TickDataSeriesExport.Any())
                return;

            using var fs = new FileStream(fileName, FileMode.Create, FileAccess.Write, FileShare.None);
            using var bw = new BinaryWriter(fs);

            foreach (var (Ticks, Price) in TickDataSeriesExport)
            {
                bw.Write(Ticks);
                bw.Write(Price);
            }

            Print($"Tick data written to {fileName}");
        }

        internal int FindTickIndex(long timestamp)
        {
            int low = 0;
            int high = TickDataSeriesImport.Length - 1;

            while (low <= high)
            {
                int mid = (low + high) >> 1;

                if (TickDataSeriesImport[mid].Ticks < timestamp)
                    low = mid + 1;
                else
                    high = mid - 1;
            }

            return low;
        }

        internal (int NewIndex, (long Timestamp, double Price)? Tick) GetNextTick(int currentTickIndex, long nextBarTicks)
        {
            if (currentTickIndex >= TickDataSeriesImport.Length)
                return (currentTickIndex, null);

            var tick = TickDataSeriesImport[currentTickIndex];

            if (tick.Ticks >= nextBarTicks)
                return (currentTickIndex, null);

            return (currentTickIndex + 1, tick);
        }

        #endregion

        #region 1m OHLC Data

        internal List<(string timestamp, double open, double high, double low, double close, long volume)> OHLCDataSeriesExport { get; set; }

        internal void ExportOHLCVData(string fileName)
        {
            if (!OHLCDataSeriesExport.Any())
                return;

            string path = fileName;

            using (StreamWriter sw = new(path))
            {
                foreach (var (timestamp, open, high, low, close, volume) in OHLCDataSeriesExport)
                {
                    sw.WriteLine($"{timestamp},{open},{high},{low},{close},{volume}");
                }
            }

            foreach (var rollover in Bars.Instrument.MasterInstrument.RolloverCollection)
            {
                Print(
                    $"{rollover.ContractMonth:MM-yy}," +
                    $"{rollover.Date:yyyy-MM-dd}," +
                    $"{rollover.Offset}"
                );
            }


            //using var fs = new FileStream(fileName, FileMode.Create, FileAccess.Write, FileShare.None);
            //using var bw = new BinaryWriter(fs);

            //foreach (var (timestamp, open, high, low, close, volume, contract) in OHLCDataSeriesExport)
            //{
            //    bw.Write(timestamp);
            //    bw.Write(open);
            //    bw.Write(high);
            //    bw.Write(low);
            //    bw.Write(close);
            //    bw.Write(volume);
            //    bw.Write(contract);
            //}

            Print($"OHLCV data written to {fileName}");
        }

        #endregion

        #region Base Pnl

        internal double BasePnlUpdates(DateTime tradeExitTime, bool isLoss)
        {
            double tradePnl;

            if (isLoss)
                tradePnl = -Math.Abs(CurrentTrade.Entry - CurrentTrade.StopLoss) * CurrentTrade.PositionSize * Instrument.MasterInstrument.PointValue;
            else
                tradePnl = Math.Abs(CurrentTrade.TakeProfit - CurrentTrade.Entry) * CurrentTrade.PositionSize * Instrument.MasterInstrument.PointValue;

            tradePnl = Math.Round(tradePnl);

            var winLoss = tradePnl >= 0 ? "Win" : "Loss";

            var tradeCSV =
                $"{CurrentTrade.EntryTime}," +
                $"{tradeExitTime}," +
                $"{CurrentTrade.Bias}," +
                $"{CurrentTrade.OrderType}," +
                $"{winLoss}," +
                $"{tradePnl}," +
                $"{CurrentTrade.PositionSize}," +
                $"{CurrentTrade.CVD}," +
                $"{CurrentTrade.CVD_EMA}," +
                $"{CurrentTrade.CVD_EMA_Alignment}," +
                $"{CurrentTrade.CVD_TradeType}," +
                $"{CurrentTrade.VWAPDistance}," +
                $"{CurrentTrade.ATR}";

            TradeCSVs.Add(tradeCSV);
            PrintTrade(tradeCSV);

            DayPnL += tradePnl;
            PrintInfo($"[PNL_UPDATE] {tradePnl} DayPnL {DayPnL}");

            return tradePnl;
        }

        internal bool CheckDailyPnlLimits()
        {
            if (DailyMaxProfit != 0 && (DayPnL) >= DailyMaxProfit)
            {
                HaltTrading($"[TRADING_HALTED] Max Profit reached {DayPnL}");
                return false;
            }

            if (DailyMaxLoss != 0 && (DayPnL) <= DailyMaxLoss)
            {
                HaltTrading($"[TRADING_HALTED] Max Loss reached {DayPnL}");
                return false;
            }

            return true;
        }

        #endregion

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
            Print($"{Account.Name}|{Time[0]} {toPrint}");
            if (ExportStrategyLogsEnabled)
                StrategyLogs.Add($"{Account.Name}|{Time[0]} {toPrint}");
        }

        public void PrintDebug(string toPrint)
        {
            if (!DebugLogEnabled)
                return;

            Print($"{Account.Name}|{Time[0]} {toPrint}");
            if (ExportStrategyLogsEnabled)
                StrategyLogs.Add($"{Account.Name}|{Time[0]} {toPrint}");
        }

        public void PrintTrade(string toPrint)
        {
            if (!TradeLogEnabled)
                return;

            Print($"{Account.Name}|{Time[0]} {toPrint}");
            if (ExportStrategyLogsEnabled)
                StrategyLogs.Add($"{Account.Name}|{Time[0]} {toPrint}");
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

        public bool CheckCandleVolatility()
        {
            if (CandleBodyRange(0) > 300)
            {
                HaltTrading($"Trading halted as last candle was {CandleBodyRange(0)} points.");
                return false;
            }

            return true;
        }

        public void BreakEvenTrade()
        {
            // Check if the stop has already been moved to break even, if so then do nothing.
            if (CurrentTrade.StopLoss == Position.AveragePrice)
                return;

            var breakEvenDirection = "";

            if (CurrentTrade.Bias == TradeBias.Long && High[0] >= CurrentTrade.BreakEvenThreshold)
            {
                breakEvenDirection = LONG_TRADE;
                PrintDebug($"[BREAK_EVEN_TP] threshold reached");
            }

            if (CurrentTrade.Bias == TradeBias.Short && Low[0] <= CurrentTrade.BreakEvenThreshold)
            {
                breakEvenDirection = SHORT_TRADE;
                PrintDebug($"[BREAK_EVEN_TP] threshold reached");
            }

            if (!string.IsNullOrEmpty(breakEvenDirection))
            {
                SetStopLoss(breakEvenDirection, CalculationMode.Price, Position.AveragePrice, true);
                CurrentTrade.StopLoss = Position.AveragePrice;
                PrintDebug($"[BREAK_EVEN] set {Position.AveragePrice}");
            }
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

        internal bool ImbalanceFormed(TradeBias tradeBias, int minTicks = 1)
        {
            if (tradeBias == TradeBias.Long)
            {
                if (Low[0] > High[2] &&         // IMB
                    Low[0] - High[2] >= minTicks       // IMB gap distance
                   )
                {
                    PrintDebug($"[BULLISH_IMB_FORMED] {tradeBias}: {High[2]} {Low[0]}");
                    return true;
                }
            }

            if (tradeBias == TradeBias.Short)
            {
                if (High[0] < Low[2] &&         // IMB
                    Low[2] - High[0] >= minTicks       // IMB gap distance
                   )
                {
                    PrintDebug($"[BEARISH_IMB_FORMED] {tradeBias}: {High[0]} {Low[2]}");
                    return true;
                }
            }

            return false;
        }

        internal bool ImbalanceFormed(TradeBias tradeBias, out double imbalanceStart)
        {
            imbalanceStart = 0;

            if (tradeBias == TradeBias.Long)
            {
                if (Low[0] > High[2] &&         // IMB
                    Low[0] - High[2] >= 1       // IMB gap distance
                   )
                {
                    PrintDebug($"[BULLISH_IMB_FORMED] {tradeBias}: {High[2]} {Low[0]}");
                    imbalanceStart = Low[1];
                    return true;
                }
            }

            if (tradeBias == TradeBias.Short)
            {
                if (High[0] < Low[2] &&         // IMB
                    Low[2] - High[0] >= 1       // IMB gap distance
                   )
                {
                    PrintDebug($"[BEARISH_IMB_FORMED] {tradeBias}: {High[0]} {Low[2]}");
                    imbalanceStart = High[1];
                    return true;
                }
            }

            return false;
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
            return OrderFlowCVDIndicator.DeltaClose[barsAgo] - OrderFlowCVDIndicator.DeltaOpen[barsAgo];
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
            return Math.Round((OrderFlowCVDIndicator.DeltaClose[0] - OrderFlowCVDIndicator.DeltaClose[lookback]) / lookback, 2);
        }

        internal void ExportStrategyLogs(string fileName)
        {
            if (StrategyLogs.Count == 0)
                return;

            string path = fileName;

            using (StreamWriter sw = new(path))
            {
                foreach (string trade in StrategyLogs)
                {
                    sw.WriteLine(trade);
                }
            }

            Print($"Strategy log written to {path}");
        }

        internal void ExportTradeLogs(string fields, string fileName)
        {
            if (TradeCSVs.Count == 0)
                return;

            string path = fileName;

            using (StreamWriter sw = new(path))
            {
                sw.WriteLine(fields);
                foreach (string trade in TradeCSVs)
                {
                    sw.WriteLine(trade);
                }
            }

            Print($"Trade CSV written to {path}");
        }

        internal void ExportCVD(List<(string TimeStamp, double Price)> cvdData, string fileName)
        {
            if (!cvdData.Any())
                return;

            string path = fileName;

            using (StreamWriter sw = new(path))
            {
                foreach (var (TimeStamp, Price) in cvdData)
                {
                    sw.WriteLine($"{TimeStamp}, {Price}");
                }
            }

            Print($"CVD data written to {path}");
        }

        internal void ImportCVDData(string whichData, string fileName)
        {
            string path = fileName;

            using (StreamReader sr = new(path))
            {
                while (!sr.EndOfStream)
                {
                    var line = sr.ReadLine().Split(',');

                    if (whichData == "EMA")
                        CvdEmaImport.Add(DateTime.Parse(line[0]), Double.Parse(line[1]));

                    if (whichData == "DeltaClose")
                        CvdDeltaCloseImport.Add(DateTime.Parse(line[0]), Double.Parse(line[1]));
                }
            }

            Print($"CVD data read from {fileName}");
        }

        internal readonly List<(string, double)> CvdEmaExport = [];
        internal readonly List<(string, double)> CvdDeltaCloseExport = [];

        internal readonly Dictionary<DateTime, double> CvdEmaImport = [];
        internal readonly Dictionary<DateTime, double> CvdDeltaCloseImport = [];

        internal (double Slope, double RSquared, double SlopeZ) CalculateRegression(ISeries<double> series, int lookback, int zLookback)
        {
            if (CurrentBar < Math.Max(lookback - 1, zLookback))
                return (0, 0, 0);

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
                return (0, 0, 0);

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

            // ==========================
            // Store slope
            // ==========================
            CvdSlopeSeries[0] = slope;

            // ==========================
            // Calculate Slope Z-score
            // ==========================
            double meanSlope = 0;

            for (int i = 0; i < zLookback; i++)
                meanSlope += CvdSlopeSeries[i];

            meanSlope /= zLookback;

            double variance = 0;

            for (int i = 0; i < zLookback; i++)
                variance += Math.Pow(CvdSlopeSeries[i] - meanSlope, 2);

            double stdDev = Math.Sqrt(variance / zLookback);

            double slopeZ = stdDev > 0
                ? (slope - meanSlope) / stdDev
                : 0;

            return (
                Math.Round(slope, 2),
                Math.Round(rSquared, 4),
                Math.Round(slopeZ, 2)
            );
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

        internal void CollectStatistics()
        {
            if (!ImportTickAndCvdEnabled)
            {
                CurrentTrade.CVD = Math.Round(OrderFlowCVDIndicator.DeltaClose[0]);
                CurrentTrade.CVD_EMA = Math.Round(CVDEmaIndicator[0]);
                CurrentTrade.CVD_EMA_Distance = Math.Round(Math.Abs(OrderFlowCVDIndicator.DeltaClose[0] - CVDEmaIndicator[0]));
                CurrentTrade.CVD_EMA_Alignment = OrderFlowCVDIndicator.DeltaClose[0] > CVDEmaIndicator[0] ? "Above" : "Below";
            }
            else
            {
                CurrentTrade.CVD = Math.Round(CvdDeltaCloseImport[Time[0]]);
                CurrentTrade.CVD_EMA = Math.Round(CvdEmaImport[Time[0]]);
                CurrentTrade.CVD_EMA_Distance = Math.Round(Math.Abs(CurrentTrade.CVD - CurrentTrade.CVD_EMA));
                CurrentTrade.CVD_EMA_Alignment = CurrentTrade.CVD > CurrentTrade.CVD_EMA ? "Above" : "Below";
            }
        }

        internal TradeBias? DetectBOS(int timeframe)
        {
            //---Bullish BOS ---
            if (FractalManager.Fractals.TryGetValue((timeframe, FractalType.HighThreeBar), out (double Price, DateTime Time) lastFractalHigh))
            {
                if (Close[0] > lastFractalHigh.Price)
                {
                    BearishBOSDetected = false;
                    BullishBOSDetected = true;
                    BOSPrice = lastFractalHigh.Price;

                    //PrintDebug($"[BULLISH_BOS] at {Close[0]}, fractal {lastFractalHigh.Price}");

                    return TradeBias.Long;
                }
            }

            // --- Bearish BOS ---
            if (FractalManager.Fractals.TryGetValue((timeframe, FractalType.LowThreeBar), out (double Price, DateTime Time) lastFractalLow))
            {
                if (Close[0] < lastFractalLow.Price)
                {
                    BearishBOSDetected = true;
                    BullishBOSDetected = false;
                    BOSPrice = lastFractalLow.Price;

                    //PrintDebug($"[BEARISH_BOS] at {Close[0]}, fractal {lastFractalLow.Price}");

                    return TradeBias.Short;
                }
            }

            return null;
        }
    }

    public enum TradeBias
    {
        Long,
        Short,
        Neutral
    }
}
