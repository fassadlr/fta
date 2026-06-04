using System;
using System.Runtime.CompilerServices;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoPO3
{
    public class DealingRange
    {
        public readonly double ExtensionLow;
        public readonly double ExtensionLowCE;
        public readonly double Low;
        public readonly double OBCE1;
        public readonly double FV1;
        public readonly double FV2;
        public readonly double FV2Entry;
        public readonly double BR1;
        public readonly double BR2;
        public readonly double MB1;
        public readonly double MB2;
        public readonly double MBCE1;
        public readonly double Eq;
        public readonly double MB3;
        public readonly double MB4;
        public readonly double MBCE2;
        public readonly double BR3;
        public readonly double BR4;
        public readonly double FV3;
        public readonly double FV3Entry;
        public readonly double FV4;
        public readonly double OBCE2;
        public readonly double High;
        public readonly double ExtensionHighCE;

        public double ExtensionRange { get; }

        public readonly double ExtensionHigh;

        public bool LongTradeExecuted { get; set; }
        public bool ShortTradeExecuted { get; set; }

        public bool ShortTradeLoss { get; set; }
        public bool LongTradeLoss { get; set; }

        // Tracks what part has been covered
        public double MinCovered { get; private set; }
        public double MaxCovered { get; private set; }

        public DealingRange(int po3, double price)
        {
            Low = Math.Floor(price / po3) * po3;
            ExtensionLow = Low - (po3 * 0.111);
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
            ExtensionHigh = High + (po3 * 0.111);

            OBCE1 = Low + ((FV1 - Low) / 2);
            ExtensionLowCE = ExtensionLow + ((Low - ExtensionLow) / 2);
            FV2Entry = Math.Round(FV2 / 0.05) * 0.05;
            MBCE1 = MB1 + ((MB2 - MB1) / 2);
            MBCE2 = MB3 + ((MB4 - MB3) / 2);
            FV3Entry = Math.Round(FV3 / 0.05) * 0.05;
            OBCE2 = FV4 + ((High - FV4) / 2);
            ExtensionHighCE = ExtensionHigh - ((ExtensionHigh - High) / 2);

            ExtensionRange = Math.Round(Low - ExtensionLow);

            MinCovered = 0;
            MaxCovered = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsInRange(double price) => price >= Low && price <= High;

        public double InvalidLevel(TradeBias bias, int invalidRangeSetting)
        {
            if (invalidRangeSetting == 2)
                return Eq;

            if (bias == TradeBias.Short)
            {
                if (invalidRangeSetting == 0)
                    return BR1;
                if (invalidRangeSetting == 1)
                    return MB1;
            }

            if (bias == TradeBias.Long)
            {
                if (invalidRangeSetting == 0)
                    return BR2;
                if (invalidRangeSetting == 1)
                    return MB2;
            }

            return 0;
        }

        public void UpdateCoverage(double price)
        {
            if (MinCovered == 0 || MaxCovered == 0)
            {
                MinCovered = price;
                MaxCovered = price;
            }

            MinCovered = Math.Min(MinCovered, price);
            MaxCovered = Math.Max(MaxCovered, price);

            if (MaxCovered > High)
                MaxCovered = High;

            if (MinCovered < Low)
                MinCovered = Low;
        }

        public int CoveredPercentage
        {
            get
            {
                return MaxCovered == 0 || MinCovered == 0 ? 0 : (int)((MaxCovered - MinCovered) / (High - Low) * 100);
            }
        }

        public static bool operator ==(DealingRange a, DealingRange b)
        {
            if (ReferenceEquals(a, b))
                return true;

            if (a is null || b is null)
                return false;

            return a.Low == b.Low && a.High == b.High;
        }

        public static bool operator !=(DealingRange a, DealingRange b) => !(a == b);

        public override bool Equals(object obj) => obj is DealingRange other && this == other;

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + Low.GetHashCode();
                hash = hash * 23 + High.GetHashCode();
                return hash;
            }
        }

        public override string ToString() => $"Low: {Low} High: {High} Min Covered: {MinCovered} Max Covered: {MaxCovered}, Covered: {CoveredPercentage}%";

        public DealingRangePosition GetPositionInRange(RangeBias rangeBias, double price)
        {
            if (rangeBias == RangeBias.Bullish)
            {
                if (price > Eq)
                    return DealingRangePosition.Premium;
                else
                    return DealingRangePosition.Discount;
            }

            if (price > Eq)
                return DealingRangePosition.Discount;
            else
                return DealingRangePosition.Premium;
        }

        public void ClearCovered()
        {
            MinCovered = 0;
            MaxCovered = 0;
        }
    }

    public enum DealingRangePosition
    {
        Discount,
        Premium
    }
}
