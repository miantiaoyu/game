namespace DarkTide.Rules;

/// <summary>
/// 牌桌规模的共同上限。放在单独的类型里，是因为牌桌容量属于“桌”，
/// 不属于某一局或某一轮：Match、Hand、BettingRound、Room 都要校验它，
/// 若挂在 Hand 上，其他类型引用 Hand.MinSeats 就成了巧合式耦合。
/// </summary>
public static class TableLimits
{
    public const int MinSeats = 2;
    public const int MaxSeats = 8;

    /// <summary>数值与 <see cref="MinSeats"/> 相同，但含义不同：一个是牌桌容量，一个是本局人数。</summary>
    public const int MinParticipants = 2;
}
