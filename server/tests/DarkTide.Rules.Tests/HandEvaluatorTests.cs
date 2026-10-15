using DarkTide.Rules;

namespace DarkTide.Rules.Tests;

public class HandEvaluatorTests
{
    private static Card C(Suit suit, int rank) => new(suit, rank);
    private const Suit R = Suit.Red, B = Suit.Blue, G = Suit.Green;

    private static HandValue Three(Card a, Card b, Card c) => HandEvaluator.EvaluateThree(a, b, c);

    [Fact]
    public void CategoriesFollowTheRuleExamples()
    {
        Assert.Equal(HandCategory.ThreeOfAKind, Three(C(R, 4), C(B, 4), C(G, 4)).Category);
        Assert.Equal(HandCategory.StraightFlush, Three(C(R, 3), C(R, 4), C(R, 5)).Category);
        Assert.Equal(HandCategory.Straight, Three(C(R, 3), C(B, 4), C(R, 5)).Category);
        Assert.Equal(HandCategory.Flush, Three(C(G, 2), C(G, 7), C(G, 9)).Category);
        Assert.Equal(HandCategory.Pair, Three(C(R, 6), C(B, 6), C(G, 2)).Category);
        Assert.Equal(HandCategory.HighCard, Three(C(R, 9), C(B, 5), C(G, 2)).Category);
    }

    [Fact]
    public void CategoryOutranksAnyNumbers()
    {
        // 任何三条都大于任何同花顺：最小的三条 111 也大于最大的同花顺 789。
        Assert.True(Three(C(R, 1), C(B, 1), C(G, 1)) > Three(C(R, 7), C(R, 8), C(R, 9)));
        Assert.True(Three(C(R, 1), C(R, 2), C(R, 3)) > Three(C(R, 7), C(B, 8), C(G, 9)));
        Assert.True(Three(C(R, 1), C(B, 2), C(G, 3)) > Three(C(G, 4), C(G, 8), C(G, 9)));
        Assert.True(Three(C(G, 1), C(G, 2), C(G, 4)) > Three(C(R, 9), C(B, 9), C(G, 8)));
        Assert.True(Three(C(R, 1), C(B, 1), C(G, 2)) > Three(C(R, 9), C(B, 8), C(G, 6)));
    }

    [Fact]
    public void OneIsOnlyLowAndStraightsDoNotWrap()
    {
        Assert.Equal(HandCategory.Straight, Three(C(R, 1), C(B, 2), C(G, 3)).Category);
        // 891、912 都不是顺子。
        Assert.Equal(HandCategory.HighCard, Three(C(R, 8), C(B, 9), C(G, 1)).Category);
        Assert.Equal(HandCategory.HighCard, Three(C(R, 9), C(B, 1), C(G, 2)).Category);
        Assert.Equal(HandCategory.Flush, Three(C(R, 9), C(R, 1), C(R, 2)).Category);
        // 123 是最小的顺子，789 是最大的。
        Assert.True(Three(C(R, 1), C(B, 2), C(G, 3)) < Three(C(R, 2), C(B, 3), C(G, 4)));
        Assert.True(Three(C(R, 7), C(B, 8), C(G, 9)) > Three(C(R, 6), C(B, 7), C(G, 8)));
    }

    [Fact]
    public void SameCategoryComparesNumbersInTheRuleOrder()
    {
        // 对子：先比对子数字，再比第三张。
        Assert.True(Three(C(R, 7), C(B, 7), C(G, 1)) > Three(C(R, 6), C(B, 6), C(G, 9)));
        Assert.True(Three(C(R, 7), C(B, 7), C(G, 5)) > Three(C(G, 7), C(B, 7), C(R, 4)));
        // 同花、散牌：从大到小依次比较。
        Assert.True(Three(C(R, 9), C(R, 5), C(R, 2)) > Three(C(B, 9), C(B, 4), C(B, 3)));
        Assert.True(Three(C(R, 9), C(B, 6), C(G, 2)) > Three(C(R, 9), C(B, 5), C(G, 4)));
        Assert.True(Three(C(R, 9), C(B, 6), C(G, 3)) > Three(C(G, 9), C(R, 6), C(B, 2)));
        // 三条、顺子类只比最高数字。
        Assert.True(Three(C(R, 5), C(B, 5), C(G, 5)) > Three(C(R, 4), C(B, 4), C(G, 4)));
        Assert.Equal(new[] { 7, 3 }, Three(C(R, 3), C(B, 7), C(G, 7)).Ranks);
        Assert.Equal(new[] { 9, 5, 2 }, Three(C(R, 2), C(B, 9), C(G, 5)).Ranks);
    }

