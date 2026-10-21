#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
#endregion

// PO3 81 London Open strategy - MNQ 1 minute
namespace NinjaTrader.NinjaScript.Strategies.FTA
{
    public class PO3_81_LondonOpen : Strategy
    {
        #region DealingRange
        private class DealingRange
        {
            public double Low, ExtLow, FV1, FV2, BR1, BR2, MB1, MB2, Eq, MB3, MB4, BR3, BR4, FV3, FV4, High, ExtHigh;
            public double OBCE1, ExtensionLowCE, FV2Entry, MBCE1, MBCE2, FV3Entry, OBCE2, ExtensionHighCE, ExtensionRange;
            public int MinCovered, MaxCovered;

            public DealingRange(int po3, double price)
            {
                Low = Math.Floor(price / po3) * po3;
                ExtLow = Low - (po3 * 0.111);
                FV1 = Low + (po3 * 0.11);
                FV2 = Low + (po3 * 0.17);
                BR1 = Low + (po3 * 0.29);
                BR2 = Low + (po3 * 0.41);
                MB1 = Low + (po3 * 0.41);
                MB2 = Low + (po3 * 0.47);
                Eq = Low + (po3 * 0.50);
                MB3 = Low + (po3 * 0.53);
                MB4 = Low + (po3 * 0.59);
                BR3 = Low + (po3 * 0.59);
                BR4 = Low + (po3 * 0.71);
                FV3 = Low + (po3 * 0.83);
                FV4 = Low + (po3 * 0.89);
                High = Low + po3;
                ExtHigh = High + (po3 * 0.111);

                OBCE1 = Low + ((FV1 - Low) / 2);
                ExtensionLowCE = ExtLow + ((Low - ExtLow) / 2);
                FV2Entry = Math.Round(FV2 / 0.05) * 0.05;
                MBCE1 = MB1 + ((MB2 - MB1) / 2);
                MBCE2 = MB3 + ((MB4 - MB3) / 2);
                FV3Entry = Math.Round(FV3 / 0.05) * 0.05;
                OBCE2 = FV4 + ((High - FV4) / 2);
                ExtensionHighCE = ExtHigh - ((ExtHigh - High) / 2);

                ExtensionRange = Math.Round(Low - ExtLow);
                MinCovered = 0;
                MaxCovered = 0;
            }
        }
        #endregion

        #region State
        private TimeZoneInfo srcTz, ukTz;
        private int dayKey = -1;
        private bool refSet, openEvaluated;
        private int refIdx;
        private int phase;          // 0 = waiting for first new range, 1 = inside armed range, 2 = left armed range (waiting re-entry)
        private int armedIdx, armedDir;
        private int tradesToday;
        private int prevIdx, lastDir;   // continuous range tracking: previous range index and direction of last range change
        private bool wasInPos;
        private bool lastExitWasTP;
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "PO3 81 dealing range - London open (MNQ 1min)";
                Name = "PO3_81_LondonOpen";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                BarsRequiredToTrade = 20;

