#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// PO3_London_BOS strategy (run on a 5-minute chart)
//
//  1. Fractals: 3-candle swing high/low (1 candle each side).
//     Tracked from 07:00 UK each day; the previous day's fractals are cleared at 07:00.
//  2. BOS: a 5m candle CLOSES beyond any fractal that has not yet been closed through
//     (not just the most recent one). Each fractal can only trigger once; if several break on
//     the same candle, the most recent of them is the broken fractal.
//     NEW RANGE rule (Low = floor(price / po3) * po3): the stop fractal is in the old range, the
//     BOS close is at least 1 tick into a new range, and it is the first BOS in that direction into
//     that range since the stop fractal formed (or since price last closed back in the old range). If not, the BOS is ignored but the fractal is used up.
//  3. Entry: LIMIT order at the broken fractal price (retest of the break).
//  4. Stop: StopBufferTicks (default 3) beyond the most recent opposite fractal.
//     Skipped if the stop lands in the new range's extension band (long: -0.111 Ext to 0 Low,
//     short: 1 High to 1.111 Ext). If an earlier fractal sits just
//     beyond it (within StopPivotTolerance x PO3) and is still unbroken - i.e. it is the true
//     origin of the leg that made this BOS - that swing is used instead. All other checks
//     then use the new stop.
//  5. Target: 1R from the limit price, capped at EQ of the new dealing range (never more than 1R).
//  6. Skip if RR < MinRR (0.9). EQ scenario (optional): if the target is capped at EQ and RR is
//     between AdjustFloorRR (0.5) and MinRR, move the limit toward the stop until RR = MinRR.
//     1R scenario (optional): if the target is a plain 1R (not capped by EQ), move the limit
//     EntryOffsetPoints (0.5) toward price (up for longs, down for shorts), keeping stop and target,
//     as long as RR stays >= MinRR.
//  7. Skip if the stop leg (stop fractal -> BOS candle) has an FVG in the trade direction
//     (bearish FVG for a short, bullish FVG for a long).
//     Only UNTOUCHED FVGs count: if any later candle has traded into the gap, it is ignored.
//     A gap completed by the BOS candle itself never counts.
//  Session: 08:00-10:00 UK time (handles GMT/BST automatically). New setups only inside it.
//  Late-range scenario (optional): if a BOS candle breaks a fractal but its close is still in the
//  stop's range, wait LateRangeCandles (1) candle(s). If one closes into the new range, run the normal
//  checks then, with the limit at the original broken fractal.
//  07:55 scenario (optional): a BOS on the candle opening 07:55 also counts; its limit goes in
//  at the 08:00 open. All the usual rules still apply.
//  Pending limit is cancelled if price reaches the EQ block before fill (0.47 of the new range for
//  longs, 0.53 for shorts) - a 1R target being traded through does not cancel - or the session ends.
//  (If the BOS candle itself already reached the EQ block, the setup is skipped.)
//  Filled positions are never closed on time: they run to stop or target.
//  Max 1 trade per UK day: once an entry fills, no new setups that day.
//  (A limit cancelled unfilled does not count.)

namespace NinjaTrader.NinjaScript.Strategies.FTA
{
    public class PO3_London_BOS : Strategy
    {
        private class Swing
        {
            public double Price;
            public int Bar;      // absolute bar index
            public bool IsHigh;
        }

        // Mirrors the DealingRange grid (only the levels this strategy uses).
        private class DealingRange
        {
            public double ExtLow, Low, MB2, Eq, MB3, High, ExtHigh;
            public DealingRange(int po3, double price)
            {
                Low  = Math.Floor(price / po3) * po3;
                MB2  = Low + (po3 * 0.47);
                Eq   = Low + (po3 * 0.50);
                MB3  = Low + (po3 * 0.53);
                High = Low + po3;
                ExtLow  = Low - (po3 * 0.111);
                ExtHigh = High + (po3 * 0.111);
            }
        }

        private List<Swing> swings;
        private Swing lastHigh;          // most recent fractal high (stop source for shorts)
        private Swing lastLow;           // most recent fractal low  (stop source for longs)
        private List<Swing> openHighs;   // fractal highs not yet closed above
        private List<Swing> openLows;    // fractal lows  not yet closed below
        private List<int> bullBosBars;   // every long BOS today (traded or not)
        private List<int> bearBosBars;   // every short BOS today (traded or not)

        // Late-range scenario: a BOS that broke a fractal but hasn't closed into the new range yet
        private Swing waitingBroken;
        private bool waitingLong;
        private int waitingBosBar = -1;

        // Pending limit tracking
        private Order entryOrder;
        private bool pendingLong;
        private double pendingCancelLevel;   // price that counts as "TP hit" before fill
        private int pendingSinceBar;

        private const string LongSignal  = "PO3 Long";
        private const string ShortSignal = "PO3 Short";
        private const int FractalStrength = 1;   // 3-candle fractal: 1 candle each side
        private const double TargetR = 1.0;

        // Session window in UK time
        private static readonly TimeSpan SessionStartUk = new TimeSpan(8, 0, 0);
        private static readonly TimeSpan SessionEndUk   = new TimeSpan(10, 0, 0);
        private static readonly TimeSpan TrackFromUk    = new TimeSpan(7, 0, 0);
        private TimeZoneInfo ukTz;

