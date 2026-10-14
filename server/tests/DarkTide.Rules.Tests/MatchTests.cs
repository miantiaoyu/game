using DarkTide.Rules;

namespace DarkTide.Rules.Tests;

public class MatchTests
{
    [Fact]
    public void MatchStartsWithStandardChipsAndNoHand()
    {
        var match = new Match(5);

        Assert.Equal(5, match.SeatCount);
        Assert.Equal(0, match.CurrentHandNumber);
        Assert.False(match.AllHandsPlayed);
        for (int seat = 0; seat < 5; seat++)
        {
            Assert.Equal(100, match.ChipsAt(seat).Amount);
        }
        Assert.Throws<InvalidOperationException>(() => match.CurrentAnte());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(6, 1)]
    [InlineData(7, 2)]
    [InlineData(12, 2)]
    public void AnteIsOneForFirstHalfAndTwoForSecond(int handNumber, int expectedAnte)
    {
        var match = New(handCount: handNumber);

        Assert.Equal(handNumber, match.CurrentHandNumber);
        Assert.Equal(expectedAnte, match.CurrentAnte().Amount);
    }

    [Fact]
    public void PayingAnteIsNotElimination()
    {
        var match = new Match(3);
        match.StartNextHand();
        match.CollectAntes(new[] { 0, 1, 2 });

        // 交底注只是少了一枚筹码，不构成淘汰；只有结算后为零才算。
        Assert.Equal(99, match.ChipsAt(0).Amount);
        Assert.Empty(match.FindEliminatedSeats());
        Assert.False(match.IsDecided);
        Assert.Equal(3, match.CountSeatsWithChips());
    }

    [Fact]
    public void MatchIsDecidedWhenOnlyOneSeatHasChips()
    {
        var match = new Match(3, initialChips: 0);
        match.AddChips(2, new Chips(7));

        Assert.True(match.IsDecided);
        Assert.Equal(new[] { 0, 1 }, match.FindEliminatedSeats());
        Assert.Equal(1, match.CountSeatsWithChips());
        Assert.Equal(7, match.ChipsAt(2).Amount);
    }

    [Fact]
    public void ChipsCarryOverBetweenHands()
    {
        var match = new Match(3);
        match.StartNextHand();
        match.CollectAntes(new[] { 0, 1, 2 });
        match.AddChips(1, new Chips(3));

        Assert.Equal(99, match.ChipsAt(0).Amount);
        Assert.Equal(102, match.ChipsAt(1).Amount);

        match.StartNextHand();
        match.CollectAntes(new[] { 0, 1, 2 });

        Assert.Equal(98, match.ChipsAt(0).Amount);
        Assert.Equal(101, match.ChipsAt(1).Amount);
    }

    [Fact]
    public void ShortAntePaysEverythingWithoutNegativeBalance()
    {
        var match = New(handCount: 7, initialChips: 1);

        // 第七局底注为 2，但每人只有 1 枚。
        match.CollectAntes(new[] { 0, 1 });

        Assert.Equal(0, match.ChipsAt(0).Amount);
        Assert.Equal(0, match.ChipsAt(1).Amount);
    }

    [Fact]
    public void DuplicateZeroChipAndOutOfRangeSeatsAreRejected()
    {
        var match = new Match(3);
        match.StartNextHand();

        Assert.Throws<ArgumentException>(() => match.CollectAntes(new[] { 0, 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => match.CollectAntes(new[] { 0, 3 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => match.ChipsAt(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => match.ChipsAt(3));

        var emptyMatch = new Match(3, initialChips: 0);
        emptyMatch.StartNextHand();
        Assert.Throws<ArgumentException>(() => emptyMatch.CollectAntes(new[] { 0, 1 }));
    }

    [Fact]
    public void CannotStartAnotherHandAfterLastOne()
    {
        var match = New(handCount: 12);

        Assert.True(match.AllHandsPlayed);
        Assert.Throws<InvalidOperationException>(() => match.StartNextHand());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void UnsupportedSeatCountIsRejected(int seatCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Match(seatCount));
    }

    private static Match New(int handCount = 0, int initialChips = 100)
    {
        var match = new Match(5, initialChips);
        for (int index = 0; index < handCount; index++)
        {
            match.StartNextHand();
        }
        return match;
    }
}
