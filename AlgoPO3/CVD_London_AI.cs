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
        // Setup phases
        private const int PhaseWaitingForRangeBreak = 0;   // waiting for price to enter a new range
        private const int PhaseInsideArmedRange = 1;        // price is inside the armed range, waiting for it to leave
        private const int PhaseWaitingForReentry = 2;       // price left the armed range, waiting for a confirmed re-entry

        private TimeZoneInfo chartTimeZone, ukTimeZone;

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

                Po3Size = 81;
                RiskPerTrade = 200;
                MaxContracts = 50;
                MinRR = 0.5;
                EqOffsetPoints = 1;
                FractalStrength = 2;
                FractalLookback = 100;
                StopBufferPoints = 3;
                MaxTradesPerDay = 5;
                ReentryConfirmPoints = 1;
                EnableDebug = true;
                AllowEqualFractals = false;
                TrackStartHHMM = 700;
                TradeStartHHMM = 800;
                TradeEndHHMM = 955;
                ChartTimeZoneId = "";
            }
            else if (State == State.DataLoaded)
            {
                ukTimeZone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); // UK incl. BST
                chartTimeZone = string.IsNullOrEmpty(ChartTimeZoneId) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(ChartTimeZoneId);
            }
        }

        private DateTime ToUkTime(DateTime chartTime)
        {
            return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(chartTime, DateTimeKind.Unspecified), chartTimeZone, ukTimeZone);
        }

        private static TimeSpan HhmmToTimeSpan(int hhmm) { return new TimeSpan(hhmm / 100, hhmm % 100, 0); }

        private int GetRangeIndex(double price) { return (int)Math.Floor(price / Po3Size); }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution.Order == null || execution.Order.OrderState != OrderState.Filled) return;
            if (execution.Order.Name == "Profit target") lastExitWasTarget = true;
            else if (execution.Order.Name == "Stop loss") lastExitWasTarget = false;
        }

        private void Log(string message)
        {
            if (EnableDebug) Print(ToUkTime(Time[0]).ToString("yyyy-MM-dd HH:mm") + " UK | " + message);
        }

        protected override void OnBarUpdate()
        {
            RunStrategyLogic();

            if (!EnableDebug || !referenceRangeSet || CurrentBar < BarsRequiredToTrade) return;
            TimeSpan barTimeOfDay = ToUkTime(Time[0]).TimeOfDay;
            if (barTimeOfDay < HhmmToTimeSpan(TrackStartHHMM) || barTimeOfDay > HhmmToTimeSpan(TradeEndHHMM)) return;

            int loggedRangeIndex = GetRangeIndex(Close[0]);
            string stateText = "range " + (loggedRangeIndex * Po3Size) + " | ref " + (referenceRangeIndex * Po3Size) + " | phase " + setupPhase +
                               " | armed " + (setupPhase == PhaseWaitingForRangeBreak ? "-" : (armedRangeIndex * Po3Size).ToString()) + " dir " + armedDirection +
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
                lastLoggedState = "";
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

            if (ukTimeOfDay < HhmmToTimeSpan(TradeStartHHMM)) return;

            bool isInPosition = Position.MarketPosition != MarketPosition.Flat;

            // Trade just closed (stop or target)
            if (wasInPositionLastBar && !isInPosition && londonOpenEvaluated)
            {
                if (lastExitWasTarget)
                {
                    // Target hit: that leg is spent. Same range cannot be re-armed - wait for a NEW range break from here.
                    referenceRangeIndex = currentRangeIndex;
                    setupPhase = PhaseWaitingForRangeBreak;
                    Log("Target hit - leg invalid, waiting for new range break from " + (currentRangeIndex * Po3Size));
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

                if (currentRangeIndex != referenceRangeIndex)
                {
                    // Scenario 1: new range entered since 07:00
                    int openDirection = currentRangeIndex > referenceRangeIndex ? 1 : -1;
                    DealingRange openRange = new DealingRange(Po3Size, currentRangeIndex * Po3Size + Po3Size / 2.0);
                    double openLegTargetPrice = openDirection > 0 ? openRange.Eq - EqOffsetPoints : openRange.Eq + EqOffsetPoints;
                    bool openLegTargetAlreadyReached = openDirection > 0 ? highSinceRangeEntry >= openLegTargetPrice : lowSinceRangeEntry <= openLegTargetPrice;

                    if (openLegTargetAlreadyReached)
                    {
                        // The 07:00-08:00 leg already ran to its target level: no trade, wait for a new range break
                        Log("London open: leg already reached target level " + openLegTargetPrice.ToString("F2") + " - no trade, waiting for new range break from " + (currentRangeIndex * Po3Size));
                        referenceRangeIndex = currentRangeIndex;
                        setupPhase = PhaseWaitingForRangeBreak;
                    }
                    else if (!TryEnter(openDirection, currentRangeIndex))
                    {
                        // Rejected (RR too low / no valid stop) but target not reached: leg still valid for a leave and re-entry
                        Log("London open: entry rejected, target not reached - range " + (currentRangeIndex * Po3Size) + " armed, waiting for leave and re-entry");
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
                DealingRange armedRange = new DealingRange(Po3Size, armedRangeIndex * Po3Size + Po3Size / 2.0);
                double armedLegTargetPrice = armedDirection > 0 ? armedRange.Eq - EqOffsetPoints : armedRange.Eq + EqOffsetPoints;
                bool armedLegTargetReached = armedDirection > 0 ? High[0] >= armedLegTargetPrice : Low[0] <= armedLegTargetPrice;
                if (armedLegTargetReached && (currentRangeIndex - armedRangeIndex) * armedDirection <= 0)
                {
                    referenceRangeIndex = currentRangeIndex;
                    setupPhase = PhaseWaitingForRangeBreak;
                    Log("Price reached target level " + armedLegTargetPrice.ToString("F2") + " before re-entry - leg invalid, waiting for new range break from " + (currentRangeIndex * Po3Size));
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
                    double armedRangeLow = armedRangeIndex * Po3Size;
                    double armedRangeHigh = armedRangeLow + Po3Size;
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
            DealingRange dealingRange = new DealingRange(Po3Size, rangeIndex * Po3Size + Po3Size / 2.0);
            double entryPrice = Close[0];
            double stopPrice, targetPrice, rewardPoints, riskPoints;

            if (direction > 0)
            {
                double fractalLowPrice = FindFractalLow(dealingRange.ExtLow);
                if (double.IsNaN(fractalLowPrice)) { Log("No entry: no fractal low below ExtLow " + dealingRange.ExtLow.ToString("F2") + " in lookback"); return false; }
                stopPrice = fractalLowPrice - StopBufferPoints;
                targetPrice = dealingRange.Eq - EqOffsetPoints;
                rewardPoints = targetPrice - entryPrice;
                riskPoints = entryPrice - stopPrice;
            }
            else
            {
                double fractalHighPrice = FindFractalHigh(dealingRange.ExtHigh);
                if (double.IsNaN(fractalHighPrice)) { Log("No entry: no fractal high above ExtHigh " + dealingRange.ExtHigh.ToString("F2") + " in lookback"); return false; }
                stopPrice = fractalHighPrice + StopBufferPoints;
                targetPrice = dealingRange.Eq + EqOffsetPoints;
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
            Log((direction > 0 ? "ENTER LONG " : "ENTER SHORT ") + "qty " + contracts + " entry~" + entryPrice + " stop " + stopPrice + " tp " + targetPrice + " RR " + (rewardPoints / riskPoints).ToString("F2"));
            return true;
        }

        // Most recent confirmed fractal low that sits below 'belowPrice'
        private double FindFractalLow(double belowPrice)
        {
            int maxBarsAgo = Math.Min(FractalLookback, CurrentBar - FractalStrength);
            for (int barsAgo = FractalStrength; barsAgo <= maxBarsAgo; barsAgo++)
            {
                bool isFractal = true;
                for (int neighbourOffset = 1; neighbourOffset <= FractalStrength; neighbourOffset++)
                {
                    double candidateLow = Low[barsAgo];
                    double lowerNeighbourBefore = Low[barsAgo + neighbourOffset];   // older bar
                    double lowerNeighbourAfter = Low[barsAgo - neighbourOffset];    // newer bar
                    bool failsFractal = AllowEqualFractals
                        ? (candidateLow > lowerNeighbourAfter || candidateLow > lowerNeighbourBefore)
                        : (candidateLow >= lowerNeighbourAfter || candidateLow >= lowerNeighbourBefore);
                    if (failsFractal) { isFractal = false; break; }
                }
                if (isFractal && Low[barsAgo] < belowPrice) return Low[barsAgo];
            }
            return double.NaN;
        }

        // Most recent confirmed fractal high that sits above 'abovePrice'
        private double FindFractalHigh(double abovePrice)
        {
            int maxBarsAgo = Math.Min(FractalLookback, CurrentBar - FractalStrength);
            for (int barsAgo = FractalStrength; barsAgo <= maxBarsAgo; barsAgo++)
            {
                bool isFractal = true;
                for (int neighbourOffset = 1; neighbourOffset <= FractalStrength; neighbourOffset++)
                {
                    double candidateHigh = High[barsAgo];
                    double higherNeighbourBefore = High[barsAgo + neighbourOffset]; // older bar
                    double higherNeighbourAfter = High[barsAgo - neighbourOffset];  // newer bar
                    bool failsFractal = AllowEqualFractals
                        ? (candidateHigh < higherNeighbourAfter || candidateHigh < higherNeighbourBefore)
                        : (candidateHigh <= higherNeighbourAfter || candidateHigh <= higherNeighbourBefore);
                    if (failsFractal) { isFractal = false; break; }
                }
                if (isFractal && High[barsAgo] > abovePrice) return High[barsAgo];
            }
            return double.NaN;
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(0.0, 100.0)]
        [Display(Name = "Re-entry confirmation (points inside range)", GroupName = "Range", Order = 1)]
        public double ReentryConfirmPoints { get; set; }

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
        [Display(Name = "Min RR (all trades)", GroupName = "Risk", Order = 2)]
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