        // Daily fractal tracking window (starts 07:00 UK)
        private DateTime trackingDate = DateTime.MinValue;
        private int trackingStartBar = 0;

        // One trade per UK day: set when an entry fills
        private DateTime tradedDate = DateTime.MinValue;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                                = "PO3_London_BOS";
                Description                         = "5m BOS into a new PO3 dealing range. Limit at broken fractal, stop a few ticks past last fractal, target 1R or range EQ, min RR 0.9, skip if FVG on stop leg.";
                Calculate                           = Calculate.OnBarClose;
                EntriesPerDirection                 = 1;
                EntryHandling                       = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy        = false;   // positions stay open until stop or target
                ExitOnSessionCloseSeconds           = 30;
                IsFillLimitOnTouch                  = false;
                MaximumBarsLookBack                 = MaximumBarsLookBack.Infinite;
                OrderFillResolution                 = OrderFillResolution.Standard;
                Slippage                            = 0;
                StartBehavior                       = StartBehavior.WaitUntilFlat;
                TimeInForce                         = TimeInForce.Gtc;
                TraceOrders                         = false;
                RealtimeErrorHandling               = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling                  = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade                 = 20;
                IsInstantiatedOnEachOptimizationIteration = true;

                // Structure
                RequireNewRange     = true;
                Po3                 = 81;
                StopPivotTolerance  = 0.03;   // fraction of PO3 (0.03 x 81 = 2.43 pts); 0 = off

                // Risk
                MinRR               = 0.9;
                RiskDollars         = 500;
                StopBufferTicks     = 3;
                AdjustEntryForRR    = true;
                AdjustFloorRR       = 0.5;
                OffsetEntryOn1R     = true;
                AllowPreOpenBos     = true;
                SkipStopInExtension = true;
                LateRangeCandles    = 1;
                EntryOffsetPoints   = 0.5;

                // FVG filter
                MinFvgTicks         = 1;

                // Filters
                AllowLong           = true;
                AllowShort          = true;

                // Visuals
                ShowDrawings        = true;
                LogSetups           = true;
            }
            else if (State == State.DataLoaded)
            {
                swings = new List<Swing>();
                lastHigh = null;
                lastLow = null;
                openHighs = new List<Swing>();
                openLows = new List<Swing>();
                bullBosBars = new List<int>();
                bearBosBars = new List<int>();
                entryOrder = null;
                trackingDate = DateTime.MinValue;
                trackingStartBar = 0;
                tradedDate = DateTime.MinValue;
                ukTz = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0)
                return;

            if (CurrentBar < 2 * FractalStrength + 2)
                return;

            DetectFractal();

            // BOS = first close beyond ANY fractal that has not been closed through yet.
            // Every fractal closed through is consumed, whether we trade it or not.
            // If several break at once, the most recent one is the broken fractal (limit price).
            Swing brokenHigh = ConsumeBroken(openHighs, true);
            Swing brokenLow  = ConsumeBroken(openLows, false);
            bool longBos  = brokenHigh != null;
            bool shortBos = brokenLow != null;
            if (longBos)  bullBosBars.Add(CurrentBar);
            if (shortBos) bearBosBars.Add(CurrentBar);

            // Late-range scenario: pick up a BOS that is waiting for a close into the new range.
            // It is dropped if it has expired or if a fresh BOS printed on this candle.
            Swing lateBroken = null;
            bool lateLong = false;
            int lateBosBar = -1;
            if (waitingBroken != null)
            {
                if (CurrentBar - waitingBosBar <= LateRangeCandles && !longBos && !shortBos)
                {
                    lateBroken = waitingBroken;
                    lateLong = waitingLong;
                    lateBosBar = waitingBosBar;
                }
                waitingBroken = null;
            }

            // Manage a working limit order before looking for new setups.
            if (entryOrder != null)
            {
                ManagePendingLimit();
                return;
            }

            if (CurrentBar < BarsRequiredToTrade)
                return;
            if (Position.MarketPosition != MarketPosition.Flat)
                return;
            if (!CanStartSetup())
                return;
            if (tradedDate == ToUk(Time[0]).Date)
            {
                if ((longBos && AllowLong) || (shortBos && AllowShort))
                    Log(string.Format("{0} BOS ignored: already traded today", longBos ? "LONG" : "SHORT"));
                return;
            }

