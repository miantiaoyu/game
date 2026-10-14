using DarkTide.Rules;

namespace DarkTide.Rules.Tests;

public class DeckTests
{
    [Fact]
    public void StandardDeckIsCompleteAndUnique()
    {
        var allCards = Deck.CreateStandard();

        Assert.Equal(27, allCards.Count);
        Assert.Equal(27, allCards.Distinct().Count());

        foreach (var suit in Enum.GetValues<Suit>())
        {
            Assert.Equal(9, allCards.Count(card => card.Suit == suit));
        }

        foreach (var rank in Enumerable.Range(1, 9))
        {
            Assert.Equal(3, allCards.Count(card => card.Rank == rank));
        }
    }
}
