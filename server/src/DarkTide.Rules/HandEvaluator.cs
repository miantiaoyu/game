namespace DarkTide.Rules;

/// <summary>
/// 牌型判定（规则第 2 节）。纯函数：不读不改任何状态。
///
/// 从两张底牌和三张公共牌里选三张，至少用一张底牌，共九种组合，取最强的一组。
/// 五张牌任选三张有 C(5,3)=10 种，去掉“三张都是公共牌”那一种，正好九种。
/// 每次摊牌每人只算九次，不需要查表或缓存。
/// </summary>
public static class HandEvaluator
{
    /// <summary>三张牌的牌型与比较值。数字 1 只作最低值，不能接在 9 后面组成顺子。</summary>
    public static HandValue EvaluateThree(Card first, Card second, Card third)
    {
        // 三张牌各不相同（同一副牌里不会有重复）；这里只按数字降序排一下，三张手写比较即可。
        int a = first.Rank, b = second.Rank, c = third.Rank;
        if (a < b) (a, b) = (b, a);
        if (b < c) (b, c) = (c, b);
        if (a < b) (a, b) = (b, a);

        bool flush = first.Suit == second.Suit && second.Suit == third.Suit;

        if (a == c)
        {
            return HandValue.Create(HandCategory.ThreeOfAKind, a);
        }
        // a > b > c 且首尾差 2，就是三个连续数字。1 只能当最低，所以 9-1-2 这类不会被当作顺子。
        bool straight = a - c == 2 && a != b && b != c;
        if (straight)
        {
            return HandValue.Create(flush ? HandCategory.StraightFlush : HandCategory.Straight, a);
        }
        if (flush)
        {
            return HandValue.Create(HandCategory.Flush, a, b, c);
        }
        if (a == b)
        {
            return HandValue.Create(HandCategory.Pair, a, c);
        }
        if (b == c)
        {
            return HandValue.Create(HandCategory.Pair, b, a);
        }
        return HandValue.Create(HandCategory.HighCard, a, b, c);
    }

    /// <summary>两张底牌加三张公共牌的最强合法组合（至少使用一张底牌）。</summary>
    public static HandValue EvaluateBest(IReadOnlyList<Card> holeCards, IReadOnlyList<Card> communityCards)
    {
        ArgumentNullException.ThrowIfNull(holeCards);
        ArgumentNullException.ThrowIfNull(communityCards);
        if (holeCards.Count != Hand.HoleCardsPerPlayer || communityCards.Count != Hand.CommunityCardCount)
        {
            throw new ArgumentException($"需要 {Hand.HoleCardsPerPlayer} 张底牌和 {Hand.CommunityCardCount} 张公共牌。");
        }

        Card h0 = holeCards[0], h1 = holeCards[1];
        Card c0 = communityCards[0], c1 = communityCards[1], c2 = communityCards[2];

        // 一张底牌 + 两张公共牌：2 × 3 = 6 种。
        var best = Max(
            EvaluateThree(h0, c0, c1), EvaluateThree(h0, c0, c2), EvaluateThree(h0, c1, c2),
            EvaluateThree(h1, c0, c1), EvaluateThree(h1, c0, c2), EvaluateThree(h1, c1, c2));
        // 两张底牌 + 一张公共牌：3 种。
        return Max(best, Max(EvaluateThree(h0, h1, c0), EvaluateThree(h0, h1, c1), EvaluateThree(h0, h1, c2)));
    }

    private static HandValue Max(HandValue first, HandValue second) => first >= second ? first : second;

    private static HandValue Max(params ReadOnlySpan<HandValue> values)
    {
        var best = values[0];
        for (int i = 1; i < values.Length; i++)
        {
            best = Max(best, values[i]);
        }
        return best;
    }
}