            if (longBos && AllowLong)
                TryLong(brokenHigh, CurrentBar);
            else if (shortBos && AllowShort)
                TryShort(brokenLow, CurrentBar);
            else if (lateBroken != null && lateLong && AllowLong)
                TryLong(lateBroken, lateBosBar);
            else if (lateBroken != null && !lateLong && AllowShort)
                TryShort(lateBroken, lateBosBar);
        }

        // Late-range scenario: if the BOS only failed because its close is still in the stop's range,
        // wait up to LateRangeCandles candles for a close into the new range. Returns true if waiting.
        private bool WaitForLateRange(bool isLong, Swing broken, int bosBar)
        {
            if (LateRangeCandles <= 0 || CurrentBar - bosBar >= LateRangeCandles)
                return false;
            waitingBroken = broken;
            waitingLong = isLong;
            waitingBosBar = bosBar;
            Log(string.Format("{0} BOS of {1} not into new range yet - waiting {2} candle(s) for a close into it",
                isLong ? "LONG" : "SHORT", broken.Price, LateRangeCandles - (CurrentBar - bosBar)));
            return true;
        }

        // Removes every open fractal the current close has gone through; returns the most recent one.
        private Swing ConsumeBroken(List<Swing> open, bool highs)
        {
            Swing mostRecent = null;
            for (int i = open.Count - 1; i >= 0; i--)
            {
                Swing f = open[i];
                bool broken = highs ? Close[0] > f.Price : Close[0] < f.Price;
                if (!broken)
                    continue;
                if (mostRecent == null || f.Bar > mostRecent.Bar)
                    mostRecent = f;
                open.RemoveAt(i);
            }
            return mostRecent;
        }

        // "Into a new range" (all must hold):
        //  1. the stop fractal (where the leg started) is in the OLD range,
        //  2. the BOS close is at least 1 tick into a NEW range (higher for a long, lower for a short),
        //  3. this is the FIRST BOS in this direction into that range since the stop fractal formed,
        //     ignoring earlier BOSs that price has since closed back through into the old range.
        // Returns null if OK, otherwise the reason it failed.
        private string NewRangeFailure(bool isLong, Swing origin, out bool stillInStopRange)
        {
            stillInStopRange = false;
            double closeLow  = RangeLowFor(Close[0], isLong);
            double originLow = RangeLowFor(origin.Price, isLong);

            if (isLong ? closeLow <= originLow : closeLow >= originLow)
            {
                stillInStopRange = true;
                return string.Format("not into new range (stop {0} range {1}, close range {2})", origin.Price, originLow, closeLow);
            }

            // An earlier BOS into this range only blocks us if price has stayed in the new range since.
            // If any candle after it closed back in the old range (below for a long, above for a short),
            // that BOS is superseded and this one counts as a fresh break into the range.
            int resetBar = origin.Bar;
            for (int c = CurrentBar - 1; c > origin.Bar; c--)
            {
                double cLow = RangeLowFor(Bars.GetClose(c), isLong);
                if (isLong ? cLow < closeLow : cLow > closeLow)
                {
                    resetBar = c;
                    break;
                }
            }

            foreach (int b in isLong ? bullBosBars : bearBosBars)
            {
                if (b > resetBar && b < CurrentBar && RangeLowFor(Bars.GetClose(b), isLong) == closeLow)
                    return string.Format("not first BOS into range {0} (earlier BOS on candle opening {1}, price has not closed back in the old range since)", closeLow, OpenTime(b));
            }
            return null;
        }

        // Range a close belongs to for the range-change check. A close EXACTLY on a boundary has not
        // entered the next range - it must be at least 1 tick past it:
        //  long:  a range is (Low, High]  -> a close on the boundary stays in the range below
        //  short: a range is [Low, High)  -> a close on the boundary stays in the range above
        private double RangeLowFor(double price, bool isLong)
        {
            double k = isLong ? Math.Ceiling(price / Po3 - 1e-9) - 1 : Math.Floor(price / Po3 + 1e-9);
            return k * Po3;
        }

        #region Setup logic

        // bosBar = the candle that broke the fractal (CurrentBar, or earlier for a late-range confirmation).
        private void TryLong(Swing broken, int bosBar)
        {
            Swing stopSwing = lastLow;
            if (broken == null || stopSwing == null)
                return;
            stopSwing = RefineStop(stopSwing);

            double entry = Instrument.MasterInstrument.RoundToTickSize(broken.Price);
            double stop  = Instrument.MasterInstrument.RoundToTickSize(stopSwing.Price - StopBufferTicks * TickSize);
            double risk  = entry - stop;

            if (risk <= 0)
            {
                Skip("Stop not below entry", true);
                return;
            }

            DealingRange newRange = new DealingRange(Po3, Close[0]);
            bool stillInStopRange = false;
            string rangeFail = RequireNewRange ? NewRangeFailure(true, stopSwing, out stillInStopRange) : null;
            if (rangeFail != null)
            {
                if (stillInStopRange && WaitForLateRange(true, broken, bosBar))
                    return;
                Skip(rangeFail, true);
                return;
            }
            if (bosBar != CurrentBar)
                Log(string.Format("LONG  BOS of {0} (candle opening {1}) confirmed: closed into new range on candle opening {2}",
                    broken.Price, OpenTime(bosBar), OpenTime(CurrentBar)));

            // Skip if the stop sits in the extension band below the new range (between -0.111 Ext and 0 Low).
            if (SkipStopInExtension && stop >= newRange.ExtLow - 1e-9 && stop <= newRange.Low + 1e-9)
            {
                Skip(string.Format("stop {0} in range extension ({1} - {2})", stop,
                    Instrument.MasterInstrument.RoundToTickSize(newRange.ExtLow), newRange.Low), true);
                return;
            }

            FvgHit fvg = FvgOnStopLeg(stopSwing, true);
            if (fvg != null)
            {
                ReportFvgSkip(fvg, true);
                return;
            }

            double targetR = entry + risk * TargetR;
            double target  = Instrument.MasterInstrument.RoundToTickSize(Math.Min(targetR, newRange.Eq));
            double rr      = (target - entry) / risk;

            // Scenario: target capped at EQ and RR short of MinRR (but >= floor) ->
            // move the limit DOWN toward the stop until EQ is MinRR away. Stop and target unchanged.
            if (rr < MinRR && AdjustEntryForRR && target < targetR && rr >= AdjustFloorRR)
            {
                double raw      = (target + MinRR * stop) / (1 + MinRR);
                double newEntry = Instrument.MasterInstrument.RoundToTickSize(Math.Floor(raw / TickSize + 1e-9) * TickSize);
                if (newEntry > stop && newEntry < entry)
                {
                    Log(string.Format("LONG  limit moved {0} -> {1} (RR {2:0.00} -> {3:0.00} to EQ)",
                        entry, newEntry, rr, (target - newEntry) / (newEntry - stop)));
                    entry    = newEntry;
                    risk     = entry - stop;
                    rr       = (target - entry) / risk;
                }
            }

            // Scenario: target is a plain 1R (below EQ) -> raise the limit by EntryOffsetPoints so a
            // near-miss retest still fills. Stop and target stay put; only applied if RR stays >= MinRR.
            if (rr >= MinRR && OffsetEntryOn1R && EntryOffsetPoints > 0 && targetR < newRange.Eq)
            {
                double newEntry = Instrument.MasterInstrument.RoundToTickSize(entry + EntryOffsetPoints);
                double newRR    = (target - newEntry) / (newEntry - stop);
                if (newRR >= MinRR && newEntry < target)
                {
                    Log(string.Format("LONG  limit offset {0} -> {1} (RR {2:0.00} -> {3:0.00})", entry, newEntry, rr, newRR));
                    entry = newEntry;
                    risk  = entry - stop;
                    rr    = newRR;
                }
                else
                    Log(string.Format("LONG  limit offset skipped: RR would be {0:0.00} < {1:0.00}", newRR, MinRR));
            }

            if (rr < MinRR)
            {
                Skip(string.Format("RR {0:0.00} < {1:0.00}", rr, MinRR), true);
                return;
            }

            // "TP hit" only means price reached the EQ block (0.47 for a long) - that completes the leg.
            // A plain 1R target being traded through before fill does NOT cancel/skip.
            double cancelLevel = Instrument.MasterInstrument.RoundToTickSize(newRange.MB2);
            if (High[0] >= cancelLevel)
            {
                Skip(string.Format("EQ block ({0}) already hit on BOS candle", cancelLevel), true);
                return;
            }

            int qty = PositionSize(risk);
            if (qty < 1)
            {
                Skip(string.Format("1 contract risks ${0:0} > ${1:0}", risk * Instrument.MasterInstrument.PointValue, RiskDollars), true);
                return;
            }

            SetStopLoss(LongSignal, CalculationMode.Price, stop, false);
            SetProfitTarget(LongSignal, CalculationMode.Price, target);
            entryOrder = EnterLongLimit(0, true, qty, entry, LongSignal);

            pendingLong = true;
            pendingCancelLevel = cancelLevel;
            pendingSinceBar = CurrentBar;

            if (ShowDrawings)
            {
                Draw.Line(this, "po3Lent" + CurrentBar, false, CurrentBar - broken.Bar, entry, 0, entry, Brushes.LimeGreen, DashStyleHelper.Dash, 1);
                DrawStop(true, stopSwing, stop);
            }
            Log(string.Format("LONG  {5} @ limit {0} stop {1} target {2} (EQ {3}) RR {4:0.00} risk ${6:0}", entry, stop, target, newRange.Eq, rr, qty, qty * risk * Instrument.MasterInstrument.PointValue));
        }

        private void TryShort(Swing broken, int bosBar)
        {
            Swing stopSwing = lastHigh;
            if (broken == null || stopSwing == null)
                return;
            stopSwing = RefineStop(stopSwing);

            double entry = Instrument.MasterInstrument.RoundToTickSize(broken.Price);
            double stop  = Instrument.MasterInstrument.RoundToTickSize(stopSwing.Price + StopBufferTicks * TickSize);
            double risk  = stop - entry;

            if (risk <= 0)
            {
                Skip("Stop not above entry", false);
                return;
            }

            DealingRange newRange = new DealingRange(Po3, Close[0]);
            bool stillInStopRange = false;
            string rangeFail = RequireNewRange ? NewRangeFailure(false, stopSwing, out stillInStopRange) : null;
            if (rangeFail != null)
            {
                if (stillInStopRange && WaitForLateRange(false, broken, bosBar))
                    return;
                Skip(rangeFail, false);
                return;
            }
            if (bosBar != CurrentBar)
                Log(string.Format("SHORT BOS of {0} (candle opening {1}) confirmed: closed into new range on candle opening {2}",
                    broken.Price, OpenTime(bosBar), OpenTime(CurrentBar)));

            // Skip if the stop sits in the extension band above the new range (between 1 High and 1.111 Ext).
            if (SkipStopInExtension && stop >= newRange.High - 1e-9 && stop <= newRange.ExtHigh + 1e-9)
            {
                Skip(string.Format("stop {0} in range extension ({1} - {2})", stop, newRange.High,
                    Instrument.MasterInstrument.RoundToTickSize(newRange.ExtHigh)), false);
                return;
            }

            FvgHit fvg = FvgOnStopLeg(stopSwing, false);
            if (fvg != null)
            {
                ReportFvgSkip(fvg, false);
                return;
            }

            double targetR = entry - risk * TargetR;
            double target  = Instrument.MasterInstrument.RoundToTickSize(Math.Max(targetR, newRange.Eq));
            double rr      = (entry - target) / risk;

            // Scenario: target capped at EQ and RR short of MinRR (but >= floor) ->
            // move the limit UP toward the stop until EQ is MinRR away. Stop and target unchanged.
            if (rr < MinRR && AdjustEntryForRR && target > targetR && rr >= AdjustFloorRR)
            {
                double raw      = (target + MinRR * stop) / (1 + MinRR);
                double newEntry = Instrument.MasterInstrument.RoundToTickSize(Math.Ceiling(raw / TickSize - 1e-9) * TickSize);
                if (newEntry < stop && newEntry > entry)
                {
                    Log(string.Format("SHORT limit moved {0} -> {1} (RR {2:0.00} -> {3:0.00} to EQ)",
                        entry, newEntry, rr, (newEntry - target) / (stop - newEntry)));
                    entry    = newEntry;
                    risk     = stop - entry;
                    rr       = (entry - target) / risk;
                }
            }

            // Scenario: target is a plain 1R (above EQ) -> lower the limit by EntryOffsetPoints so a
            // near-miss retest still fills. Stop and target stay put; only applied if RR stays >= MinRR.
            if (rr >= MinRR && OffsetEntryOn1R && EntryOffsetPoints > 0 && targetR > newRange.Eq)
            {
                double newEntry = Instrument.MasterInstrument.RoundToTickSize(entry - EntryOffsetPoints);
                double newRR    = (newEntry - target) / (stop - newEntry);
                if (newRR >= MinRR && newEntry > target)
                {
                    Log(string.Format("SHORT limit offset {0} -> {1} (RR {2:0.00} -> {3:0.00})", entry, newEntry, rr, newRR));
                    entry = newEntry;
                    risk  = stop - entry;
                    rr    = newRR;
                }
                else
                    Log(string.Format("SHORT limit offset skipped: RR would be {0:0.00} < {1:0.00}", newRR, MinRR));
            }

            if (rr < MinRR)
            {
                Skip(string.Format("RR {0:0.00} < {1:0.00}", rr, MinRR), false);
                return;
            }

            // "TP hit" only means price reached the EQ block (0.53 for a short) - that completes the leg.
            // A plain 1R target being traded through before fill does NOT cancel/skip.
            double cancelLevel = Instrument.MasterInstrument.RoundToTickSize(newRange.MB3);
            if (Low[0] <= cancelLevel)
            {
                Skip(string.Format("EQ block ({0}) already hit on BOS candle", cancelLevel), false);
                return;
            }

            int qty = PositionSize(risk);
            if (qty < 1)
            {
                Skip(string.Format("1 contract risks ${0:0} > ${1:0}", risk * Instrument.MasterInstrument.PointValue, RiskDollars), false);
                return;
            }

            SetStopLoss(ShortSignal, CalculationMode.Price, stop, false);
            SetProfitTarget(ShortSignal, CalculationMode.Price, target);
            entryOrder = EnterShortLimit(0, true, qty, entry, ShortSignal);

            pendingLong = false;
            pendingCancelLevel = cancelLevel;
            pendingSinceBar = CurrentBar;

            if (ShowDrawings)
            {
                Draw.Line(this, "po3Sent" + CurrentBar, false, CurrentBar - broken.Bar, entry, 0, entry, Brushes.OrangeRed, DashStyleHelper.Dash, 1);
                DrawStop(false, stopSwing, stop);
            }
            Log(string.Format("SHORT {5} @ limit {0} stop {1} target {2} (EQ {3}) RR {4:0.00} risk ${6:0}", entry, stop, target, newRange.Eq, rr, qty, qty * risk * Instrument.MasterInstrument.PointValue));
        }

        // Fixed rules: cancel if price reaches the EQ block before fill (0.47 for longs, 0.53 for
        // shorts, of the new range), or the 08:00-10:00 UK session ends.
        private void ManagePendingLimit()
        {
            string reason = null;

            if (CurrentBar > pendingSinceBar)
            {
                if (pendingLong && High[0] >= pendingCancelLevel)
                    reason = string.Format("EQ block {0} reached before fill", pendingCancelLevel);
                else if (!pendingLong && Low[0] <= pendingCancelLevel)
                    reason = string.Format("EQ block {0} reached before fill", pendingCancelLevel);
            }

            if (reason == null && !InSession())
                reason = "session ended";

            if (reason != null)
            {
                CancelOrder(entryOrder);
                if (ShowDrawings)
                {
                    double y = pendingLong ? Low[0] - 6 * TickSize : High[0] + 6 * TickSize;
                    Draw.Text(this, "po3cxl" + CurrentBar, "cxl: " + reason, 0, y, Brushes.Gray);
                }
                Log("Pending limit cancelled: " + reason);
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string nativeError)
        {
            if (order.Name != LongSignal && order.Name != ShortSignal)
                return;

            entryOrder = order;

            // Any fill (full or partial) uses up today's one trade.
            if (filled > 0)
                tradedDate = ToUk(time).Date;

            if (orderState == OrderState.Filled || orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                entryOrder = null;
        }

        // Red stop line from the stop swing to the BOS candle, labelled with the stop price.
        private void DrawStop(bool isLong, Swing stopSwing, double stop)
        {
            Draw.Line(this, "po3SL" + CurrentBar, false, CurrentBar - stopSwing.Bar, stop, 0, stop,
                Brushes.Red, DashStyleHelper.Solid, 2);

            // Price label sits just past the stop: below the line for longs, above for shorts.
            Draw.Text(this, "po3SLtxt" + CurrentBar, false, stop.ToString(), 0, stop, isLong ? -12 : 12,
                Brushes.Red, new SimpleFont("Arial", 11), TextAlignment.Right,
                Brushes.Transparent, Brushes.Transparent, 0);
        }

        // Skipped setups are only written to the Output window, not drawn on the chart.
        private void Skip(string reason, bool isLong)
        {
            Log(string.Format("{0} BOS skipped: {1}", isLong ? "LONG" : "SHORT", reason));
        }

        #endregion

        #region Stop refinement

        // If an earlier swing sits slightly beyond the selected stop fractal
        // (within StopPivotTolerance x PO3) and is the TRUE origin of the leg that made
        // the current BOS, use it as the stop instead.
        //  - beyond:   lower for a long stop, higher for a short stop
        //  - unbroken: no candle since has traded through it, so it is still the
        //              extreme of the move that has now broken structure
        // If several qualify, the furthest one (the true extreme) is used.
        // Every fractal checked is written to the Output window with the reason.
        private Swing RefineStop(Swing selected)
        {
            if (StopPivotTolerance <= 0)
                return selected;

            double tol = Po3 * StopPivotTolerance + TickSize * 0.001;
            string side = selected.IsHigh ? "high" : "low";
            Swing best = selected;
            int checkedCount = 0;

            for (int i = swings.Count - 1; i >= 0; i--)
            {
                Swing c = swings[i];
                if (c.IsHigh != selected.IsHigh || c.Bar >= selected.Bar)
                    continue;

                double beyond = selected.IsHigh ? c.Price - selected.Price : selected.Price - c.Price;
                string tag = string.Format("  fractal {0} {1} (candle opening {2}): ", side, c.Price, OpenTime(c.Bar));

                if (beyond <= 0)
                    continue;
                if (beyond > tol)
                {
                    if (beyond <= 2 * tol)   // only log near misses, to keep the Output window readable
                        Log(tag + string.Format("{0} pts beyond > tolerance {1:0.00} - ignored", beyond, tol));
                    continue;
                }
                checkedCount++;
                if (!IsUnbroken(c))
                {
                    Log(tag + "already traded through - ignored");
                    continue;
                }

                Log(tag + string.Format("{0} pts beyond, unbroken - QUALIFIES", beyond));
                bool further = selected.IsHigh ? c.Price > best.Price : c.Price < best.Price;
                if (further)
                    best = c;
            }

            if (best != selected)
                Log(string.Format("Stop moved from fractal {0} ({1}) to swing {2} ({3})",
                    selected.Price, OpenTime(selected.Bar), best.Price, OpenTime(best.Bar)));
            else if (checkedCount == 0)
                Log(string.Format("No earlier fractal {0} within {1:0.00} pts of {2} ({3}) - stop unchanged",
                    side, tol, selected.Price, OpenTime(selected.Bar)));

            return best;
        }

        // Candle OPEN time in UK time (NinjaTrader stamps bars with their close time).
        private string OpenTime(int bar)
        {
            return ToUk(Bars.GetTime(bar)).AddMinutes(-BarsPeriod.Value).ToString("HH:mm");
        }

        private bool IsUnbroken(Swing c)
        {
            for (int b = c.Bar + 1; b <= CurrentBar; b++)
            {
                if (c.IsHigh ? Bars.GetHigh(b) > c.Price : Bars.GetLow(b) < c.Price)
                    return false;
            }
            return true;
        }

        #endregion

        #region Fractals

        private void DetectFractal()
        {
            int n = FractalStrength;
            int pivot = CurrentBar - n;

            // Only track fractals whose pivot candle opened at or after 07:00 UK.
            // Bar times are close times, so a 5m candle opening 07:00 closes 07:05 (> 07:00).
            DateTime pivotUk = ToUk(Bars.GetTime(pivot));
            if (pivotUk.TimeOfDay <= TrackFromUk)
                return;

            // First fractal candle of a new UK day: clear the previous day's structure.
            if (pivotUk.Date != trackingDate)
            {
                trackingDate = pivotUk.Date;
                trackingStartBar = FirstBarAfterTrackStart(pivot);
                swings.Clear();
                lastHigh = null;
                lastLow = null;
                openHighs.Clear();
                openLows.Clear();
                bullBosBars.Clear();
                bearBosBars.Clear();
                waitingBroken = null;
            }

            double ph = Bars.GetHigh(pivot);
            double pl = Bars.GetLow(pivot);
            bool isHigh = true;
            bool isLow = true;

            for (int k = 1; k <= n; k++)
            {
                if (!(ph > Bars.GetHigh(pivot - k) && ph > Bars.GetHigh(pivot + k)))
                    isHigh = false;
                if (!(pl < Bars.GetLow(pivot - k) && pl < Bars.GetLow(pivot + k)))
                    isLow = false;
            }

            if (isHigh)
            {
                Swing s = new Swing { Price = ph, Bar = pivot, IsHigh = true };
                swings.Add(s);
                lastHigh = s;
                openHighs.Add(s);
                if (ShowDrawings)
                    Draw.TriangleDown(this, "fh" + pivot, false, n, ph + 2 * TickSize, Brushes.IndianRed);
            }
            if (isLow)
            {
                Swing s = new Swing { Price = pl, Bar = pivot, IsHigh = false };
                swings.Add(s);
                lastLow = s;
                openLows.Add(s);
                if (ShowDrawings)
                    Draw.TriangleUp(this, "fl" + pivot, false, n, pl - 2 * TickSize, Brushes.SteelBlue);
            }

            if (swings.Count > 500)
                swings.RemoveRange(0, swings.Count - 500);
        }

        #endregion

        #region FVG

        private class FvgHit
        {
            public int Bar;         // third candle of the FVG (absolute index)
            public bool Bull;
            public double Top, Bottom;
            public int LegFrom, LegTo;
            public double LegFromPrice, LegToPrice;
        }

        // The stop leg = from the stop fractal to the BOS candle (the move in the trade direction).
        // Only gaps in the trade direction count: bearish FVGs for a short, bullish FVGs for a long.
        // Returns the first such FVG, or null if the leg is clean.
        private FvgHit FvgOnStopLeg(Swing stopSwing, bool isLong)
        {
            // The BOS candle never completes an FVG (it is not a fully formed gap), so the last
            // 3-candle pattern checked ends on the candle BEFORE the BOS. The BOS candle can still
            // touch (and so invalidate) an earlier FVG.
            FvgHit hit = FindFvg(stopSwing.Bar, CurrentBar - 1, isLong);
            if (hit != null)
            {
                hit.LegFrom = stopSwing.Bar;
                hit.LegFromPrice = stopSwing.Price;
                hit.LegTo = CurrentBar;
                hit.LegToPrice = Close[0];
            }
            return hit;
        }

        // Log the FVG and leg, and mark them on the chart: grey dotted line = leg checked,
        // orange box = the gap that caused the skip.
        private void ReportFvgSkip(FvgHit h, bool isLong)
        {
            Log(string.Format("{0} BOS skipped: FVG on stop leg. Leg {1} ({2}) -> {3} ({4}). {5} FVG {6} - {7} ({8} pts) on candles opening {9}-{10}",
                isLong ? "LONG" : "SHORT",
                h.LegFromPrice, OpenTime(h.LegFrom), h.LegToPrice, OpenTime(h.LegTo),
                h.Bull ? "Bullish" : "Bearish", h.Bottom, h.Top, h.Top - h.Bottom,
                OpenTime(h.Bar - 2), OpenTime(h.Bar)));

            if (!ShowDrawings)
                return;

            Draw.Line(this, "po3FvgLeg" + CurrentBar, false, CurrentBar - h.LegFrom, h.LegFromPrice,
                CurrentBar - h.LegTo, h.LegToPrice, Brushes.Gray, DashStyleHelper.Dot, 2);
            Draw.Rectangle(this, "po3Fvg" + CurrentBar, false, CurrentBar - (h.Bar - 2), h.Top,
                CurrentBar - h.Bar, h.Bottom, Brushes.Orange, Brushes.Orange, 40);
        }

        // First UNTOUCHED 3-candle FVG in the given direction fully inside [fromBar, toBar], or null.
        // An FVG only counts if no candle after it (up to and including the BOS candle) has traded
        // back into it, even by a tick. Touched FVGs are logged and ignored.
        private FvgHit FindFvg(int fromBar, int toBar, bool bullish)
        {
            double minGap = Math.Max(MinFvgTicks, 1) * TickSize - TickSize * 0.001;
            for (int i = fromBar + 2; i <= toBar; i++)
            {
                FvgHit hit = null;
                if (bullish)
                {
                    double gap = Bars.GetLow(i) - Bars.GetHigh(i - 2);
                    if (gap >= minGap)
                        hit = new FvgHit { Bar = i, Bull = true, Bottom = Bars.GetHigh(i - 2), Top = Bars.GetLow(i) };
                }
                else
                {
                    double gap = Bars.GetLow(i - 2) - Bars.GetHigh(i);
                    if (gap >= minGap)
                        hit = new FvgHit { Bar = i, Bull = false, Bottom = Bars.GetHigh(i), Top = Bars.GetLow(i - 2) };
                }
                if (hit == null)
                    continue;

                int touchBar = FirstTouch(hit);
                if (touchBar < 0)
                    return hit;

                Log(string.Format("  {0} FVG {1} - {2} (candles opening {3}-{4}) touched at {5} - ignored",
                    hit.Bull ? "Bullish" : "Bearish", hit.Bottom, hit.Top,
                    OpenTime(hit.Bar - 2), OpenTime(hit.Bar), OpenTime(touchBar)));
            }
            return null;
        }

        // First candle after the FVG that trades into it: for a bearish FVG a high reaching its
        // bottom edge, for a bullish FVG a low reaching its top edge. -1 if never touched.
        private int FirstTouch(FvgHit h)
        {
            for (int b = h.Bar + 1; b <= CurrentBar; b++)
            {
                if (h.Bull ? Bars.GetLow(b) <= h.Top : Bars.GetHigh(b) >= h.Bottom)
                    return b;
            }
            return -1;
        }

        #endregion

        #region Helpers

        // Contracts = floor(risk $ / (stop distance in points * $ per point)). Always rounds DOWN,
        // so actual risk never exceeds RiskDollars.
        private int PositionSize(double riskPoints)
        {
            double riskPerContract = riskPoints * Instrument.MasterInstrument.PointValue;
            if (riskPerContract <= 0)
                return 0;
            return (int)Math.Floor(RiskDollars / riskPerContract + 1e-9);
        }

        // Bar times are CLOSE times in NinjaTrader's configured time zone; convert to UK time.
        private DateTime ToUk(DateTime t)
        {
            return TimeZoneInfo.ConvertTime(t, Core.Globals.GeneralOptions.TimeZoneInfo, ukTz);
        }

        // In session = bars closing 08:05 .. 09:55 UK. The bar closing at 10:00 ends the session.
        private bool InSession()
        {
            TimeSpan t = ToUk(Time[0]).TimeOfDay;
            return t > SessionStartUk && t < SessionEndUk;
        }

        // New setups: any candle inside the session, plus (optionally) the 07:55 candle, which closes
        // at 08:00 exactly - its limit order goes in at the 08:00 open.
        private bool CanStartSetup()
        {
            if (InSession())
                return true;
            return AllowPreOpenBos && ToUk(Time[0]).TimeOfDay == SessionStartUk;
        }

        // Earliest bar on the same UK day as 'bar' that opened at or after 07:00 UK.
        private int FirstBarAfterTrackStart(int bar)
        {
            DateTime day = ToUk(Bars.GetTime(bar)).Date;
            int b = bar;
            while (b > 0)
            {
                DateTime prevUk = ToUk(Bars.GetTime(b - 1));
                if (prevUk.Date != day || prevUk.TimeOfDay <= TrackFromUk)
                    break;
                b--;
            }
            return b;
        }

        private void Log(string msg)
        {
            if (LogSetups)
                Print(string.Format("{0} | {1}", Time[0], msg));
        }

        #endregion

        #region Properties

        [NinjaScriptProperty]
        [Display(Name = "Require BOS into new range", Order = 2, GroupName = "1. Structure")]
        public bool RequireNewRange { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name = "PO3 (dealing range size)", Order = 3, GroupName = "1. Structure")]
        public int Po3 { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1)]
        [Display(Name = "Structural stop tolerance (fraction of PO3, 0 = off)", Order = 4, GroupName = "1. Structure")]
        public double StopPivotTolerance { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Skip if stop is in range extension", Order = 1, GroupName = "2. Risk")]
        public bool SkipStopInExtension { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Min RR", Order = 2, GroupName = "2. Risk")]
        public double MinRR { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name = "Risk per trade ($)", Order = 3, GroupName = "2. Risk")]
        public double RiskDollars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "Stop buffer beyond fractal (ticks)", Order = 4, GroupName = "2. Risk")]
        public int StopBufferTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EQ scenario: move limit to reach Min RR", Order = 5, GroupName = "2. Risk")]
        public bool AdjustEntryForRR { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "EQ scenario: only if original RR >=", Order = 6, GroupName = "2. Risk")]
        public double AdjustFloorRR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "1R scenario: offset limit toward price", Order = 7, GroupName = "2. Risk")]
        public bool OffsetEntryOn1R { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "1R scenario: entry offset (points)", Order = 8, GroupName = "2. Risk")]
        public double EntryOffsetPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Min FVG size (ticks)", Order = 3, GroupName = "4. FVG filter")]
        public int MinFvgTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "07:55 scenario: allow BOS on the 07:55 candle", Order = 3, GroupName = "5. Filters")]
        public bool AllowPreOpenBos { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "Late-range scenario: candles to wait for close into new range (0 = off)", Order = 4, GroupName = "5. Filters")]
        public int LateRangeCandles { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow longs", Order = 1, GroupName = "5. Filters")]
        public bool AllowLong { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow shorts", Order = 2, GroupName = "5. Filters")]
        public bool AllowShort { get; set; }

        [Display(Name = "Show drawings", Order = 1, GroupName = "6. Visuals")]
        public bool ShowDrawings { get; set; }

        [Display(Name = "Log setups to Output window", Order = 2, GroupName = "6. Visuals")]
        public bool LogSetups { get; set; }

        #endregion
    }
}