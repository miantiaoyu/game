using DarkTide.Rules;

namespace DarkTide.Bots;

/// <summary>
/// 对照用的固定策略。调参时必须同时和它们打：只和自己打，容易调出只会对付自己的参数。
/// 一个合格的人机应当稳定赢过它们；若“永远加注”能赢它，说明它太容易被吓跑。
/// </summary>
public sealed class CallingStationBot : IBot
{
    public string Name => "永远跟注";
    public void StartMatch() { }
    public BetDecision DecideBet(BettingObservation observation) =>
        observation.Legal.CanCheck ? BetDecision.Check : observation.Legal.CanCall ? BetDecision.Call : BetDecision.AllIn;
    public bool DecideReroute(RerouteObservation observation) => false;
    public void EndHand(HandOutcome outcome) { }
}

/// <summary>能加注就按最大额加注，否则跟注。</summary>
public sealed class ManiacBot : IBot
{
    public string Name => "永远加注";
    public void StartMatch() { }
    public BetDecision DecideBet(BettingObservation observation)
    {
        var legal = observation.Legal;
        if (legal.CanRaise)
        {
            return BetDecision.RaiseTo(legal.MaxRaiseTarget);
        }
        return legal.CanCheck ? BetDecision.Check : legal.CanCall ? BetDecision.Call : BetDecision.AllIn;
    }
    public bool DecideReroute(RerouteObservation observation) => false;
    public void EndHand(HandOutcome outcome) { }
}

/// <summary>在合法动作里均匀随机选。种子固定，结果可复现。</summary>
public sealed class RandomBot(ulong seed) : IBot
{
    private SplitMix64 _random = new(seed);

    public string Name => "随机";
    public void StartMatch() { }
    public BetDecision DecideBet(BettingObservation observation)
    {
        var legal = observation.Legal;
        var options = new List<BetDecision>();
        if (legal.CanCheck) options.Add(BetDecision.Check);
        if (legal.CanCall) options.Add(BetDecision.Call);
        if (legal.CanFold) options.Add(BetDecision.Fold);
        if (legal.CanRaise)
        {
            options.Add(BetDecision.RaiseTo(legal.MinRaiseTarget + _random.NextInt(legal.MaxRaiseTarget - legal.MinRaiseTarget + 1)));
        }
        if (options.Count == 0) options.Add(BetDecision.AllIn);
        return options[_random.NextInt(options.Count)];
    }
    public bool DecideReroute(RerouteObservation observation) =>
        observation.Common.Stack >= observation.Fee && _random.NextInt(2) == 0;
    public void EndHand(HandOutcome outcome) { }
}
