using DarkTide.Rules;

namespace DarkTide.Bots;

/// <summary>
/// 牌力查表：任意三张牌 → 牌力名次（0～248，越大越强，相等即并列）。
///
/// 27 张牌任选三张只有 2925 种组合、249 种不同强弱，所以名次放得进一个字节；
/// 按 27×27×27 排开是 19,683 字节，整张表能留在 CPU 一级缓存里。
/// 表由 <see cref="HandEvaluator.EvaluateThree"/> 在首次使用时生成，所以和规则层的判定不会出现两份口径。
///
/// 规则层摊牌每人只算九次，直接调用判定函数就够了；这里要为胜率表算几十亿次，才值得查表。
/// </summary>
internal static class StrengthTable
{
    public const int DistinctStrengths = 249;

    // 下标 = (id1-1)*729 + (id2-1)*27 + (id3-1)；三张顺序任意，六种排列都填。
    private static readonly byte[] Table = Build();

    private static byte[] Build()
    {
        var deck = Deck.CreateStandard();
        var values = new List<(int A, int B, int C, HandValue Value)>();
        for (int i = 0; i < deck.Count; i++)
        for (int j = i + 1; j < deck.Count; j++)
        for (int k = j + 1; k < deck.Count; k++)
        {
            values.Add((deck[i].Id, deck[j].Id, deck[k].Id, HandEvaluator.EvaluateThree(deck[i], deck[j], deck[k])));
        }

        var ranks = values.Select(entry => entry.Value).Distinct().Order().ToArray();
        if (ranks.Length != DistinctStrengths)
        {
            throw new InvalidOperationException($"牌力种类应为 {DistinctStrengths}，实际 {ranks.Length}：规则判定变了，胜率表需要重新生成。");
        }
        var rankOf = ranks.Select((value, index) => (value, index)).ToDictionary(pair => pair.value, pair => (byte)pair.index);

        var table = new byte[27 * 27 * 27];
        foreach (var (a, b, c, value) in values)
        {
            byte rank = rankOf[value];
            foreach (var (x, y, z) in new[] { (a, b, c), (a, c, b), (b, a, c), (b, c, a), (c, a, b), (c, b, a) })
            {
                table[(x - 1) * 729 + (y - 1) * 27 + (z - 1)] = rank;
            }
        }
        return table;
    }

    internal static ReadOnlySpan<byte> RawForFingerprint() => Table;

    /// <summary>三张牌的名次。参数是牌的编号（1～27），互不相同。</summary>
    public static int Three(int a, int b, int c) => Table[(a - 1) * 729 + (b - 1) * 27 + (c - 1)];

    /// <summary>两张底牌 + 三张公共牌的最强合法组合名次（至少一张底牌，九选一）。</summary>
    public static int Best(int h0, int h1, int c0, int c1, int c2)
    {
        var t = Table;
        int x0 = (h0 - 1) * 729, x1 = (h1 - 1) * 729;
        // 一张底牌 + 两张公共牌：公共牌两两组合 (c0,c1)、(c0,c2)、(c1,c2)。
        int y0 = (c0 - 1) * 27, y1 = (c1 - 1) * 27;
        int best = t[x0 + y0 + (c1 - 1)];
        best = Math.Max(best, t[x0 + y0 + (c2 - 1)]);
        best = Math.Max(best, t[x0 + y1 + (c2 - 1)]);
        best = Math.Max(best, t[x1 + y0 + (c1 - 1)]);
        best = Math.Max(best, t[x1 + y0 + (c2 - 1)]);
        best = Math.Max(best, t[x1 + y1 + (c2 - 1)]);
        int hh = x0 + (h1 - 1) * 27;
        best = Math.Max(best, t[hh + (c0 - 1)]);
        best = Math.Max(best, t[hh + (c1 - 1)]);
        best = Math.Max(best, t[hh + (c2 - 1)]);
        return best;
    }
}
