using DarkTide.Rules;

namespace DarkTide.Rules.Tests;

public class ShufflerTests
{
    [Fact]
    public void ShuffleReturnsExactlyTheStandardDeck()
    {
        var shuffled = Shuffler.ShuffleStandard();
        var standard = Deck.CreateStandard();

        Assert.Equal(27, shuffled.Count);
        Assert.Equal(27, shuffled.Distinct().Count());
        Assert.True(standard.ToHashSet().SetEquals(shuffled));
    }

    [Fact]
    public void ShuffledDeckDrawsCompletelyWithoutDuplicates()
    {
        var shuffled = Shuffler.ShuffleStandard();
        var pile = new DrawPile(shuffled);
        var drawn = new List<Card>();

        while (pile.DrawNext() is Card card)
        {
            drawn.Add(card);
        }

        Assert.Equal(shuffled.ToArray(), drawn.ToArray());
        Assert.Equal(27, drawn.Distinct().Count());
        Assert.Equal(0, pile.RemainingCount);
    }
}
