namespace DarkTide.Rules;

/// <summary>单个玩家可以看到的底牌信息；不持有内部局面或牌堆的引用。</summary>
public sealed class PlayerHandView
{
    public int Seat { get; }
    public IReadOnlyList<Card> HoleCards { get; }
    public IReadOnlyList<SeatCardSummary> OtherSeats { get; }

    /// <summary>
    /// record 生成的 ToString 会打印底牌。视图可能出现在日志或异常里，
    /// 所以只输出数量与座位，不输出牌面。
    /// </summary>
    public override string ToString() =>
        $"{nameof(PlayerHandView)} {{ {nameof(Seat)} = {Seat}, " +
        $"{nameof(HoleCards)} = {HoleCards.Count} cards, {nameof(OtherSeats)} = {OtherSeats.Count} seats }}";

    internal PlayerHandView(
        int Seat,
        IReadOnlyList<Card> HoleCards,
        IReadOnlyList<SeatCardSummary> OtherSeats)
    {
        this.Seat = Seat;
        this.HoleCards = Array.AsReadOnly(HoleCards.ToArray());
        this.OtherSeats = Array.AsReadOnly(OtherSeats.ToArray());
    }
}
