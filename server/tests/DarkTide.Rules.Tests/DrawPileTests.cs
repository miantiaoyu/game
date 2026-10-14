using DarkTide.Rules;

namespace DarkTide.Rules.Tests;

public class DrawPileTests
{
    [Fact]
    public void DrawsInGivenOrderAndDecrementsRemaining()
    {
        var expectedOrder = Deck.CreateStandard().Reverse().ToArray();
        var pile = new DrawPile(expectedOrder);

        Assert.Equal(27, pile.RemainingCount);
        for (int index = 0; index < expectedOrder.Length; index++)
        {
            Assert.Equal<Card?>(expectedOrder[index], pile.DrawNext());
            Assert.Equal(26 - index, pile.RemainingCount);
        }
    }

    [Fact]
    public void DrawingPastEndReturnsNullAndKeepsCount()
    {
        var pile = new DrawPile(Deck.CreateStandard());
        for (int times = 0; times < 27; times++)
        {
            Assert.NotNull(pile.DrawNext());
        }

        for (int times = 0; times < 3; times++)
        {
            Assert.Null(pile.DrawNext());
            Assert.Equal(0, pile.RemainingCount);
        }
    }

    [Fact]
    public void MutatingSourceArrayDoesNotChangePile()
    {
        var inputOrder = Deck.CreateStandard().ToArray();
        var pile = new DrawPile(inputOrder);

        Array.Reverse(inputOrder);

        Assert.Equal<Card?>(new Card(Suit.Red, 1), pile.DrawNext());
        Assert.Equal<Card?>(new Card(Suit.Red, 2), pile.DrawNext());
    }

    [Fact]
    public void TwoPilesFromSameOrderAreIndependent()
    {
        var sharedOrder = Deck.CreateStandard();
        var first = new DrawPile(sharedOrder);
        var second = new DrawPile(sharedOrder);

        first.DrawNext();
        first.DrawNext();

        Assert.Equal(25, first.RemainingCount);
        Assert.Equal(27, second.RemainingCount);
        Assert.Equal<Card?>(new Card(Suit.Red, 1), second.DrawNext());
        Assert.Equal<Card?>(new Card(Suit.Red, 3), first.DrawNext());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(26)]
    [InlineData(28)]
    public void DeckWithWrongSizeIsRejected(int Amount)
    {
        var inputOrder = Deck.CreateStandard()
            .Append(new Card(Suit.Red, 1)).Take(Amount).ToArray();

        Assert.Throws<ArgumentException>(() => new DrawPile(inputOrder));
    }

    [Fact]
    public void DeckWithDuplicatesIsRejected()
    {
        var inputOrder = Deck.CreateStandard().ToArray();
        inputOrder[26] = inputOrder[0];

        Assert.Throws<ArgumentException>(() => new DrawPile(inputOrder));
    }

    [Fact]
    public void DeckWithUninitializedCardIsRejected()
    {
        // 数量正确、也没有重复，只靠查重会放过它。
        var inputOrder = Deck.CreateStandard().ToArray();
        inputOrder[26] = default;

        Assert.Throws<ArgumentException>(() => new DrawPile(inputOrder));
    }

    [Fact]
    public void NullDeckIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new DrawPile(null!));
    }
}
