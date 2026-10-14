namespace DarkTide.Rules;

/// <summary>
/// 一个普通奖池层：金额，以及在结算时有资格争夺它的座位。
/// 已弃牌的玩家出过钱、钱留在这个池里，但不在 有资格座位 中。
/// </summary>
public sealed record Pot(Chips Total, IReadOnlyList<int> EligibleSeats);
