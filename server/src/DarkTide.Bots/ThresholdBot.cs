using DarkTide.Rules;

namespace DarkTide.Bots;

/// <summary>人机的全部可调参数。门槛都是胜率（0～1），按轮次与对手数分别给出。</summary>
public sealed record ThresholdBotParameters
{
    /// <summary>弃牌门槛 [轮次, 对手数]：轮次 0～2 对应一注～三注，对手数 1～7（下标 0 不用）。</summary>
    public required double[,] Fold { get; init; }
    /// <summary>加注门槛，含义同上；高于它就加注。</summary>
    public required double[,] Raise { get; init; }
    /// <summary>加注额占“跟注后池子”的比例。</summary>
    public double RaisePotFraction { get; init; } = 0.5;
    /// <summary>换牌余量：胜率提升超过“改航费 ÷ 可争夺池＋余量”才换。</summary>
    public double RerouteMargin { get; init; } = 0.02;
    /// <summary>按结果调整的步长：弃了却本该最大 → 门槛下调 Down；打到底却输 → 门槛上调 Up。为 0 表示不调整。</summary>
    public double AdjustDown { get; init; }
    public double AdjustUp { get; init; }
    /// <summary>调整的上下限（绝对值）。</summary>
    public double AdjustLimit { get; init; } = 0.10;

    /// <summary>
    /// 初始值：按“公平份额”给的起点，不是实验结论。一名对手时公平份额是 1/2，k 名是 1/(k+1)；
    /// 弃牌门槛取公平份额的 0.8 倍，加注门槛取 1.5 倍。基准由自我对战实验再定。
    /// </summary>
    public static ThresholdBotParameters FairShareStart(double foldFactor = 0.8, double raiseFactor = 1.5)
    {
        var fold = new double[3, EquityTable.MaxOpponents + 1];
        var raise = new double[3, EquityTable.MaxOpponents + 1];
        for (int street = 0; street < 3; street++)
        for (int k = 1; k <= EquityTable.MaxOpponents; k++)
        {
            double fair = 1.0 / (k + 1);
            fold[street, k] = foldFactor * fair;
            raise[street, k] = Math.Min(0.95, raiseFactor * fair);
        }
        return new ThresholdBotParameters { Fold = fold, Raise = raise };
    }
}

/// <summary>
/// 按概率行事的人机（用户的设计，2026-10-16）：
/// - 只算自己的胜率，不猜对手的牌，不看对手的行为；
/// - 面对下注：胜率低于本轮门槛、或低于“跟注额 ÷ (池＋跟注额)”就弃；高于加注门槛就加；其余跟注；
/// - 没人下注时不弃牌：够加注门槛就开注，否则过牌；
/// - 改航：换后的胜率比保留高出“改航费 ÷ 可争夺池＋余量”就换；
/// - 每局结束按结果调整门槛：弃了却本该最大 → 下调；打到底却输 → 上调；其余不变。每场开局归零，有上下限。
/// </summary>
public sealed class ThresholdBot(string name, ThresholdBotParameters parameters, EquityTable table) : IBot
{
    private double _offset;

    public string Name => name;

    /// <summary>当前对所有门槛的偏移（正数更保守）。</summary>
    public double Offset => _offset;

    public void StartMatch() => _offset = 0;

    public BetDecision DecideBet(BettingObservation observation)
    {
        var common = observation.Common;
        var legal = observation.Legal;
        int opponents = Math.Clamp(common.OpponentsInHand, 1, EquityTable.MaxOpponents);
        int street = common.Stage switch
        {
            HandStage.FirstBet => 0,
            HandStage.SecondBet => 1,
            _ => 2
        };
        double equity = EquityEstimator.Estimate(table, common.HoleCards, common.CommunityCards, common.DeadCards, opponents);
        double foldAt = Math.Clamp(parameters.Fold[street, opponents] + _offset, 0, 1);
        double raiseAt = Math.Clamp(parameters.Raise[street, opponents] + _offset, 0, 1);

        // 跟注额 ÷ (池＋跟注额) 是“不亏钱”的最低胜率；门槛表示在此之上还要多保守。没人下注时为 0。
        int toCall = legal.CanCall ? legal.CallAmount : 0;
        double breakEven = (double)toCall / (common.Pot + toCall);

        if (legal.CanRaise && equity >= Math.Max(raiseAt, breakEven))
        {
            // 目标额（本轮累计）＝跟齐之后，再加“跟注后池子”的一个比例；截到合法区间。
            int desired = observation.CommittedThisRound + toCall + (int)Math.Ceiling((common.Pot + toCall) * parameters.RaisePotFraction);
            return BetDecision.RaiseTo(Math.Clamp(desired, legal.MinRaiseTarget, legal.MaxRaiseTarget));
        }

        if (legal.CanCheck)
        {
            return BetDecision.Check;
        }

        if (equity < Math.Max(foldAt, breakEven))
        {
            return BetDecision.Fold;
        }
        return legal.CanCall ? BetDecision.Call : BetDecision.AllIn;
    }

    public bool DecideReroute(RerouteObservation observation)
    {
        var common = observation.Common;
        if (common.Stack < observation.Fee || observation.Exposure <= 0)
        {
            return false;
        }
        int opponents = Math.Clamp(common.OpponentsInHand, 1, EquityTable.MaxOpponents);
        var (keep, swap) = EquityEstimator.EvaluateReroute(table, common.HoleCards, common.CommunityCards, common.DeadCards, opponents);
        return swap - keep > (double)observation.Fee / observation.Exposure + parameters.RerouteMargin;
    }

    public void EndHand(HandOutcome outcome)
    {
        if (outcome.Folded && outcome.WouldHaveBeenBest == true)
        {
            _offset -= parameters.AdjustDown;
        }
        else if (!outcome.Folded && outcome.WentToShowdown && !outcome.WonAnyPot)
        {
            _offset += parameters.AdjustUp;
        }
        _offset = Math.Clamp(_offset, -parameters.AdjustLimit, parameters.AdjustLimit);
    }
}
