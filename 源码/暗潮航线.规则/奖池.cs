namespace 暗潮航线.规则;

/// <summary>
/// 一个普通奖池层：金额，以及在结算时有资格争夺它的座位。
/// 已弃牌的玩家出过钱、钱留在这个池里，但不在 有资格座位 中。
/// </summary>
public sealed record 奖池(筹码 金额, IReadOnlyList<int> 有资格座位);
