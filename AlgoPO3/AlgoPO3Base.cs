using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public abstract class AlgoPO3Base : AlgoBase
    {
        [NinjaScriptProperty]
        [Display(Name = "PO3", Description = "PO3", Order = 10, GroupName = "Parameters")]
        public int PO3 { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Long Edge X", Order = 11, GroupName = "Parameters")]
        public double LongEdgeMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Short Edge X", Order = 12, GroupName = "Parameters")]
        public double ShortEdgeMultiplier { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable CVD EMA Dist Filtering ", Order = 14, GroupName = "Parameters")]
        public bool CVDEMADistanceFilteringEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EQ TP Adjust", Order = 15, GroupName = "Parameters")]
        public int EqAdjust { get; set; }

        internal int[] BlockEndHoursParsed;
        internal int[] EdgeHoursParsed;
        internal int[] SkipHoursParsed;

        internal bool rangeBreakDetected { get; set; } = false;

        internal RangeBias RangeBias { get; set; }
        internal TradeBias TradeBias { get; set; }

        internal DealingRange CurrentRange { get; set; }
        internal DealingRange PreviousRange { get; set; }

        internal List<DealingRange> Ranges { get; set; }

        internal InvalidRange InvalidRange { get; set; }
        internal SharedBorderTrade SharedBorderTrade { get; set; }

        internal TradeSessionState TradingSession { get; set; }

        internal CVD_TradeType_Enum CVD_TradeType { get; set; }

        internal abstract List<(double Min, double Max)> LongBuckets { get; }
        internal abstract List<(double Min, double Max)> LongEdgeBuckets { get; }
        internal abstract List<(double Min, double Max)> ShortBuckets { get; }
        internal abstract List<(double Min, double Max)> ShortEdgeBuckets { get; }

        internal void PlaceCvdTrade(int risk)
        {
            /* ----- PLACE THE TRADE ----- */

            CurrentTrade.PositionSize = CalculatePositionSizeWithEdge(Math.Abs(Close[0] - CurrentTrade.StopLoss), risk);

            var rr = CalculateRiskReward(Math.Abs(Close[0] - CurrentTrade.StopLoss), Math.Abs(CurrentTrade.TakeProfit - Close[0]));
            if (rr < 0.5)
            {
                PrintDebug($"[RISK_REWARD_BELOW_THRESHOLD] {CurrentTrade.CVD_TradeType} {CurrentTrade.Bias} {rr}");

                CurrentTrade.Virtual = false;

                // Only reset if this was the initial trade into a range
                //if (CurrentTrade.CVD_TradeType == CVD_TradeType_Enum.Normal)
                rangeBreakDetected = false;

                return;
            }

            if (CurrentTrade.Virtual)
            {
                PrintInfo($"[VIRTUAL_POSITION] {CurrentTrade.Bias} at {CurrentTrade.Entry}, SL {CurrentTrade.StopLoss}, TP {CurrentTrade.TakeProfit}");
                return;
            }

            if (CurrentTrade.Bias == TradeBias.Long)
            {
                SetStopLoss(LONG_TRADE, CalculationMode.Price, CurrentTrade.StopLoss, false);
                SetProfitTarget(LONG_TRADE, CalculationMode.Price, CurrentTrade.TakeProfit);

                EnterLong(0, CurrentTrade.PositionSize, LONG_TRADE);
                PrintDebug($"[PLACE_TRADE_MARKET] {CurrentTrade.Bias} {CurrentTrade.CVD_TradeType}, at {CurrentTrade.Entry}, SL {CurrentTrade.StopLoss}, TP {CurrentTrade.TakeProfit}");

                return;
            }

            if (CurrentTrade.Bias == TradeBias.Short)
            {
                SetStopLoss(SHORT_TRADE, CalculationMode.Price, CurrentTrade.StopLoss, false);
                SetProfitTarget(SHORT_TRADE, CalculationMode.Price, CurrentTrade.TakeProfit);

                EnterShort(0, CurrentTrade.PositionSize, SHORT_TRADE);
                PrintDebug($"[PLACE_TRADE_MARKET] {CurrentTrade.Bias} {CurrentTrade.CVD_TradeType}, at {CurrentTrade.Entry}, SL {CurrentTrade.StopLoss}, TP {CurrentTrade.TakeProfit}");

                return;
            }
        }

        internal void CancelLimitOrder()
        {
            if (CurrentTrade == null)
                return;

            if (CurrentTrade.PendingOrder != null)
            {
                CancelOrder(CurrentTrade.PendingOrder);
                PrintDebug("[CANCEL_LIMIT_ORDER]");
            }

            CurrentTrade = null;
        }

        internal void CreateCurrentTradeCVD(int timeframe = 1, bool targetFullRange = false)
        {
            if (RangeBias == RangeBias.Bearish && TradeBias == TradeBias.Short)
            {
                CurrentTrade = new TradeDetails()
                {
                    Bias = TradeBias.Short,
                    Entry = Close[0], // This is set for virtual trade purposes but it is essentially a market order.
                    EntryTime = Time[0] // Market order, so entry time is now.
                };

                if (CVD_TradeType == CVD_TradeType_Enum.Normal)
                {
                    CurrentTrade.StopLoss = CalculateStopLossForCVDAlgos(timeframe);
                    CurrentTrade.TakeProfit = (targetFullRange ? CurrentRange.Low : CurrentRange.Eq + EqAdjust);
                }
                else
                {
                    CurrentTrade.StopLoss = CurrentRange.High;
                    CurrentTrade.TakeProfit = CurrentRange.Low;
                }
            }

            if (RangeBias == RangeBias.Bullish && TradeBias == TradeBias.Long)
            {
                CurrentTrade = new TradeDetails()
                {
                    Bias = TradeBias.Long,
                    Entry = Close[0], // This is set for virtual trade purposes but it is essentially a market order.
                    EntryTime = Time[0] // Market order, so entry time is now.
                };

                if (CVD_TradeType == CVD_TradeType_Enum.Normal)
                {
                    CurrentTrade.StopLoss = CalculateStopLossForCVDAlgos(timeframe);
                    CurrentTrade.TakeProfit = (targetFullRange ? CurrentRange.High : CurrentRange.Eq - EqAdjust);
                }
                else
                {
                    CurrentTrade.StopLoss = CurrentRange.Low;
                    CurrentTrade.TakeProfit = CurrentRange.High;
                }
            }
        }

        internal bool DetectAndCheckInvalidRange()
        {
            if (InvalidRange == null)
                return false;

            if (InvalidRange.IsInPlay(Close[0]))
            {
                PrintDebug($"[RANGE_INVALID_IN_PLAY] Skipping as {Close[0]} is in invalid range.");
                return true;
            }

            // Did we leave the invalid range?
            if (!InvalidRange.IsInPlay(Close[0]))
            {
                PrintDebug($"[RANGE_INVALID_CLEARED] Price {Close[0]} cleared the invalid range, resetting.");

                InvalidRange = null;
                SharedBorderTrade = null;

                return false;
            }

            return false;
        }

        internal void UpdateCVDTradeType(bool isWin)
        {
            if (isWin)
            {
                if (CVD_TradeType == CVD_TradeType_Enum.Continuation)
                {
                    CVD_TradeType = CVD_TradeType_Enum.Normal;

                    // We need to wait for a new break before looking for another entry.
                    rangeBreakDetected = false;
                }
                else
                    CVD_TradeType = CVD_TradeType_Enum.Continuation;
            }
            else
                CVD_TradeType = CVD_TradeType_Enum.Normal;
        }

        internal void ProcessVirtualTrade()
        {
            if (CurrentTrade == null || (CurrentTrade?.Virtual == false)) return;

            if (!ImportTickAndCvdEnabled)
            {
                ProcessVirtualTradeInternal(Close[0]);
                return;
            }

            // Process the 1min bar's ticks via the imported data.
            long nextBarTicks = Time[0].AddMinutes(1).Ticks;
            //PrintDebug($"{CurrentTrade.VirtualTickIndex} {new DateTime(TickDataSeriesImport[CurrentTrade.VirtualTickIndex].Ticks)} {new DateTime(nextBarTicks)}");
            while (CurrentTrade.Virtual)
            {
                var (NewIndex, Tick) = GetNextTick(CurrentTrade.VirtualTickIndex, nextBarTicks);

                if (Tick == null)
                    break;

                CurrentTrade.VirtualTickIndex = NewIndex;

                ProcessVirtualTradeInternal(Tick.Value.Price);
            }
        }

        private void ProcessVirtualTradeInternal(double tickPrice)
        {
            if (!CurrentTrade.VirtualOpen && (CurrentTrade.OrderType == OrderType.Limit || CurrentTrade.OrderType == OrderType.StopMarket) && tickPrice == CurrentTrade.Entry)
            {
                CurrentTrade.VirtualOpen = true;
                PrintInfo($"[VIRTUAL_POSITION] {CurrentTrade.Bias} triggered at {tickPrice}");
                return;
            }

            if (!CurrentTrade.VirtualOpen) return;

            var contractTickValue = Instrument.MasterInstrument.PointValue * CurrentTrade.PositionSize;

            if (CurrentTrade.Bias == TradeBias.Short &&
                (tickPrice <= CurrentTrade.TakeProfit || tickPrice >= CurrentTrade.StopLoss))
            {
                CurrentTrade.Virtual = false;
                CurrentTrade.VirtualOpen = false;

                UpdateCVDTradeType(tickPrice <= CurrentTrade.TakeProfit);

                var pnl = 0.0;

                if (tickPrice <= CurrentTrade.TakeProfit)
                    pnl = (CurrentTrade.Entry - CurrentTrade.TakeProfit) * contractTickValue;

                if (tickPrice >= CurrentTrade.StopLoss)
                    pnl = -((CurrentTrade.StopLoss - CurrentTrade.Entry) * contractTickValue);

                DayPnLVirtual += pnl;

                PrintInfo($"[VIRTUAL_POSITION] {CurrentTrade.Bias} closed at {tickPrice} Trade Pnl {Math.Round(pnl)} Virtual Day Pnl {Math.Round(DayPnLVirtual)}");
            }

            if (CurrentTrade.Bias == TradeBias.Long &&
                (tickPrice >= CurrentTrade.TakeProfit || tickPrice <= CurrentTrade.StopLoss))
            {
                CurrentTrade.Virtual = false;
                CurrentTrade.VirtualOpen = false;

                UpdateCVDTradeType(tickPrice >= CurrentTrade.TakeProfit);

                var pnl = 0.0;

                if (tickPrice >= CurrentTrade.TakeProfit)
                    pnl = (CurrentTrade.TakeProfit - CurrentTrade.Entry) * contractTickValue;

                if (tickPrice <= CurrentTrade.StopLoss)
                    pnl = -((CurrentTrade.StopLoss - CurrentTrade.Entry) * contractTickValue);

                DayPnLVirtual += pnl;

                PrintInfo($"[VIRTUAL_POSITION] {CurrentTrade.Bias} closed at {tickPrice} Trade Pnl {Math.Round(pnl)} Virtual Day Pnl {Math.Round(DayPnLVirtual)}");
            }
        }

        internal double CalculateStopLossForCVDAlgos(int timeframe)
        {
            var candidates = new List<(string, double)>();

            if (TradeBias == TradeBias.Short)
            {
                if (ImbalanceFormed(TradeBias.Short, out double imbalanceStart))
                    if (imbalanceStart > CurrentRange.ExtHigh)
                    {
                        PrintDebug($"[STOP_LOSS_IMB] {imbalanceStart} {CurrentRange.ExtHigh}");
                        return imbalanceStart;
                    }

                if (FractalManager.Fractals.TryGetValue((timeframe, FractalType.HighThreeBar), out var threeBarFractal))
                    if (threeBarFractal.Price > CurrentRange.ExtHigh) candidates.Add(("3 Bar Fractal", threeBarFractal.Price));

                if (FractalManager.Fractals.TryGetValue((timeframe, FractalType.HighFiveBar), out var fiveBarFractal))
                    if (fiveBarFractal.Price > CurrentRange.ExtHigh) candidates.Add(("5 Bar Fractal", fiveBarFractal.Price));

                if (High[0] > Math.Round(CurrentRange.ExtHigh))
                    candidates.Add(("High Above Ext", High[0]));

                candidates.ForEach(x => PrintDebug($"{x}"));

                // which one is the closest to the ext high 
                if (candidates.Any())
                    return candidates.Min(c => c.Item2);

                //return CurrentRange.ExtLow;
            }

            if (TradeBias == TradeBias.Long)
            {
                if (ImbalanceFormed(TradeBias.Long, out double imbalanceStart))
                    if (imbalanceStart < CurrentRange.ExtLow)
                    {
                        PrintDebug($"[STOP_LOSS_IMB] {imbalanceStart} {CurrentRange.ExtLow}");
                        return imbalanceStart;
                    }

                if (FractalManager.Fractals.TryGetValue((timeframe, FractalType.LowThreeBar), out var threeBarFractal))
                    if (threeBarFractal.Price < CurrentRange.ExtLow) candidates.Add(("3 Bar Fractal", threeBarFractal.Price));

                if (FractalManager.Fractals.TryGetValue((timeframe, FractalType.LowFiveBar), out var fiveBarFractal))
                    if (fiveBarFractal.Price < CurrentRange.ExtLow) candidates.Add(("5 Bar Fractal", fiveBarFractal.Price));

                if (Low[0] < Math.Round(CurrentRange.ExtLow))
                    candidates.Add(("Low Below Ext", Low[0]));

                // which one is the closest to the ext low
                if (candidates.Any())
                    return candidates.Max(x => x.Item2);

                //return CurrentRange.ExtLow;
            }

            return 0;
        }

        internal DealingRange CreateDealingRange()
        {
            var newDealingRange = new DealingRange(PO3, Close[0]);
            Ranges.Add(newDealingRange);
            PrintDebug($"Creating new range: {newDealingRange}");
            return newDealingRange;
        }

        internal bool IsInBucket(double value, IEnumerable<(double Min, double Max)> buckets)
        {
            return buckets.Any(x => value > x.Min && value <= x.Max);
        }

        internal int CalculatePositionSizeWithEdge(double stopDistance, int risk)
        {
            var positionSize = CalculatePositionSize(stopDistance, risk);

            if (CurrentTrade.Bias == TradeBias.Long && IsInBucket(CurrentTrade.CVD_EMA_Distance, LongEdgeBuckets))
                positionSize = (int)(positionSize * LongEdgeMultiplier);

            if (CurrentTrade.Bias == TradeBias.Short && IsInBucket(CurrentTrade.CVD_EMA_Distance, ShortEdgeBuckets))
                positionSize = (int)(positionSize * ShortEdgeMultiplier);

            return positionSize;
        }
    }
}