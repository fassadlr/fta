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

// PO3 Range BOS strategy (run on a 5-minute chart)
//
//  1. Fractals: 3-candle swing high/low (1 candle each side).
//     Tracked from 07:00 UK each day; the previous day's fractals are cleared at 07:00.
//  2. BOS: a 5m candle CLOSES beyond the most recent unbroken fractal, and that close sits in a
//     different PO3 dealing range than where the leg started, i.e. the stop fractal
//     (Low = floor(price / po3) * po3). The broken fractal itself may be inside the new range.
//     Each fractal can only trigger once; if a setup is skipped we wait for the next BOS.
//  3. Entry: LIMIT order at the broken fractal price (retest of the break).
//  4. Stop: StopBufferTicks (default 3) beyond the most recent opposite fractal. If an earlier fractal sits just
//     beyond it (within StopPivotTolerance x PO3) and is still unbroken - i.e. it is the true
//     origin of the leg that made this BOS - that swing is used instead. All other checks
//     then use the new stop.
//  5. Target: 1R from the limit price, capped at EQ of the new dealing range (never more than 1R).
//  6. Skip if RR < MinRR (0.9).
//  7. Skip if the leg INTO the stop fractal contains a fair value gap.
//  Session: 08:00-10:00 UK time (handles GMT/BST automatically). New setups only inside it.
//  Pending limit is cancelled if price hits the target before fill, or the session ends.
//  (If the BOS candle itself already traded to the target, the setup is skipped.)
//  Filled positions are never closed on time: they run to stop or target.
//  Max 1 trade per UK day: once an entry fills, no new setups that day.
//  (A limit cancelled unfilled does not count.)

