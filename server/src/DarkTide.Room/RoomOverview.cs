using DarkTide.Rules;

namespace DarkTide.Room;

/// <summary>
/// 房间的公开信息快照，任何人都可以看到，不含任何人的底牌（摊牌时公开的除外）。
/// 与底牌视图配合使用：座位有人但不在参与顺序里，就是“已入座、本局未参与”；座位没人就是空座。
/// 客户端提交命令时，把这里的状态版本作为预期版本带上。
/// </summary>
/// <param name="MatchChips">整场筹码：一局进行中是本局开始时的余额，结算后是结算结果。</param>
/// <param name="HandsPlayed">已开始的局数（含进行中的一局）。</param>
/// <param name="Hand">当前或刚结算的一局；还没发过牌时为空。</param>
public sealed record RoomOverview(
    Guid RoomId,
    RoomStage Stage,
    long StateVersion,
    IReadOnlyList<bool> SeatOccupied,
    IReadOnlyList<int> ParticipantOrder,
    IReadOnlyList<int> MatchChips,
    int HandsPlayed,
    HandOverview? Hand);

/// <summary>
/// 一局的公开状态。所有数组按座位号索引；不参加本局的座位，投入为 0、未弃牌、余额为整场筹码。
/// 合法动作只在下注进行中给出，属于公开信息（只由公开的金额与加价权决定），客户端据此显示按钮。
/// </summary>
public sealed record HandOverview(
    int HandNumber,
    int Ante,
    HandStage Stage,
    IReadOnlyList<Card> CommunityCards,
    IReadOnlyList<Card> DiscardedCards,
    IReadOnlyList<int> Stacks,
    IReadOnlyList<int> Contributions,
    IReadOnlyList<int> CommittedThisRound,
    IReadOnlyList<int> RerouteFees,
    IReadOnlyList<bool> Folded,
    int ActingSeat,
    LegalActions? LegalActions,
    IReadOnlyList<RerouteQuote> RerouteQuotes,
    int RerouteActingSeat,
    SettlementOverview? Settlement);

/// <summary>
/// 结算的公开结果。摊牌时未弃牌者的底牌公开（规则第 3 节）；没有摊牌时谁的牌都不公开，<see cref="ShownHoleCards"/> 为空。
/// </summary>
public sealed record SettlementOverview(
    IReadOnlyList<int> FinalStacks,
    IReadOnlyList<PotResult> Pots,
    IReadOnlyList<PotResult> BonusPots,
    bool Showdown,
    IReadOnlyDictionary<int, IReadOnlyList<Card>> ShownHoleCards);
