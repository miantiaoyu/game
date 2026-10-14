namespace DarkTide.Rules;

/// <summary>按固定顺序生成标准规则使用的完整牌库。</summary>
public static class Deck
{
    public const int TotalCards = 27;

    public static IReadOnlyList<Card> CreateStandard()
    {
        var cards = new List<Card>(TotalCards);

        // 外层每次选一种花色，内层为它生成 1～9。
        foreach (var suit in Enum.GetValues<Suit>())
        {
            for (int rank = Card.MinRank; rank <= Card.MaxRank; rank++)
            {
                cards.Add(new Card(suit, rank));
            }
        }

        // 只把只读视图交给调用者，原始列表留在方法内部。
        return cards.AsReadOnly();
    }
}
