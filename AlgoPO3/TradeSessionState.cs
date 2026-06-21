using System;
using System.Collections.Generic;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public class TradeSessionState
    {
        public double LowOfDay { get; set; }
        public double HighOfDay { get; set; }
        public RangeBias RangeBias { get; private set; }

        private DealingRange currentRange;
        private DealingRange previous;

        private DealingRangePosition rangePosition;

        private bool filteredByTimeOfDay;
        private bool filteredByPriceAtHighOfDay;
        private bool filteredByPriceAtLowOfDay;

        private bool strongBreaksIntoRange;
        private double atr;

        public double MinCovered { get; }
        public double MaxCovered { get; }

        private readonly List<(int hour, int minute)> noTradeTimes = [];

        private readonly AlgoPO3Base owner;

        public TradeSessionState(AlgoPO3Base owner)
        {
            this.owner = owner;

            RangeBias = RangeBias.Neutral;

            noTradeTimes.Add((9, 45));
            noTradeTimes.Add((12, 45));
        }

        public void Update(DealingRange current, DealingRange previous, double open, double close, double high, double low, DateTime priceTime, double atr)
        {
            this.currentRange = current;
            this.previous = previous;

            if (close > HighOfDay || HighOfDay == 0)
            {
                HighOfDay = close;
            }

            if (close < LowOfDay || LowOfDay == 0)
            {
                LowOfDay = close;
            }

            if (previous == null)
                return;

            DetermineRangeBias();

            this.rangePosition = this.currentRange.GetPositionInRange(RangeBias, close);

            this.atr = atr;
        }

        private void DetermineRangeBias()
        {
            if (currentRange.Low == previous.High)
            {
                RangeBias = RangeBias.Bullish;
                //owner.PrintDebug($"Range bias bull {rangeBias}");
            }

            if (currentRange.High == previous.Low)
            {
                RangeBias = RangeBias.Bearish;
                //owner.PrintDebug($"Range bias bear {rangeBias}");
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

            return $"RangePosition: {rangePosition}, Strong Move: {strongBreaksIntoRange} LOD: {LowOfDay}, HOD: {HighOfDay}, Filtered {filteredBy}";
        }

        public TradeBias CalculateTradeBias(DateTime priceTime, double open, double close, double high, double low)
        {
            var bias = TradeBias.Neutral;

            if (previous == null)
                return bias;

            strongBreaksIntoRange = StrongBreak(open, close, currentRange, atr, owner);

            // Don't take trades after these times as we are too close to consolidation.
            if ((priceTime.Hour == 9 && priceTime.Minute >= 45) ||
                (priceTime.Hour == 12 && priceTime.Minute >= 45))
            {
                owner.PrintDebug($"Not taking trades as we are past {priceTime.Hour}:{priceTime.Minute}.");
                filteredByTimeOfDay = true;
                return bias;
            }
            else
            {
                filteredByTimeOfDay = false;
            }

            //if (owner.SkipHours.Split(',').Contains(priceTime.Hour.ToString()))
            //{
            //    owner.PrintDebug($"Not taking trades as we are in hour {priceTime.Hour}:{priceTime.Minute}.");
            //    return bias;
            //}

            if (RangeBias == RangeBias.Bullish)
            {
                if (rangePosition == DealingRangePosition.Discount)
                {
                    if (strongBreaksIntoRange && owner.BullishBOSDetected)
                    {
                        bias = TradeBias.Long;
                        owner.PrintDebug($"Set trade to {bias}, range bias {RangeBias}, range position is {rangePosition}.");
                    }
                    else
                        owner.PrintDebug($"No strong move detected {bias}, range bias {RangeBias}, range position is {rangePosition}.");
                }
            }

            if (RangeBias == RangeBias.Bearish)
            {
                if (rangePosition == DealingRangePosition.Discount)
                {
                    if (strongBreaksIntoRange && owner.BearishBOSDetected)
                    {
                        bias = TradeBias.Short;
                        owner.PrintDebug($"Set trade to {bias}, range bias {RangeBias}, range position is {rangePosition}.");
                    }
                    else
                        owner.PrintDebug($"No strong move detected {bias}, range bias {RangeBias}, range position is {rangePosition}.");
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
                    strongClose = topThird > currentRange.ExtLow && open < currentRange.Low;
                    strongMove = strongClose && candleBodyRange > currentRange.ExtensionRange;
                    owner.PrintDebug($"Strong ext close bullish {strongMove} - strongClose {strongClose} topThird {topThird} extR Low {currentRange.ExtLow} ext R {currentRange.ExtensionRange} cb {candleBodyRange}");
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
                    strongClose = bottomThird < currentRange.ExtHigh && open > currentRange.High;
                    strongMove = strongClose && candleBodyRange > currentRange.ExtensionRange;
                    owner.PrintDebug($"Strong ext close bearish {strongMove} - strongClose {strongClose} bottomThird {bottomThird} ext H {currentRange.ExtHigh} ext R {currentRange.ExtensionRange} cb {candleBodyRange}");
                }
                else
                    owner.PrintDebug($"Strong close bearish {strongMove}");
            }

            return strongMove;
        }

        private bool IsPriceInHighOfDayArea(double price)
        {
            var dayRange = HighOfDay - LowOfDay;
            var hodHigh = (dayRange * 0.05) + HighOfDay;
            var hodLow = HighOfDay - (dayRange * 0.05);

            owner.PrintDebug($"High D Range Check {dayRange} price {price} hodHighRange {hodHigh} hodLowRange {hodLow} HOD {HighOfDay} LOD {LowOfDay}");

            return price >= hodLow && price <= hodHigh;
        }

        private bool IsPriceInLowOfDayArea(double price)
        {
            var dayRange = HighOfDay - LowOfDay;
            var lodHigh = LowOfDay + (dayRange * 0.05);
            var lodLow = LowOfDay - (dayRange * 0.05);

            owner.PrintDebug($"Low D Range Check {dayRange} price {price} hodHigh {lodHigh} hodLow {lodLow} HOD {HighOfDay} LOD {LowOfDay}");

            return price >= lodLow && price <= lodHigh;
        }
    }
}