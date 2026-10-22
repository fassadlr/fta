#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

#endregion

// PO3 81 London Open strategy - MNQ 1 minute
namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public class PO3_81_LondonOpen : AlgoPO3Base
    {
        #region Properties

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
        [Display(Name = "Min RR (all trades)", GroupName = "Risk", Order = 2)]
        public double MinRR { get; set; }

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
        [Range(0, 30)]
        [Display(Name = "Skip first candles after trade start", GroupName = "Time (UK)", Order = 4)]
        public int SkipOpenCandles { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Chart time zone ID (blank = PC local)", GroupName = "Time (UK)", Order = 3)]
        public string ChartTimeZoneId { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "Re-entry confirmation points", GroupName = "Parameters", Order = 0)]
        public double ReentryConfirmPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Fractal strength", GroupName = "Parameters", Order = 1)]
        public int FractalStrength { get; set; }

        [NinjaScriptProperty]
        [Range(10, 5000)]
        [Display(Name = "Fractal lookback (bars)", GroupName = "Parameters", Order = 2)]
        public int FractalLookback { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow equal highs/lows as fractal", GroupName = "Parameters", Order = 3)]
        public bool AllowEqualFractals { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "Stop buffer", GroupName = "Parameters", Order = 3)]
        public double StopBufferPoints { get; set; }

        internal override List<(double Min, double Max)> LongBuckets => throw new NotImplementedException();

        internal override List<(double Min, double Max)> LongEdgeBuckets => throw new NotImplementedException();

        internal override List<(double Min, double Max)> ShortBuckets => throw new NotImplementedException();

        internal override List<(double Min, double Max)> ShortEdgeBuckets => throw new NotImplementedException();
        #endregion

        #region State

        // Setup phases
        private const int PhaseWaitingForRangeBreak = 0;   // waiting for price to enter a new range
        private const int PhaseInsideArmedRange = 1;        // price is inside the armed range, waiting for it to leave
        private const int PhaseWaitingForReentry = 2;       // price left the armed range, waiting for a confirmed re-entry

        private TimeZoneInfo chartTimeZone, ukTimeZone;
        private int barMinutes = 1;                         // minutes per bar (1 on the 1-minute chart)

        private int lastProcessedDayKey = -1;
        private bool referenceRangeSet;
        private bool londonOpenEvaluated;
        private int tradesTakenToday;

        private int referenceRangeIndex;                    // range we are "moving from" (07:00 range, or reset after a spent leg)
        private int setupPhase;
        private int armedRangeIndex;
        private int armedDirection;                         // 1 = bullish (long), -1 = bearish (short), 0 = none

        private int previousRangeIndex;                     // continuous range tracking
        private int lastRangeChangeDirection;               // direction of the most recent range change
        private double highSinceRangeEntry;                 // high since price last entered the current range
        private double lowSinceRangeEntry;                  // low since price last entered the current range

        private int preOpenRangeIndex;                      // range price was in at the close of the last candle before the London open
        private bool preOpenSnapshotTaken;

        private bool wasInPositionLastBar;
        private bool lastExitWasTarget;

        private string lastLoggedState = "";
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

                PO3 = 81;

                RiskPerTrade = 200;
                MaxContracts = 50;
                MinRR = 0.5;
                EqAdjust = 0;
                FractalStrength = 1;
                FractalLookback = 100;
                StopBufferPoints = 3;
                MaxTradesPerDay = 3;
                ReentryConfirmPoints = 1;
                DebugLogEnabled = true;
                AllowEqualFractals = false;
                TrackStartHHMM = 700;
                TradeStartHHMM = 800;
                TradeEndHHMM = 955;
                ChartTimeZoneId = "";
                SkipOpenCandles = 1;

                ImportFile = string.Empty;
            }
            else if (State == State.Configure)
            {
                Events = NewsEvents.Events();
            }
            else if (State == State.DataLoaded)
            {
                ukTimeZone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); // UK incl. BST
                chartTimeZone = string.IsNullOrEmpty(ChartTimeZoneId) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(ChartTimeZoneId);
                barMinutes = BarsPeriod.BarsPeriodType == BarsPeriodType.Minute ? Math.Max(1, BarsPeriod.Value) : 1;
            }
        }

        private DateTime ToUkTime(DateTime chartTime)
        {
            return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(chartTime, DateTimeKind.Unspecified), chartTimeZone, ukTimeZone);
        }

        private static TimeSpan HhmmToTimeSpan(int hhmm) { return new TimeSpan(hhmm / 100, hhmm % 100, 0); }

        private int GetRangeIndex(double price) { return (int)Math.Floor(price / PO3); }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order == null || execution.Order.OrderState != OrderState.Filled) return;
            if (execution.Order.Name == "Profit target") lastExitWasTarget = true;
            else if (execution.Order.Name == "Stop loss") lastExitWasTarget = false;
        }

        private void Log(string message)
        {
            if (DebugLogEnabled)
                Print(ToUkTime(Time[0]).ToString("yyyy-MM-dd HH:mm") + " UK | " + message);
        }

        protected override void OnBarUpdate()
        {
            RunStrategyLogic();

            if (!DebugLogEnabled || !referenceRangeSet || CurrentBar < BarsRequiredToTrade)
                return;

            TimeSpan barTimeOfDay = ToUkTime(Time[0]).TimeOfDay;

            if (barTimeOfDay < HhmmToTimeSpan(TrackStartHHMM) || barTimeOfDay > HhmmToTimeSpan(TradeEndHHMM))
                return;

            int loggedRangeIndex = GetRangeIndex(Close[0]);

            string stateText = "range " + (loggedRangeIndex * PO3) + " | ref " + (referenceRangeIndex * PO3) + " | phase " + setupPhase +
                               " | armed " + (setupPhase == PhaseWaitingForRangeBreak ? "-" : (armedRangeIndex * PO3).ToString()) + " dir " + armedDirection +
                               " | lastDir " + lastRangeChangeDirection + " | pos " + Position.MarketPosition + " | trades " + tradesTakenToday;

            if (stateText != lastLoggedState)
            {
                Log("close " + Close[0] + " | " + stateText);
                lastLoggedState = stateText;
            }
        }

        private void RunStrategyLogic()
        {
            if (CurrentBar < BarsRequiredToTrade) return;

            DateTime ukTime = ToUkTime(Time[0]);
            int todayDayKey = ukTime.Year * 10000 + ukTime.Month * 100 + ukTime.Day;

            // New UK day: clear everything from the previous day
            if (todayDayKey != lastProcessedDayKey)
            {
                lastProcessedDayKey = todayDayKey;
                referenceRangeSet = false;
                londonOpenEvaluated = false;
                setupPhase = PhaseWaitingForRangeBreak;
                armedRangeIndex = 0;
                armedDirection = 0;
                tradesTakenToday = 0;
                referenceRangeIndex = 0;
                previousRangeIndex = 0;
                lastRangeChangeDirection = 0;
                wasInPositionLastBar = false;
                lastExitWasTarget = false;
                preOpenRangeIndex = 0;
                preOpenSnapshotTaken = false;
                lastLoggedState = "";
                TradingHalted = false;   // halt is per day: reset at the start of each new session
            }

            if (TradingHalted)
                return;

            // Skip all day events (easter etc).
            var eventForToday = GetNewsEventForToday();
            if (eventForToday != null &&
                eventForToday.EventTime.Date == Time[0].Date &&
                eventForToday.AllDay && Position.MarketPosition == MarketPosition.Flat)
            {
                HaltTrading($"[TRADING_HALTED] Trading skipped for day due to all day event {eventForToday.EventTime}");
                return;
            }

            TimeSpan ukTimeOfDay = ukTime.TimeOfDay;

            // No new trades from the cut-off time onwards. Open trades are left to hit their stop or target.
            if (ukTimeOfDay >= HhmmToTimeSpan(TradeEndHHMM)) return;

            // Reference range at 07:00 UK - the first range, where we are moving from
            if (!referenceRangeSet && ukTimeOfDay >= HhmmToTimeSpan(TrackStartHHMM))
            {
                referenceRangeIndex = GetRangeIndex(Open[0]);
                previousRangeIndex = referenceRangeIndex;
                lastRangeChangeDirection = 0;
                highSinceRangeEntry = High[0];
                lowSinceRangeEntry = Low[0];
                referenceRangeSet = true;
            }
            if (!referenceRangeSet) return;

            // Track every range change from 07:00 onwards (multiple ranges, always know where we came from)
            int currentRangeIndex = GetRangeIndex(Close[0]);
            if (currentRangeIndex != previousRangeIndex)
            {
                lastRangeChangeDirection = currentRangeIndex > previousRangeIndex ? 1 : -1;
                previousRangeIndex = currentRangeIndex;
                highSinceRangeEntry = High[0];
                lowSinceRangeEntry = Low[0];
            }
            else
            {
                highSinceRangeEntry = Math.Max(highSinceRangeEntry, High[0]);
                lowSinceRangeEntry = Math.Min(lowSinceRangeEntry, Low[0]);
            }

            // Remember which range price was in just before the London open (close of the last pre-open candle)
            if (!preOpenSnapshotTaken && ukTimeOfDay >= HhmmToTimeSpan(TradeStartHHMM))
            {
                preOpenRangeIndex = currentRangeIndex;
                preOpenSnapshotTaken = true;
            }

            // Bar times are bar CLOSE times, and an order sent on a bar close fills on the next bar's open.
            // Skipping N candles means no decision is made until the first N candles of the session have fully closed,
            // so nothing can be entered at the open itself. Range tracking above still includes those candles.
            TimeSpan firstDecisionTime = HhmmToTimeSpan(TradeStartHHMM) + TimeSpan.FromMinutes(SkipOpenCandles * barMinutes);
            if (ukTimeOfDay < firstDecisionTime) return;

            bool isInPosition = Position.MarketPosition != MarketPosition.Flat;

            // Trade just closed (stop or target)
            if (wasInPositionLastBar && !isInPosition && londonOpenEvaluated)
            {
                if (lastExitWasTarget)
                {
                    // Target hit: that leg is spent. Same range cannot be re-armed - wait for a NEW range break from here.
                    referenceRangeIndex = currentRangeIndex;
                    setupPhase = PhaseWaitingForRangeBreak;
                    Log("Target hit - leg invalid, waiting for new range break from " + (currentRangeIndex * PO3));
                }
                else if (lastRangeChangeDirection != 0)
                {
                    // Stopped out: arm the CURRENT range using the direction we entered it from
                    armedRangeIndex = currentRangeIndex;
                    armedDirection = lastRangeChangeDirection;
                    setupPhase = PhaseInsideArmedRange;
                }
                else
                {
                    referenceRangeIndex = currentRangeIndex;
                    setupPhase = PhaseWaitingForRangeBreak;
                }
                lastExitWasTarget = false;
                wasInPositionLastBar = false;
                return;
            }
            wasInPositionLastBar = isInPosition;

            bool canTakeNewTrade = !isInPosition && tradesTakenToday < MaxTradesPerDay;

            // ---- London open: evaluate once ----
            if (!londonOpenEvaluated)
            {
                if (!canTakeNewTrade) return;
                londonOpenEvaluated = true;

                if (currentRangeIndex != preOpenRangeIndex)
                {
                    // The skipped open candle(s) moved price into a different range: that is the arming event.
                    // No immediate entry - the range must be left and re-entered first.
                    int openCandleDirection = currentRangeIndex > preOpenRangeIndex ? 1 : -1;
                    DealingRange enteredRange = new DealingRange(PO3, currentRangeIndex * PO3 + PO3 / 2.0);
                    double enteredLegTargetPrice = openCandleDirection > 0 ? enteredRange.Eq - EqAdjust : enteredRange.Eq + EqAdjust;
                    bool enteredLegTargetReached = openCandleDirection > 0 ? highSinceRangeEntry >= enteredLegTargetPrice : lowSinceRangeEntry <= enteredLegTargetPrice;

                    if (enteredLegTargetReached)
                    {
                        Log("London open: open candle entered range " + (currentRangeIndex * PO3) + " and already reached target level " + enteredLegTargetPrice.ToString("F2") + " - waiting for new range break");
                        referenceRangeIndex = currentRangeIndex;
                        setupPhase = PhaseWaitingForRangeBreak;
                    }
                    else
                    {
                        Log("London open: open candle entered range " + (currentRangeIndex * PO3) + " (" + (openCandleDirection > 0 ? "bullish" : "bearish") + ") - armed, waiting for leave and re-entry");
                        armedRangeIndex = currentRangeIndex;
                        armedDirection = openCandleDirection;
                        setupPhase = PhaseInsideArmedRange;
                    }
                }
                else if (currentRangeIndex != referenceRangeIndex)
                {
                    // Scenario 1: new range entered since 07:00
                    int openDirection = currentRangeIndex > referenceRangeIndex ? 1 : -1;
                    DealingRange openRange = new DealingRange(PO3, currentRangeIndex * PO3 + PO3 / 2.0);
                    double openLegTargetPrice = openDirection > 0 ? openRange.Eq - EqAdjust : openRange.Eq + EqAdjust;
                    bool openLegTargetAlreadyReached = openDirection > 0 ? highSinceRangeEntry >= openLegTargetPrice : lowSinceRangeEntry <= openLegTargetPrice;

                    if (openLegTargetAlreadyReached)
                    {
                        // The 07:00-08:00 leg already ran to its target level: no trade, wait for a new range break
                        Log("London open: leg already reached target level " + openLegTargetPrice.ToString("F2") + " - no trade, waiting for new range break from " + (currentRangeIndex * PO3));
                        referenceRangeIndex = currentRangeIndex;
                        setupPhase = PhaseWaitingForRangeBreak;
                    }
                    else if (!TryEnter(openDirection, currentRangeIndex))
                    {
                        // Rejected (RR too low / no valid stop) but target not reached: leg still valid for a leave and re-entry
                        Log("London open: entry rejected, target not reached - range " + (currentRangeIndex * PO3) + " armed, waiting for leave and re-entry");
                        armedRangeIndex = currentRangeIndex;
                        armedDirection = openDirection;
                        setupPhase = PhaseInsideArmedRange;
                    }
                }
                else
                {
                    referenceRangeIndex = currentRangeIndex;
                    setupPhase = PhaseWaitingForRangeBreak;
                }
                return;
            }

            // ---- Scenario 2: enter -> leave -> re-enter (runs even while in a trade, so range changes are never missed) ----
            if (setupPhase == PhaseWaitingForRangeBreak)
            {
                if (currentRangeIndex != referenceRangeIndex)
                {
                    armedRangeIndex = currentRangeIndex;
                    armedDirection = currentRangeIndex > referenceRangeIndex ? 1 : -1;
                    setupPhase = PhaseInsideArmedRange;
                }
            }
            else if (setupPhase == PhaseInsideArmedRange)
            {
                // Entered the armed range and ran straight to the target level without a re-entry: leg is invalid
                DealingRange armedRange = new DealingRange(PO3, armedRangeIndex * PO3 + PO3 / 2.0);
                double armedLegTargetPrice = armedDirection > 0 ? armedRange.Eq - EqAdjust : armedRange.Eq + EqAdjust;
                bool armedLegTargetReached = armedDirection > 0 ? High[0] >= armedLegTargetPrice : Low[0] <= armedLegTargetPrice;
                if (armedLegTargetReached && (currentRangeIndex - armedRangeIndex) * armedDirection <= 0)
                {
                    referenceRangeIndex = currentRangeIndex;
                    setupPhase = PhaseWaitingForRangeBreak;
                    Log("Price reached target level " + armedLegTargetPrice.ToString("F2") + " before re-entry - leg invalid, waiting for new range break from " + (currentRangeIndex * PO3));
                    return;
                }

                if (currentRangeIndex == armedRangeIndex) return;
                if ((currentRangeIndex - armedRangeIndex) * armedDirection > 0) armedRangeIndex = currentRangeIndex;   // pushed on into a further range
                else setupPhase = PhaseWaitingForReentry;                                                               // fell back out of armed range
            }
            else if (setupPhase == PhaseWaitingForReentry)
            {
                if (currentRangeIndex == armedRangeIndex)
                {
                    // Re-entry must close at least ReentryConfirmPoints inside the range
                    double armedRangeLow = armedRangeIndex * PO3;
                    double armedRangeHigh = armedRangeLow + PO3;
                    double closeDepthInsideRange = armedDirection > 0 ? Close[0] - armedRangeLow : armedRangeHigh - Close[0];
                    if (closeDepthInsideRange < ReentryConfirmPoints)
                    {
                        Log("Re-entry close only " + closeDepthInsideRange.ToString("F2") + " pts inside range (need " + ReentryConfirmPoints + ") - waiting");
                        return;
                    }

                    if (!canTakeNewTrade) { Log("Re-entry trigger ignored: position open or trade limit reached"); return; }   // keep waiting until flat
                    if (TryEnter(armedDirection, armedRangeIndex)) return;
                    setupPhase = PhaseInsideArmedRange;   // could not take it (no stop / RR / size) - keep watching
                }
                else if ((currentRangeIndex - armedRangeIndex) * armedDirection > 0)
                {
                    armedRangeIndex = currentRangeIndex;
                    setupPhase = PhaseInsideArmedRange;   // moved on into a further range
                }
                else if ((currentRangeIndex - GetRangeIndexArmedFrom()) * armedDirection < 0)
                {
                    // price went through to the opposite side - new range in the other direction
                    armedRangeIndex = currentRangeIndex;
                    armedDirection = -armedDirection;
                    setupPhase = PhaseInsideArmedRange;
                }
            }
        }

        // The range we armed from is one step back from the armed range
        private int GetRangeIndexArmedFrom() { return armedRangeIndex - armedDirection; }

        private bool TryEnter(int direction, int rangeIndex)
        {
            DealingRange dealingRange = new DealingRange(PO3, rangeIndex * PO3 + PO3 / 2.0);
            double entryPrice = Close[0];
            double stopPrice, targetPrice, rewardPoints, riskPoints;
            int stopFractalBarsAgo;
            string stopFractalText;

            if (direction > 0)
            {
                double fractalLowPrice = FindFractalLow(dealingRange.ExtLow, out stopFractalBarsAgo);
                if (double.IsNaN(fractalLowPrice)) { Log("No entry: no fractal low below ExtLow " + dealingRange.ExtLow.ToString("F2") + " in lookback"); return false; }
                stopFractalText = fractalLowPrice.ToString("F2") + " from " + ToUkTime(Time[stopFractalBarsAgo]).ToString("HH:mm") + " UK (" + stopFractalBarsAgo + " bars ago, ExtLow " + dealingRange.ExtLow.ToString("F2") + ")";
                stopPrice = fractalLowPrice - StopBufferPoints;
                targetPrice = dealingRange.Eq - EqAdjust;
                rewardPoints = targetPrice - entryPrice;
                riskPoints = entryPrice - stopPrice;
            }
            else
            {
                double fractalHighPrice = FindFractalHigh(dealingRange.ExtHigh, out stopFractalBarsAgo);
                if (double.IsNaN(fractalHighPrice)) { Log("No entry: no fractal high above ExtHigh " + dealingRange.ExtHigh.ToString("F2") + " in lookback"); return false; }
                stopFractalText = fractalHighPrice.ToString("F2") + " from " + ToUkTime(Time[stopFractalBarsAgo]).ToString("HH:mm") + " UK (" + stopFractalBarsAgo + " bars ago, ExtHigh " + dealingRange.ExtHigh.ToString("F2") + ")";
                stopPrice = fractalHighPrice + StopBufferPoints;
                targetPrice = dealingRange.Eq + EqAdjust;
                rewardPoints = entryPrice - targetPrice;
                riskPoints = stopPrice - entryPrice;
            }

            if (rewardPoints <= 0 || riskPoints <= 0)
            {
                Log("No entry: reward " + rewardPoints.ToString("F2") + " risk " + riskPoints.ToString("F2") + " (stop " + stopPrice.ToString("F2") + ", tp " + targetPrice.ToString("F2") + ")");
                return false;
            }
            if (rewardPoints / riskPoints < MinRR)
            {
                Log("No entry: RR " + (rewardPoints / riskPoints).ToString("F2") + " < " + MinRR + " (stop " + stopPrice.ToString("F2") + ", tp " + targetPrice.ToString("F2") + ")");
                return false;
            }

            int contracts = (int)Math.Floor(RiskPerTrade / (riskPoints * Instrument.MasterInstrument.PointValue));
            contracts = Math.Min(contracts, MaxContracts);
            if (contracts < 1)
            {
                Log("No entry: stop distance " + riskPoints.ToString("F2") + " pts (stop " + stopPrice.ToString("F2") + ") too wide for risk of " + RiskPerTrade + " -> 0 contracts");
                return false;
            }

            stopPrice = Instrument.MasterInstrument.RoundToTickSize(stopPrice);
            targetPrice = Instrument.MasterInstrument.RoundToTickSize(targetPrice);

            if (direction > 0)
            {
                SetStopLoss("Long", CalculationMode.Price, stopPrice, false);
                SetProfitTarget("Long", CalculationMode.Price, targetPrice);
                EnterLong(contracts, "Long");
            }
            else
            {
                SetStopLoss("Short", CalculationMode.Price, stopPrice, false);
                SetProfitTarget("Short", CalculationMode.Price, targetPrice);
                EnterShort(contracts, "Short");
            }

            tradesTakenToday++;
            Log((direction > 0 ? "ENTER LONG " : "ENTER SHORT ") + "qty " + contracts + " entry~" + entryPrice + " stop " + stopPrice + " tp " + targetPrice + " RR " + (rewardPoints / riskPoints).ToString("F2") + " | stop fractal " + stopFractalText);
            return true;
        }

        // Most recent strict fractal low below 'belowPrice' (stop for longs).
        // Equal-low pivots (a low that ties a neighbour) cannot be the stop themselves, but they act as a floor:
        // a strict fractal older than one is only valid if its low is LOWER than that equal-low pivot.
        private double FindFractalLow(double belowPrice, out int fractalBarsAgo)
        {
            fractalBarsAgo = -1;
            double equalPivotFloor = double.MaxValue;   // lowest equal-low pivot seen so far (more recent bars)
            int maxBarsAgo = Math.Min(FractalLookback, CurrentBar - FractalStrength);
            for (int barsAgo = FractalStrength; barsAgo <= maxBarsAgo; barsAgo++)
            {
                double candidateLow = Low[barsAgo];
                bool isStrictFractal = true;
                bool isEqualPivot = true;   // lows are >= neighbours' lows being allowed to tie
                for (int neighbourOffset = 1; neighbourOffset <= FractalStrength; neighbourOffset++)
                {
                    double olderLow = Low[barsAgo + neighbourOffset];
                    double newerLow = Low[barsAgo - neighbourOffset];
                    if (candidateLow >= newerLow || candidateLow >= olderLow) isStrictFractal = false;
                    if (candidateLow > newerLow || candidateLow > olderLow) isEqualPivot = false;
                }

                if (AllowEqualFractals)
                {
                    if (isEqualPivot && candidateLow < belowPrice) { fractalBarsAgo = barsAgo; return candidateLow; }
                    continue;
                }

                if (isStrictFractal)
                {
                    if (candidateLow < belowPrice && candidateLow < equalPivotFloor) { fractalBarsAgo = barsAgo; return candidateLow; }
                }
                else if (isEqualPivot)
                {
                    equalPivotFloor = Math.Min(equalPivotFloor, candidateLow);
                }
            }
            return double.NaN;
        }

        // Most recent strict fractal high above 'abovePrice' (stop for shorts).
        // Equal-high pivots (a high that ties a neighbour) cannot be the stop themselves, but they act as a floor:
        // a strict fractal older than one is only valid if its high is HIGHER than that equal-high pivot.
        private double FindFractalHigh(double abovePrice, out int fractalBarsAgo)
        {
            fractalBarsAgo = -1;
            double equalPivotCeiling = double.MinValue;   // highest equal-high pivot seen so far (more recent bars)
            int maxBarsAgo = Math.Min(FractalLookback, CurrentBar - FractalStrength);
            for (int barsAgo = FractalStrength; barsAgo <= maxBarsAgo; barsAgo++)
            {
                double candidateHigh = High[barsAgo];
                bool isStrictFractal = true;
                bool isEqualPivot = true;
                for (int neighbourOffset = 1; neighbourOffset <= FractalStrength; neighbourOffset++)
                {
                    double olderHigh = High[barsAgo + neighbourOffset];
                    double newerHigh = High[barsAgo - neighbourOffset];
                    if (candidateHigh <= newerHigh || candidateHigh <= olderHigh) isStrictFractal = false;
                    if (candidateHigh < newerHigh || candidateHigh < olderHigh) isEqualPivot = false;
                }

                if (AllowEqualFractals)
                {
                    if (isEqualPivot && candidateHigh > abovePrice) { fractalBarsAgo = barsAgo; return candidateHigh; }
                    continue;
                }

                if (isStrictFractal)
                {
                    if (candidateHigh > abovePrice && candidateHigh > equalPivotCeiling) { fractalBarsAgo = barsAgo; return candidateHigh; }
                }
                else if (isEqualPivot)
                {
                    equalPivotCeiling = Math.Max(equalPivotCeiling, candidateHigh);
                }
            }
            return double.NaN;
        }
    }
}