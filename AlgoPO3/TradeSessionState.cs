using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.Custom.Strategies.FTA.AlgoPO3;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public class TradeSessionState
    {
        public double LowOfDay { get; set; }
        public double HighOfDay { get; set; }
        public RangeBias RangeBias { get; private set; }

        private DealingRange currentRange;
        private DealingRange previousRange;

        private DealingRangePosition rangePosition;

        private bool filteredByTimeOfDay;
        private bool filteredByPriceAtHighOfDay;
        private bool filteredByPriceAtLowOfDay;

        private bool strongBreakIntoRange;
        private double atr;

        public double MinCovered { get; }
        public double MaxCovered { get; }

        private readonly List<(int hour, int minute)> noTradeTimes = [];

        private readonly AlgoPO3Base owner;

        private const double DAILY_RANGE_FILTER_PERCENTAGE = 0.05;

        public TradeSessionState(AlgoPO3Base owner)
        {
            this.owner = owner;

            RangeBias = RangeBias.Neutral;

            noTradeTimes.Add((9, 45));
            noTradeTimes.Add((12, 45));
        }

        public void Update(double close, double atr)
        {
            this.atr = atr;

            if (close > HighOfDay || HighOfDay == 0)
            {
                HighOfDay = close;
            }

            if (close < LowOfDay || LowOfDay == 0)
            {
                LowOfDay = close;
            }
        }

        public void ClearRangeBias()
        {
            RangeBias = RangeBias.Neutral;
        }

        public void DetermineRangeBias(DealingRange previousRange, DealingRange currentRange)
        {
            this.currentRange = currentRange;
            this.previousRange = previousRange;

            if (previousRange == null)
                return;

            if (owner.InvalidRange != null && owner.InvalidRange.IsInPlay(owner.Close[0]))
                return;

            if (currentRange.Low == previousRange.High)
            {
                RangeBias = RangeBias.Bullish;
                owner.PrintDebug($"Range break detected, range set to {currentRange}");
            }

            if (currentRange.High == previousRange.Low)
            {
                RangeBias = RangeBias.Bearish;
                owner.PrintDebug($"Range break detected, range set to {currentRange}");
            }
        }

        public string Info()
        {
            var filteredBy = "";
            if (filteredByPriceAtHighOfDay)
                filteredBy = "filteredByPriceAtHighOfDay";

            if (filteredByPriceAtLowOfDay)
                filteredBy = "filteredByPriceAtHighOfDay";

            if (filteredByTimeOfDay)
                filteredBy = "filteredByTimeOfDay";

            return $"RangePosition: {rangePosition}, Strong Move: {strongBreakIntoRange} LOD: {LowOfDay}, HOD: {HighOfDay}, Filtered {filteredBy}";
        }

        public TradeBias CalculateTradeBias(DateTime priceTime, double open, double close)
        {
            var bias = TradeBias.Neutral;

            if (previousRange == null)
                return bias;

            // Don't take trades after these times as we are too close to consolidation.
            if (owner.BlockEndHoursParsed.Contains(priceTime.Hour) && priceTime.Minute >= 45)
            {
                owner.PrintDebug($"Not taking trades as we are past {priceTime.Hour}:{priceTime.Minute}.");
                filteredByTimeOfDay = true;
                return bias;
            }
            else
            {
                filteredByTimeOfDay = false;
            }

            if (owner.SkipHoursParsed.Contains(priceTime.Hour))
            {
                owner.PrintDebug($"Not taking trades as we are in hour {priceTime.Hour}:{priceTime.Minute}.");
                return bias;
            }

            strongBreakIntoRange = StrongBreak(open, close, currentRange, atr, owner);

            if (RangeBias == RangeBias.Bullish)
            {
                if (rangePosition == DealingRangePosition.Discount)
                {
                    if (strongBreakIntoRange && owner.BullishBOSDetected)
                    {
                        bias = TradeBias.Long;
                        owner.PrintDebug($"Set trade to {bias}, range bias {RangeBias}, range position is {rangePosition}.");
                    }
                    else
                        owner.PrintDebug($"Strong break into range failed, bullish BOS {owner.BullishBOSDetected}, range bias {RangeBias}, range position is {rangePosition}.");
                }
            }

            if (RangeBias == RangeBias.Bearish)
            {
                if (rangePosition == DealingRangePosition.Discount)
                {
                    if (strongBreakIntoRange && owner.BearishBOSDetected)
                    {
                        bias = TradeBias.Short;
                        owner.PrintDebug($"Set trade to {bias}, range bias {RangeBias}, range position is {rangePosition}.");
                    }
                    else
                        owner.PrintDebug($"Strong break into range failed, bearish BOS {owner.BearishBOSDetected}, range bias {RangeBias}, range position is {rangePosition}.");
                }
            }

            //This gets skipped before London open.
            if (priceTime.Hour < 8)
                return bias;

            // Don't take continuation trades if we at the low of the day.
            if (bias == TradeBias.Long && IsPriceInHighOfDayArea(close))
            {
                bias = TradeBias.Neutral;
                filteredByPriceAtHighOfDay = true;
                filteredByPriceAtLowOfDay = false;
                owner.PrintDebug($"Price at HOD set trade to {bias} price {close}.");
            }

            if (bias == TradeBias.Short && IsPriceInLowOfDayArea(close))
            {
                bias = TradeBias.Neutral;
                filteredByPriceAtHighOfDay = false;
                filteredByPriceAtLowOfDay = true;
                owner.PrintDebug($"Price at LOD set trade to {bias} price {close}.");
            }

            return bias;
        }

        public (TradeBias Bias, bool IsTradeInThisRange) CalculateTradeBiasUniDirectional(DateTime priceTime, double open, double close)
        {
            var tradeBias = TradeBias.Neutral;
            var isTradeInThisRange = true;

            if (previousRange == null)
                return (tradeBias, false);

            rangePosition = this.currentRange.GetPositionInRange(RangeBias, close);

            if (previousRange == null)
                return (tradeBias, false);

            // Don't take trades after these times as we are too close to consolidation.
            if (owner.BlockEndHoursParsed.Contains(priceTime.Hour) && priceTime.Minute >= 45)
            {
                owner.PrintDebug($"Not taking trades as we are past {priceTime.Hour}:{priceTime.Minute}.");
                filteredByTimeOfDay = true;
                return (tradeBias, false);
            }
            else
            {
                filteredByTimeOfDay = false;
            }

            if (owner.SkipHoursParsed.Contains(priceTime.Hour))
            {
                owner.PrintDebug($"Not taking trades as we are in hour {priceTime.Hour}:{priceTime.Minute}.");
                return (tradeBias, false);
            }

            strongBreakIntoRange = StrongBreak(open, close, currentRange, atr, owner);

            if (RangeBias == RangeBias.Bullish)
            {
                if (owner.BearishBOSDetected)
                {
                    tradeBias = TradeBias.Short;
                    isTradeInThisRange = false;
                }

                if (owner.BullishBOSDetected)
                    tradeBias = TradeBias.Long;

                owner.PrintDebug($"Set trade to {tradeBias}, range bias {RangeBias}, range position is {rangePosition}.");
            }

            if (RangeBias == RangeBias.Bearish)
            {
                if (owner.BearishBOSDetected)
                {
                    tradeBias = TradeBias.Short;
                }

                if (owner.BullishBOSDetected)
                {
                    tradeBias = TradeBias.Long;
                    isTradeInThisRange = false;
                }

                owner.PrintDebug($"Set trade to {tradeBias}, range bias {RangeBias}, range position is {rangePosition}.");
            }

            //This gets skipped before London open.
            if (priceTime.Hour < 8)
                return (tradeBias, false);

            // Don't take continuation trades if we at the low of the day.
            if (tradeBias == TradeBias.Long && IsPriceInHighOfDayArea(close))
            {
                tradeBias = TradeBias.Neutral;
                filteredByPriceAtHighOfDay = true;
                filteredByPriceAtLowOfDay = false;
                owner.PrintDebug($"Price at HOD set trade to {tradeBias} price {close}.");
            }

            if (tradeBias == TradeBias.Short && IsPriceInLowOfDayArea(close))
            {
                tradeBias = TradeBias.Neutral;
                filteredByPriceAtHighOfDay = false;
                filteredByPriceAtLowOfDay = true;
                owner.PrintDebug($"Price at LOD set trade to {tradeBias} price {close}.");
            }

            return (tradeBias, isTradeInThisRange);
        }

        public static bool StrongBreak(double open, double close, DealingRange currentRange, double atr, AlgoPO3Base owner)
        {
            var strongMove = false;

            // TODO check previous candle after range break.
            var candleBodyRange = open > close ? open - close : close - open;
            if (close > open)
            {
                var topThird = open + (candleBodyRange * 0.66);
                var strongClose = topThird > currentRange.Low && open < currentRange.Low;
                strongMove = strongClose && candleBodyRange > (currentRange.ExtensionRange); // DONT USE ATR

                // Check through extension
                if (!strongMove)
                {
                    strongClose = topThird > currentRange.ExtensionLow && open < currentRange.Low;
                    strongMove = strongClose && candleBodyRange > currentRange.ExtensionRange;
                    owner.PrintDebug($"Strong ext close bullish {strongMove} - strongClose {strongClose} topThird {topThird} extR Low {currentRange.ExtensionLow} ext R {currentRange.ExtensionRange} cb {candleBodyRange}");
                }
                else
                    owner.PrintDebug($"Strong close bullish {strongMove}");
            }

            if (close < open)
            {
                var bottomThird = open - (candleBodyRange * 0.66);
                var strongClose = bottomThird < currentRange.High && open > currentRange.High;
                strongMove = strongClose && candleBodyRange > (currentRange.ExtensionRange); // DONT USE ATR

                // Check through extension (TODO this wont always trigger if we hvent actaully entered a new range
                if (!strongMove)
                {
                    strongClose = bottomThird < currentRange.ExtensionHigh && open > currentRange.High;
                    strongMove = strongClose && candleBodyRange > currentRange.ExtensionRange;
                    owner.PrintDebug($"Strong ext close bearish {strongMove} - strongClose {strongClose} bottomThird {bottomThird} ext H {currentRange.ExtensionHigh} ext R {currentRange.ExtensionRange} cb {candleBodyRange}");
                }
                else
                    owner.PrintDebug($"Strong close bearish {strongMove}");
            }

            return strongMove;
        }

        private bool IsPriceInHighOfDayArea(double price)
        {
            var dayRange = HighOfDay - LowOfDay;
            var dayRangePercentage = dayRange * DAILY_RANGE_FILTER_PERCENTAGE;
            var hodHigh = HighOfDay + dayRangePercentage;
            var hodLow = HighOfDay - dayRangePercentage;

            owner.PrintDebug($"High D Range Check {dayRange} price {price} hodHighRange {hodHigh} hodLowRange {hodLow} HOD {HighOfDay} LOD {LowOfDay}");

            return price >= hodLow && price <= hodHigh;
        }

        private bool IsPriceInLowOfDayArea(double price)
        {
            var dayRange = HighOfDay - LowOfDay;
            var dayRangePercentage = dayRange * DAILY_RANGE_FILTER_PERCENTAGE;
            var lodHigh = LowOfDay + dayRangePercentage;
            var lodLow = LowOfDay - dayRangePercentage;

            owner.PrintDebug($"Low D Range Check {dayRange} price {price} hodHigh {lodHigh} hodLow {lodLow} HOD {HighOfDay} LOD {LowOfDay}");

            return price >= lodLow && price <= lodHigh;
        }

        internal bool IsPriceAtDailyRangeLevels(double price)
        {
            return IsPriceInHighOfDayArea(price) || IsPriceInLowOfDayArea(price);
        }
    }
}