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
    public void SettleHandWritesBackChipsWhenTotalIsConserved()
    {
        var match = new Match(3);
        match.StartNextHand();

        match.SettleHand(new[] { 150, 50, 100 });

        Assert.Equal(150, match.ChipsAt(0).Amount);
        Assert.Equal(50, match.ChipsAt(1).Amount);
        Assert.Empty(match.FindEliminatedSeats());
        Assert.False(match.IsDecided);
    }

    [Fact]
    public void SettleHandRejectsNonConservedOrInvalidResultsWithoutChangingChips()
    {
        var match = new Match(3);

        Assert.Throws<InvalidOperationException>(() => match.SettleHand(new[] { 100, 100, 100 }));
        match.StartNextHand();
        Assert.Throws<InvalidOperationException>(() => match.SettleHand(new[] { 101, 100, 100 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => match.SettleHand(new[] { 301, -1, 0 }));
        Assert.Throws<ArgumentException>(() => match.SettleHand(new[] { 150, 150 }));

        Assert.All(Enumerable.Range(0, 3), seat => Assert.Equal(100, match.ChipsAt(seat).Amount));
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
    public void ChipsCarryOverBetweenHandsThroughSnapshotAndSettle()
    {
        var match = new Match(3);
        match.StartNextHand();
        var hand = new Hand(3, Deck.CreateStandard(), match.SeatsWithChips(), match.CurrentAnte().Amount, match.ChipsSnapshot());
        hand.CollectAntes();

        // 整场只在开局取快照：一局进行中，整场筹码不变，底注不会被扣两次。
        Assert.Equal(100, match.ChipsAt(0).Amount);

        match.SettleHand(new[] { 97, 103, 100 });
        match.StartNextHand();
        Assert.Equal(new[] { 97, 103, 100 }, match.ChipsSnapshot());
    }

    [Fact]
    public void EliminatedSeatsAreExcludedFromTheNextLineup()
    {
        var match = new Match(3);
        match.StartNextHand();
        match.SettleHand(new[] { 200, 0, 100 });

        Assert.Equal(new[] { 1 }, match.FindEliminatedSeats());
        Assert.Equal(new[] { 0, 2 }, match.SeatsWithChips());
        // 快照是副本，修改它不影响整场。
        var snapshot = match.ChipsSnapshot();
        snapshot[0] = 0;
        Assert.Equal(200, match.ChipsAt(0).Amount);
    }

    [Fact]
    public void OutOfRangeSeatsAreRejected()
    {
        var match = new Match(3);

        Assert.Throws<ArgumentOutOfRangeException>(() => match.ChipsAt(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => match.ChipsAt(3));
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
