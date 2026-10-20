using DarkTide.Rules;

namespace DarkTide.Bots;

/// <summary>
/// 人机做一次决定时能看到的全部信息。只含该座位有权看到的内容：自己的底牌、公共牌、公开弃置的牌、
/// 公开的金额与人数。没有别人的底牌，也没有牌堆——人机的代码里拿不到 <see cref="Hand"/>，从类型上就偷看不了。
/// </summary>
public sealed record BotObservation
{
    public required int Seat { get; init; }
    public required IReadOnlyList<Card> HoleCards { get; init; }
    public required IReadOnlyList<Card> CommunityCards { get; init; }
    /// <summary>被改航换掉、公开弃置的牌。</summary>
    public required IReadOnlyList<Card> DeadCards { get; init; }
    /// <summary>做决定这一刻还没弃牌的对手数（含已全下的，他们仍参加摊牌）。</summary>
    public required int OpponentsInHand { get; init; }
    public required HandStage Stage { get; init; }
    public required int Ante { get; init; }
    public required int Stack { get; init; }
    /// <summary>
    /// 当前普通池总额（含本轮各人已投入，不含改航附加池）。用于把胜率换算成筹码。
    /// 有边池时本人未必都能赢到；第一版人机按总额近似。
    /// </summary>
    public required int Pot { get; init; }
    /// <summary>整场第几局（1～12）。</summary>
    public required int HandNumber { get; init; }
}

/// <summary>下注轮：规则允许的动作（与客户端按钮同一份来源），以及本人本轮已投入的金额。</summary>
public sealed record BettingObservation(BotObservation Common, LegalActions Legal, int CommittedThisRound);

/// <summary>改航轮：本人的冻结报价与可争夺池。</summary>
public sealed record RerouteObservation(BotObservation Common, int Fee, int Exposure);

/// <summary>人机给出的下注决定。加注时 <see cref="RaiseTarget"/> 是本轮累计目标额。</summary>
public readonly record struct BetDecision(BettingAction Action, int RaiseTarget = 0)
{
    public static BetDecision Check => new(BettingAction.Check);
    public static BetDecision Call => new(BettingAction.Call);
    public static BetDecision Fold => new(BettingAction.Fold);
    public static BetDecision AllIn => new(BettingAction.AllIn);
    public static BetDecision RaiseTo(int target) => new(BettingAction.Raise, target);
}

/// <summary>
/// 一局结束后人机能看到的结果，用于按结果调整下一局。
/// 只在真正摊牌、三张公共牌全部揭开时，才能判断“我弃掉的牌本来是不是最大”；否则 <see cref="WouldHaveBeenBest"/> 为空。
/// </summary>
public sealed record HandOutcome(bool Folded, bool WentToShowdown, bool WonAnyPot, bool? WouldHaveBeenBest);

/// <summary>
/// 一名人机。实现必须只依赖传入的观察，不能持有对局对象。
/// 每局开始前平台调用 <see cref="StartMatch"/>，每局结束后调用 <see cref="EndHand"/>。
/// </summary>
public interface IBot
{
    string Name { get; }
    void StartMatch();
    BetDecision DecideBet(BettingObservation observation);
    bool DecideReroute(RerouteObservation observation);
    void EndHand(HandOutcome outcome);
}
