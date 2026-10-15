namespace DarkTide.Rules;

/// <summary>
/// 一局的结算结果。<see cref="FinalStacks"/> 按座位号索引，可以直接交给 <see cref="Match.SettleHand"/>。
/// <see cref="Showdown"/> 为 false 表示只剩一人未弃牌、没有比牌，此时 <see cref="HandValues"/> 全为 null，
/// 赢家的牌也不公开。
/// </summary>
public sealed record HandSettlement(
    IReadOnlyList<int> FinalStacks,
    IReadOnlyList<PotResult> Pots,
    bool Showdown,
    IReadOnlyList<HandValue?> HandValues);