    [Fact]
    public void SuitsNeverBreakTies()
    {
        Assert.Equal(Three(C(R, 3), C(B, 4), C(G, 5)), Three(C(G, 3), C(R, 4), C(B, 5)));
        Assert.Equal(Three(C(R, 9), C(R, 5), C(R, 2)), Three(C(G, 9), C(G, 5), C(G, 2)));
        Assert.Equal(Three(C(R, 8), C(B, 8), C(G, 3)), Three(C(G, 8), C(B, 8), C(R, 3)));
    }

    [Fact]
    public void ExhaustiveCountsMatchThePythonReference()
    {
        // 27 张牌任选 3 张共 2925 种。各牌型数量与 simulation/three_suit_analysis.py 精确计数一致，
        // 不同的比较值共 249 个。两套独立实现得出同样的数，说明判定与比较规则没有遗漏。
        var deck = Deck.CreateStandard();
        var counts = new Dictionary<HandCategory, int>();
        var distinct = new HashSet<HandValue>();
        for (int i = 0; i < deck.Count; i++)
        for (int j = i + 1; j < deck.Count; j++)
        for (int k = j + 1; k < deck.Count; k++)
        {
            var value = Three(deck[i], deck[j], deck[k]);
            counts[value.Category] = counts.GetValueOrDefault(value.Category) + 1;
            distinct.Add(value);
            // 结果与传入顺序无关。
            Assert.Equal(value, Three(deck[k], deck[i], deck[j]));
        }

        Assert.Equal(1848, counts[HandCategory.HighCard]);
        Assert.Equal(648, counts[HandCategory.Pair]);
        Assert.Equal(231, counts[HandCategory.Flush]);
        Assert.Equal(168, counts[HandCategory.Straight]);
        Assert.Equal(21, counts[HandCategory.StraightFlush]);
        Assert.Equal(9, counts[HandCategory.ThreeOfAKind]);
        Assert.Equal(249, distinct.Count);
    }

    [Fact]
    public void BestHandMustUseAtLeastOneHoleCard()
    {
        // 公共牌本身是三条 5，但不能三张全用公共牌。
        var hole = new[] { C(R, 1), C(B, 9) };
        var board = new[] { C(R, 5), C(B, 5), C(G, 5) };

        var best = HandEvaluator.EvaluateBest(hole, board);

        Assert.Equal(HandCategory.Pair, best.Category);
        Assert.Equal(new[] { 5, 9 }, best.Ranks);
    }

    [Fact]
    public void BestHandCanUseTwoHoleCards()
    {
        var hole = new[] { C(R, 8), C(B, 8) };
        var board = new[] { C(G, 8), C(R, 2), C(B, 4) };

        Assert.Equal(HandCategory.ThreeOfAKind, HandEvaluator.EvaluateBest(hole, board).Category);
    }

    [Fact]
    public void BestOfNineMatchesABruteForceReference()
    {
        // 随机局面下与“列出全部三张组合、过滤掉纯公共牌、取最大”的直接写法对照。
        var random = new Random(20261015);
        for (int round = 0; round < 20000; round++)
        {
            var cards = Deck.CreateStandard().OrderBy(_ => random.Next()).Take(5).ToArray();
            var hole = cards[..2];
            var board = cards[2..];

            var expected = Combinations(cards)
                .Where(triple => triple.Any(card => hole.Contains(card)))
                .Max(triple => Three(triple[0], triple[1], triple[2]));

            Assert.Equal(expected, HandEvaluator.EvaluateBest(hole, board));
        }
    }

    [Fact]
    public void WrongCardCountsAreRejected()
    {
        var board = new[] { C(R, 1), C(R, 2), C(R, 3) };
        Assert.Throws<ArgumentException>(() => HandEvaluator.EvaluateBest(new[] { C(B, 1) }, board));
        Assert.Throws<ArgumentException>(() => HandEvaluator.EvaluateBest(new[] { C(B, 1), C(B, 2) }, board[..2]));
    }

    private static IEnumerable<Card[]> Combinations(Card[] cards)
    {
        for (int i = 0; i < cards.Length; i++)
        for (int j = i + 1; j < cards.Length; j++)
        for (int k = j + 1; k < cards.Length; k++)
        {
            yield return new[] { cards[i], cards[j], cards[k] };
        }
    }
}
