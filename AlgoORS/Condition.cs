using System;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.Strategies.FTA.Shared;

namespace NinjaTrader.NinjaScript.Strategies.FTA.AlgoORS
{
    public enum TradeRangeInitiator
    {
        None,
        RangeHighEngaged,
        RangeLowEngaged,
        ContinueAboveHigh,
        ContinueBelowLow
    }

    public class StrategySettings
    {
        public double EntryPercentage { get; set; }
        public double MaxRiskReward { get; set; }
        public double RangeHigh { get; set; }
        public double RangeLow { get; set; }
        public TradeRangeInitiator RangeInitiator { get; set; }
        public double CandleBodyPercentage { get; set; }
        public double StopGrace { get; internal set; }
        public double TPMultiplier { get; internal set; }
        public int Risk { get; internal set; }
    }

    internal abstract class Condition(AlgoBase owner, FractalManager fractalManager)
    {
        protected readonly AlgoBase Owner = owner;
        protected readonly FractalManager FractalManager = fractalManager;

        internal int ValidFromTime { get; set; }

        internal abstract bool IsSatisfied(StrategySettings settings);
        internal abstract TradeExecutionResult Execute(StrategySettings settings);

        internal bool CandleBodyRangeInPoints(int minPoints)
        {
            return Owner.Open[0] - Owner.Close[0] >= minPoints || Owner.Close[0] - Owner.Open[0] >= minPoints;
        }

        internal double CandleBodyRange(int index)
        {
            if (Owner.Open[index] > Owner.Close[index])
                return Owner.Open[index] - Owner.Close[index];
            else
                return Owner.Close[index] - Owner.Open[index];
        }

        internal bool CloseHigher()
        {
            return Owner.Close[0] > Owner.Close[1];
        }

        internal bool CloseLower()
        {
            return Owner.Close[0] < Owner.Close[1];
        }

        internal double CalculateRiskReward(double stopLossDistance, double profitTargetDistance)
        {
            return Math.Round(profitTargetDistance / stopLossDistance, 2);
        }

        internal TradeExecutionResult EnterLongTrade(string log, bool isLimit, TradeRangeInitiator rangeInitiator, TradeType tradeType, int positionSize, double rr, double stopLoss, double takeProfit, double entryPrice = 0)
        {
            Order limitOrder = null;

            if (isLimit)
                limitOrder = Owner.EnterLongLimit(0, true, positionSize, entryPrice, "long");
            else
                Owner.EnterLong(positionSize, "long");

            Owner.SetStopLoss("long", CalculationMode.Price, stopLoss, true);
            Owner.SetProfitTarget("long", CalculationMode.Price, takeProfit, true);

            Owner.PrintDebug($"[{log}] Size {positionSize} RR {rr} Stop Loss {stopLoss} Profit Target {takeProfit}");

            return new TradeExecutionResult
            {
                Bias = TradeBias.Long,
                Entry = Owner.Position.AveragePrice,
                EntryTime = Owner.Time[0],
                LimitOrder = limitOrder,
                RangeInitiater = rangeInitiator,
                StopLoss = stopLoss,
                TakeProfit = takeProfit,
                Type = tradeType
            };
        }

        internal TradeExecutionResult EnterShortTrade(string log, bool isLimit, TradeRangeInitiator rangeInitiator, TradeType tradeType, int positionSize, double rr, double stopLoss, double takeProfit, double entryPrice = 0)
        {
            Order limitOrder = null;

            if (isLimit)
                limitOrder = Owner.EnterShortLimit(0, true, positionSize, entryPrice, "short");
            else
                Owner.EnterShort(positionSize, "short");

            Owner.SetStopLoss("short", CalculationMode.Price, stopLoss, true);
            Owner.SetProfitTarget("short", CalculationMode.Price, takeProfit, true);

            Owner.PrintDebug($"[{log}] Size {positionSize} RR {rr} Stop Loss {stopLoss} Profit Target {takeProfit}");

            return new TradeExecutionResult
            {
                Bias = TradeBias.Short,
                Entry = Owner.Position.AveragePrice,
                EntryTime = Owner.Time[0],
                LimitOrder = limitOrder,
                RangeInitiater = rangeInitiator,
                StopLoss = stopLoss,
                TakeProfit = takeProfit,
                Type = tradeType
            };
        }
    }
}
