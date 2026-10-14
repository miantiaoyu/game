namespace DarkTide.Rules;

/// <summary>单个玩家可以看到的底牌信息；不持有内部局面或牌堆的引用。</summary>
public sealed class PlayerHandView
{
    public int Seat { get; }
    public IReadOnlyList<Card> HoleCards { get; }
    public IReadOnlyList<SeatCardSummary> OtherSeats { get; }

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