                Po3Size = 81;
                RiskPerTrade = 100;
                MaxContracts = 50;
                MinRR = 0.5;
                EqOffsetPoints = 3;
                FractalStrength = 2;
                FractalLookback = 500;
                StopBufferPoints = 0;
                MaxTradesPerDay = 5;
                EnableDebug = true;
                AllowEqualFractals = false;
                TrackStartHHMM = 700;
                TradeStartHHMM = 800;
                TradeEndHHMM = 955;
                ChartTimeZoneId = "";
            }
            else if (State == State.DataLoaded)
            {
                ukTz = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); // UK incl. BST
                srcTz = string.IsNullOrEmpty(ChartTimeZoneId) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(ChartTimeZoneId);
            }
        }

        private DateTime ToUk(DateTime t)
        {
            return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), srcTz, ukTz);
        }

        private static TimeSpan Hhmm(int v) { return new TimeSpan(v / 100, v % 100, 0); }
        private int RangeIdx(double price) { return (int)Math.Floor(price / Po3Size); }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order == null || execution.Order.OrderState != OrderState.Filled) return;
            if (execution.Order.Name == "Profit target") lastExitWasTP = true;
            else if (execution.Order.Name == "Stop loss") lastExitWasTP = false;
        }

        private string lastLog = "";

        private void Log(string msg)
        {
            if (EnableDebug) Print(ToUk(Time[0]).ToString("yyyy-MM-dd HH:mm") + " UK | " + msg);
        }

        protected override void OnBarUpdate()
        {
            Process();

            if (!EnableDebug || !refSet || CurrentBar < BarsRequiredToTrade) return;
            TimeSpan tt = ToUk(Time[0]).TimeOfDay;
            if (tt < Hhmm(TrackStartHHMM) || tt > Hhmm(TradeEndHHMM)) return;

            int ix = RangeIdx(Close[0]);
            string state = "range " + (ix * Po3Size) + " | ref " + (refIdx * Po3Size) + " | phase " + phase +
                           " | armed " + (phase == 0 ? "-" : (armedIdx * Po3Size).ToString()) + " dir " + armedDir +
                           " | lastDir " + lastDir + " | pos " + Position.MarketPosition + " | trades " + tradesToday;
            if (state != lastLog)
            {
                Log("close " + Close[0] + " | " + state);
                lastLog = state;
            }
        }

        private void Process()
        {
            if (CurrentBar < BarsRequiredToTrade) return;

            DateTime uk = ToUk(Time[0]);
            int dk = uk.Year * 10000 + uk.Month * 100 + uk.Day;
            if (dk != dayKey)
            {
                dayKey = dk;
                refSet = false; openEvaluated = false;
                phase = 0; armedIdx = 0; armedDir = 0;
                tradesToday = 0;
                refIdx = 0; prevIdx = 0; lastDir = 0; wasInPos = false; lastExitWasTP = false; lastLog = "";
            }

            TimeSpan t = uk.TimeOfDay;

            // No new trades from the end time onwards. Open trades are left to hit their stop or target.
            if (t >= Hhmm(TradeEndHHMM)) return;

            // Reference range at 07:00 UK - this is the first range, where we are moving from
            if (!refSet && t >= Hhmm(TrackStartHHMM))
            {
                refIdx = RangeIdx(Open[0]);
                prevIdx = refIdx;
                lastDir = 0;
                refSet = true;
            }
            if (!refSet) return;

            // Track every range change from 07:00 onwards (multiple ranges, always know where we came from)
            int idx = RangeIdx(Close[0]);
            if (idx != prevIdx)
            {
                lastDir = idx > prevIdx ? 1 : -1;
                prevIdx = idx;
            }

            if (t < Hhmm(TradeStartHHMM)) return;

            bool inPos = Position.MarketPosition != MarketPosition.Flat;

            // Trade just closed (stop or target): arm the CURRENT range using the direction we entered it from.
            // e.g. long stopped out below the range -> we are in the lower range having come down, so it is armed
            // bearish and needs: go back into the old range, then re-enter this one -> short.
            if (wasInPos && !inPos && openEvaluated)
            {
                if (lastExitWasTP)
                {
                    // Target hit: that leg is spent. Same range cannot be re-armed - wait for a NEW range break from here.
                    refIdx = idx; phase = 0;
                    Log("Target hit - leg invalid, waiting for new range break from " + (idx * Po3Size));
                }
                else if (lastDir != 0) { armedIdx = idx; armedDir = lastDir; phase = 1; }
                else { refIdx = idx; phase = 0; }
                lastExitWasTP = false;
                wasInPos = false;
                return;
            }
            wasInPos = inPos;

            bool canTrade = !inPos && tradesToday < MaxTradesPerDay;

            // ---- London open: evaluate once ----
            if (!openEvaluated)
            {
                if (!canTrade) return;
                openEvaluated = true;
                if (idx != refIdx)
                {
                    // Scenario 1: new range entered since 07:00
                    int dir = idx > refIdx ? 1 : -1;
                    if (TryEnter(dir, idx, true)) return;
                    // RR failed (or no valid stop): no trade. Treat this range as the starting range and
                    // wait for a break out of it (Scenario 2 flow), rather than arming it.
                    Log("Scenario 1 rejected - using range " + (idx * Po3Size) + " as starting range");
                    refIdx = idx; phase = 0;
                }
                else
                {
                    refIdx = idx; phase = 0;
                }
                return;
            }

            // ---- Scenario 2: enter -> leave -> re-enter (runs even while in a trade, so range changes are never missed) ----
            if (phase == 0)
            {
                if (idx != refIdx)
                {
                    armedIdx = idx;
                    armedDir = idx > refIdx ? 1 : -1;
                    phase = 1;
                }
            }
            else if (phase == 1)
            {
                // Entered the armed range and ran straight to the target level without a re-entry: leg is invalid
                DealingRange ar = new DealingRange(Po3Size, armedIdx * Po3Size + Po3Size / 2.0);
                double legTp = armedDir > 0 ? ar.Eq - EqOffsetPoints : ar.Eq + EqOffsetPoints;
                bool reached = armedDir > 0 ? High[0] >= legTp : Low[0] <= legTp;
                if (reached && (idx - armedIdx) * armedDir <= 0)
                {
                    refIdx = idx; phase = 0;
                    Log("Price reached target level " + legTp.ToString("F2") + " before re-entry - leg invalid, waiting for new range break from " + (idx * Po3Size));
                    return;
                }

                if (idx == armedIdx) return;
                if ((idx - armedIdx) * armedDir > 0) armedIdx = idx;   // pushed on into a further range
                else phase = 2;                                        // fell back out of armed range
            }
            else if (phase == 2)
            {
                if (idx == armedIdx)
                {
                    if (!canTrade) { Log("Re-entry trigger ignored: position open or trade limit reached"); return; }                             // keep waiting until flat
                    if (TryEnter(armedDir, armedIdx, false)) return;
                    phase = 1; // could not take it (no stop / no reward) - keep watching
                }
                else if ((idx - armedIdx) * armedDir > 0)
                {
                    armedIdx = idx; phase = 1;                         // moved on into a further range
                }
                else if ((idx - prevRefForOpposite()) * armedDir < 0)
                {
                    // price went through to the opposite side - new range in the other direction
                    armedIdx = idx; armedDir = -armedDir; phase = 1;
                }
            }
        }

        // The range we armed from is one step back from the armed range
        private int prevRefForOpposite() { return armedIdx - armedDir; }

        private bool TryEnter(int dir, int idx, bool checkRR)
        {
            DealingRange r = new DealingRange(Po3Size, idx * Po3Size + Po3Size / 2.0);
            double entry = Close[0];
            double stop, tp, reward, risk;

            if (dir > 0)
            {
                double fl = FindFractalLow(r.ExtLow);
                if (double.IsNaN(fl)) { Log("No entry: no fractal low below ExtLow " + r.ExtLow.ToString("F2") + " in lookback"); return false; }
                stop = fl - StopBufferPoints;
                tp = r.Eq - EqOffsetPoints;
                reward = tp - entry;
                risk = entry - stop;
            }
            else
            {
                double fh = FindFractalHigh(r.ExtHigh);
                if (double.IsNaN(fh)) { Log("No entry: no fractal high above ExtHigh " + r.ExtHigh.ToString("F2") + " in lookback"); return false; }
                stop = fh + StopBufferPoints;
                tp = r.Eq + EqOffsetPoints;
                reward = entry - tp;
                risk = stop - entry;
            }

            if (reward <= 0 || risk <= 0)
            {
                Log("No entry: reward " + reward.ToString("F2") + " risk " + risk.ToString("F2") + " (stop " + stop.ToString("F2") + ", tp " + tp.ToString("F2") + ")");
                return false;
            }
            if (checkRR && reward / risk < MinRR)
            {
                Log("No entry: RR " + (reward / risk).ToString("F2") + " < " + MinRR + " (stop " + stop.ToString("F2") + ", tp " + tp.ToString("F2") + ")");
                return false;
            }

            int qty = (int)Math.Floor(RiskPerTrade / (risk * Instrument.MasterInstrument.PointValue));
            qty = Math.Min(qty, MaxContracts);
            if (qty < 1)
            {
                Log("No entry: stop distance " + risk.ToString("F2") + " pts (stop " + stop.ToString("F2") + ") too wide for risk of " + RiskPerTrade + " -> qty 0");
                return false;
            }

            stop = Instrument.MasterInstrument.RoundToTickSize(stop);
            tp = Instrument.MasterInstrument.RoundToTickSize(tp);

            if (dir > 0)
            {
                SetStopLoss("Long", CalculationMode.Price, stop, false);
                SetProfitTarget("Long", CalculationMode.Price, tp);
                EnterLong(qty, "Long");
            }
            else
            {
                SetStopLoss("Short", CalculationMode.Price, stop, false);
                SetProfitTarget("Short", CalculationMode.Price, tp);
                EnterShort(qty, "Short");
            }

            tradesToday++;
            Log((dir > 0 ? "ENTER LONG " : "ENTER SHORT ") + "qty " + qty + " entry~" + entry + " stop " + stop + " tp " + tp + " RR " + (reward / risk).ToString("F2"));
            return true;
        }

        // Most recent confirmed fractal low that sits below 'belowPrice'
        private double FindFractalLow(double belowPrice)
        {
            int maxK = Math.Min(FractalLookback, CurrentBar - FractalStrength);
            for (int k = FractalStrength; k <= maxK; k++)
            {
                bool ok = true;
                for (int j = 1; j <= FractalStrength; j++)
                    if (AllowEqualFractals ? (Low[k] > Low[k - j] || Low[k] > Low[k + j]) : (Low[k] >= Low[k - j] || Low[k] >= Low[k + j])) { ok = false; break; }
                if (ok && Low[k] < belowPrice) return Low[k];
            }
            return double.NaN;
        }

        // Most recent confirmed fractal high that sits above 'abovePrice'
        private double FindFractalHigh(double abovePrice)
        {
            int maxK = Math.Min(FractalLookback, CurrentBar - FractalStrength);
            for (int k = FractalStrength; k <= maxK; k++)
            {
                bool ok = true;
                for (int j = 1; j <= FractalStrength; j++)
                    if (AllowEqualFractals ? (High[k] < High[k - j] || High[k] < High[k + j]) : (High[k] <= High[k - j] || High[k] <= High[k + j])) { ok = false; break; }
                if (ok && High[k] > abovePrice) return High[k];
            }
            return double.NaN;
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Debug logging (Output window)", GroupName = "Debug", Order = 0)]
        public bool EnableDebug { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "PO3 range size (points)", GroupName = "Range", Order = 0)]
        public int Po3Size { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "Risk per trade ($)", GroupName = "Risk", Order = 0)]
        public double RiskPerTrade { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "Max contracts", GroupName = "Risk", Order = 1)]
        public int MaxContracts { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Min RR (scenario 1)", GroupName = "Risk", Order = 2)]
        public double MinRR { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "EQ target offset (points)", GroupName = "Exits", Order = 0)]
        public double EqOffsetPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Fractal strength (bars each side)", GroupName = "Exits", Order = 1)]
        public int FractalStrength { get; set; }

        [NinjaScriptProperty]
        [Range(10, 5000)]
        [Display(Name = "Fractal lookback (bars)", GroupName = "Exits", Order = 2)]
        public int FractalLookback { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow equal highs/lows as fractal", GroupName = "Exits", Order = 4)]
        public bool AllowEqualFractals { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "Stop buffer (points)", GroupName = "Exits", Order = 3)]
        public double StopBufferPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Max trades per day", GroupName = "Risk", Order = 3)]
        public int MaxTradesPerDay { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Reference time UK (HHMM)", GroupName = "Time (UK)", Order = 0)]
        public int TrackStartHHMM { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Trade start UK (HHMM)", GroupName = "Time (UK)", Order = 1)]
        public int TradeStartHHMM { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Last entry cut-off UK (HHMM)", GroupName = "Time (UK)", Order = 2)]
        public int TradeEndHHMM { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Chart time zone ID (blank = PC local)", GroupName = "Time (UK)", Order = 3)]
        public string ChartTimeZoneId { get; set; }
        #endregion
    }
}