namespace NinjaTrader.NinjaScript.Strategies
{
    public class PO3RangeBOS : Strategy
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
            public double Low, Eq, High;
            public DealingRange(int po3, double price)
            {
                Low  = Math.Floor(price / po3) * po3;
                Eq   = Low + (po3 * 0.50);
                High = Low + po3;
            }
        }

        private List<Swing> swings;
        private Swing lastHigh;
        private Swing lastLow;
        private bool lastHighBroken;
        private bool lastLowBroken;

        // Pending limit tracking
        private Order entryOrder;
        private bool pendingLong;
        private double pendingTarget;
        private int pendingSinceBar;

        private const string LongSignal  = "PO3 Long";
        private const string ShortSignal = "PO3 Short";
        private const int LegLookbackFallback = 100;
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
                Name                                = "PO3RangeBOS";
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

                // FVG filter
                CheckLegIntoStop    = true;
                CheckLegStopToBreak = false;
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
                lastHighBroken = false;
                lastLowBroken = false;
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

            // A fractal is consumed by the first close beyond it, whether we trade it or not.
            bool longBos = false;
            bool shortBos = false;

            if (lastHigh != null && !lastHighBroken && Close[0] > lastHigh.Price)
            {
                lastHighBroken = true;
                longBos = true;
            }
            if (lastLow != null && !lastLowBroken && Close[0] < lastLow.Price)
            {
                lastLowBroken = true;
                shortBos = true;
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
            if (!InSession())
                return;
            if (tradedDate == ToUk(Time[0]).Date)
            {
                if ((longBos && AllowLong) || (shortBos && AllowShort))
                    Log(string.Format("{0} BOS ignored: already traded today", longBos ? "LONG" : "SHORT"));
                return;
            }

            if (longBos && AllowLong)
                TryLong();
            else if (shortBos && AllowShort)
                TryShort();
        }

        #region Setup logic

        private void TryLong()
        {
            Swing broken = lastHigh;
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

            // "Into a new range" = the leg started (stop fractal) in a lower dealing range than the BOS close.
            DealingRange originRange = new DealingRange(Po3, stopSwing.Price);
            DealingRange newRange    = new DealingRange(Po3, Close[0]);

            if (RequireNewRange && newRange.Low <= originRange.Low)
            {
                Skip(string.Format("BOS not into new range (origin {0} = close {1})", originRange.Low, newRange.Low), true);
                return;
            }

            if (FvgOnStopLeg(stopSwing))
            {
                Skip("FVG on stop leg", true);
                return;
            }

            double targetR = entry + risk * TargetR;
            double target  = Instrument.MasterInstrument.RoundToTickSize(Math.Min(targetR, newRange.Eq));
            double rr      = (target - entry) / risk;

            if (rr < MinRR)
            {
                Skip(string.Format("RR {0:0.00} < {1:0.00}", rr, MinRR), true);
                return;
            }

            if (High[0] >= target)
            {
                Skip("target already hit on BOS candle", true);
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
            pendingTarget = target;
            pendingSinceBar = CurrentBar;

            if (ShowDrawings)
            {
                Draw.Text(this, "po3L" + CurrentBar, string.Format("L {3} @ {0} | {1:0.00}R{2}", entry, rr, target >= targetR ? "" : " (EQ)", qty),
                    0, Low[0] - 6 * TickSize, Brushes.LimeGreen);
                Draw.Line(this, "po3Lent" + CurrentBar, false, CurrentBar - broken.Bar, entry, 0, entry, Brushes.LimeGreen, DashStyleHelper.Dash, 1);
                DrawStop(true, stopSwing, stop);
            }
            Log(string.Format("LONG  {5} @ limit {0} stop {1} target {2} (EQ {3}) RR {4:0.00} risk ${6:0}", entry, stop, target, newRange.Eq, rr, qty, qty * risk * Instrument.MasterInstrument.PointValue));
        }

        private void TryShort()
        {
            Swing broken = lastLow;
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

            // "Into a new range" = the leg started (stop fractal) in a higher dealing range than the BOS close.
            DealingRange originRange = new DealingRange(Po3, stopSwing.Price);
            DealingRange newRange    = new DealingRange(Po3, Close[0]);

            if (RequireNewRange && newRange.Low >= originRange.Low)
            {
                Skip(string.Format("BOS not into new range (origin {0} = close {1})", originRange.Low, newRange.Low), false);
                return;
            }

            if (FvgOnStopLeg(stopSwing))
            {
                Skip("FVG on stop leg", false);
                return;
            }

            double targetR = entry - risk * TargetR;
            double target  = Instrument.MasterInstrument.RoundToTickSize(Math.Max(targetR, newRange.Eq));
            double rr      = (entry - target) / risk;

            if (rr < MinRR)
            {
                Skip(string.Format("RR {0:0.00} < {1:0.00}", rr, MinRR), false);
                return;
            }

            if (Low[0] <= target)
            {
                Skip("target already hit on BOS candle", false);
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
            pendingTarget = target;
            pendingSinceBar = CurrentBar;

            if (ShowDrawings)
            {
                Draw.Text(this, "po3S" + CurrentBar, string.Format("S {3} @ {0} | {1:0.00}R{2}", entry, rr, target <= targetR ? "" : " (EQ)", qty),
                    0, High[0] + 6 * TickSize, Brushes.OrangeRed);
                Draw.Line(this, "po3Sent" + CurrentBar, false, CurrentBar - broken.Bar, entry, 0, entry, Brushes.OrangeRed, DashStyleHelper.Dash, 1);
                DrawStop(false, stopSwing, stop);
            }
            Log(string.Format("SHORT {5} @ limit {0} stop {1} target {2} (EQ {3}) RR {4:0.00} risk ${6:0}", entry, stop, target, newRange.Eq, rr, qty, qty * risk * Instrument.MasterInstrument.PointValue));
        }

        // Fixed rules: cancel if price hits the target before fill, or the 08:00-10:00 UK session ends.
        private void ManagePendingLimit()
        {
            string reason = null;

            if (CurrentBar > pendingSinceBar)
            {
                if (pendingLong && High[0] >= pendingTarget)
                    reason = "target reached before fill";
                else if (!pendingLong && Low[0] <= pendingTarget)
                    reason = "target reached before fill";
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
                lastHighBroken = false;
                lastLowBroken = false;
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
                lastHighBroken = false;
                if (ShowDrawings)
                    Draw.TriangleDown(this, "fh" + pivot, false, n, ph + 2 * TickSize, Brushes.IndianRed);
            }
            if (isLow)
            {
                Swing s = new Swing { Price = pl, Bar = pivot, IsHigh = false };
                swings.Add(s);
                lastLow = s;
                lastLowBroken = false;
                if (ShowDrawings)
                    Draw.TriangleUp(this, "fl" + pivot, false, n, pl - 2 * TickSize, Brushes.SteelBlue);
            }

            if (swings.Count > 500)
                swings.RemoveRange(0, swings.Count - 500);
        }

        #endregion

        #region FVG

        private bool FvgOnStopLeg(Swing stopSwing)
        {
            if (CheckLegIntoStop)
            {
                int legStart = LegStart(stopSwing);
                if (legStart >= 0 && HasFvg(legStart, stopSwing.Bar))
                    return true;
            }
            if (CheckLegStopToBreak && HasFvg(stopSwing.Bar, CurrentBar))
                return true;
            return false;
        }

        // Origin of the leg that ran INTO the stop fractal:
        // the extreme opposite price between the previous same-side fractal and the stop fractal.
        private int LegStart(Swing s)
        {
            int prevSame = -1;
            for (int i = swings.Count - 1; i >= 0; i--)
            {
                if (swings[i].IsHigh == s.IsHigh && swings[i].Bar < s.Bar)
                {
                    prevSame = swings[i].Bar;
                    break;
                }
            }

            // No earlier same-side fractal today: look back no further than 07:00 UK.
            int from = prevSame >= 0 ? prevSame : Math.Max(trackingStartBar, s.Bar - LegLookbackFallback);
            int best = -1;
            double bestVal = s.IsHigh ? double.MaxValue : double.MinValue;

            for (int b = from; b < s.Bar; b++)
            {
                if (s.IsHigh)
                {
                    double lo = Bars.GetLow(b);
                    if (lo < bestVal) { bestVal = lo; best = b; }
                }
                else
                {
                    double hi = Bars.GetHigh(b);
                    if (hi > bestVal) { bestVal = hi; best = b; }
                }
            }
            return best;
        }

        // Any 3-candle FVG (bullish or bearish) fully inside [fromBar, toBar].
        private bool HasFvg(int fromBar, int toBar)
        {
            double minGap = Math.Max(MinFvgTicks, 1) * TickSize - TickSize * 0.001;
            for (int i = fromBar + 2; i <= toBar; i++)
            {
                double bullGap = Bars.GetLow(i) - Bars.GetHigh(i - 2);
                double bearGap = Bars.GetLow(i - 2) - Bars.GetHigh(i);
                if (bullGap >= minGap || bearGap >= minGap)
                    return true;
            }
            return false;
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
        [Display(Name = "Skip if FVG on leg INTO stop fractal", Order = 1, GroupName = "4. FVG filter")]
        public bool CheckLegIntoStop { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Skip if FVG on leg stop fractal -> BOS", Order = 2, GroupName = "4. FVG filter")]
        public bool CheckLegStopToBreak { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Min FVG size (ticks)", Order = 3, GroupName = "4. FVG filter")]
        public int MinFvgTicks { get; set; }

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