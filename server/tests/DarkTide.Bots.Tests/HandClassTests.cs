using DarkTide.Rules;

namespace DarkTide.Bots.Tests;

public class HandClassTests
{
    [Theory]
    [InlineData(2, 81, 351)]
    [InlineData(3, 1_629, 8_775)]
    [InlineData(4, 18_288, 105_300)]
    [InlineData(5, 137_016, 807_300)]
    public void ClassCountsMatchAnIndependentCount(int knownCards, int classes, int rawStates)
    {
        // 期望值由一段独立的 Python 脚本数出（枚举全部局面、试遍 6 种花色重排取最小）。
        // 原始局面数 = C(27,2) × C(25, 已知公共牌数)。
        var all = HandClass.Enumerate(knownCards);

        Assert.Equal(classes, all.Count);
        Assert.Equal(rawStates, all.Sum(entry => entry.Weight));
        Assert.All(all, entry => Assert.Equal(knownCards, HandClass.KnownCards(entry.Key)));
    }

    [Fact]
    public void SuitRelabellingAndCardOrderDoNotChangeTheClass()
    {
        var random = new Random(20261016);
        var suitMaps = new[] { new[] { 1, 2, 0 }, new[] { 2, 1, 0 }, new[] { 0, 2, 1 } };
        for (int round = 0; round < 5000; round++)
        {
            var cards = Deck.CreateStandard().OrderBy(_ => random.Next()).Take(2 + random.Next(4)).ToArray();
            var hole = cards[..2];
            var board = cards[2..];
            uint key = HandClass.Canonicalize(hole, board);

            foreach (var map in suitMaps)
            {
                Card Swap(Card card) => new((Suit)map[(int)card.Suit], card.Rank);
                Assert.Equal(key, HandClass.Canonicalize(hole.Select(Swap).Reverse().ToArray(), board.Select(Swap).Reverse().ToArray()));
            }

            // 代表局面本身就是自己的代表。
            var (decodedHole, decodedBoard) = HandClass.Decode(key);
            Assert.Equal(key, HandClass.Canonicalize(decodedHole, decodedBoard));
        }
    }

    [Fact]
    public void DifferentRanksOrSuitPatternsAreDifferentClasses()
    {
        var red9 = new Card(Suit.Red, 9);
        var blue9 = new Card(Suit.Blue, 9);
        var red8 = new Card(Suit.Red, 8);

        // 9 对与同花 98 不是一类；同花 98 与杂色 98 也不是一类。
        Assert.NotEqual(HandClass.Canonicalize([red9, blue9], []), HandClass.Canonicalize([red9, red8], []));
        Assert.NotEqual(HandClass.Canonicalize([red9, red8], []), HandClass.Canonicalize([blue9, red8], []));
        // 公共牌与底牌同花与否也会区分。
        var green5 = new Card(Suit.Green, 5);
        var red5 = new Card(Suit.Red, 5);
        Assert.NotEqual(HandClass.Canonicalize([red9, red8], [red5]), HandClass.Canonicalize([red9, red8], [green5]));
    }

    [Fact]
    public void InvalidInputsAreRejected()
    {
        var a = new Card(Suit.Red, 1);
        var b = new Card(Suit.Red, 2);
        Assert.Throws<ArgumentException>(() => HandClass.Canonicalize([a], []));
        Assert.Throws<ArgumentException>(() => HandClass.Canonicalize([a, a], []));
        Assert.Throws<ArgumentException>(() => HandClass.Canonicalize([a, b], [a]));
        Assert.Throws<ArgumentException>(() => HandClass.Canonicalize([a, b], [default]));
        Assert.Throws<ArgumentOutOfRangeException>(() => HandClass.Enumerate(6));
    }
}
