using DarkTide.Rules;

namespace DarkTide.Rules.Tests;

public class CardTests
{
    [Theory]
    [InlineData(Suit.Red, 1, 1)]
    [InlineData(Suit.Red, 7, 7)]
    [InlineData(Suit.Red, 9, 9)]
    [InlineData(Suit.Blue, 1, 10)]
    [InlineData(Suit.Green, 9, 27)]
    public void ValidCardRoundTripsSuitRankAndId(Suit expectedSuit, int expectedRank, int expectedId)
    {
        var card = new Card(expectedSuit, expectedRank);

        Assert.Equal(expectedSuit, card.Suit);
        Assert.Equal(expectedRank, card.Rank);
        Assert.Equal((byte)expectedId, card.Id);
        Assert.True(card.IsValid);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(int.MaxValue)]
    public void OutOfRangeRankIsRejected(int invalidRank)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Card(Suit.Red, invalidRank));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(255)]
    public void UndefinedSuitIsRejected(int invalidSuit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Card((Suit)invalidSuit, 1));
    }

    [Fact]
    public void CardsWithSameSuitAndRankAreEqual()
    {
        Assert.Equal(new Card(Suit.Red, 7), new Card(Suit.Red, 7));
        Assert.NotEqual(new Card(Suit.Red, 7), new Card(Suit.Blue, 7));
        Assert.NotEqual(new Card(Suit.Red, 7), new Card(Suit.Red, 8));
    }

    [Fact]
    public void DefaultCardIsUninitialized()
    {
        var uninitialized = default(Card);

        Assert.False(uninitialized.IsValid);
        Assert.DoesNotContain(uninitialized, Deck.CreateStandard());
        Assert.Throws<InvalidOperationException>(() => uninitialized.Suit);
        Assert.Throws<InvalidOperationException>(() => uninitialized.Rank);
        Assert.Equal("未初始化的牌", uninitialized.ToString());
    }
}
