namespace DarkTide.Rules;

/// <summary>一个其他座位的公开底牌信息，只包含编号和数量。</summary>
public readonly record struct SeatCardSummary(int seat, int CardCount);
