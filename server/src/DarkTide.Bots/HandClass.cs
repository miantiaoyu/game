using DarkTide.Rules;

namespace DarkTide.Bots;

/// <summary>
/// 已知牌局面的“等价类”：两张底牌加 0～3 张公共牌，按花色对称合并。
///
/// 三种花色地位相同（规则第 2 节），把所有红牌换成蓝牌、蓝牌换成红牌，胜率不变。
/// 所以对一个局面试遍 3! = 6 种花色重排，取编码最小的那个作为代表，
/// 胜率表只需为代表存一行：已知 5 张时从 807,300 种局面降到 137,016 行。
///
/// 底牌之间、公共牌之间的顺序也不影响胜率，编码时各自排序。
/// </summary>
public static class HandClass
{
    public const int MinKnownCards = Hand.HoleCardsPerPlayer;
    public const int MaxKnownCards = Hand.HoleCardsPerPlayer + Hand.CommunityCardCount;

    private const int CardBits = 5;
    private const int CardMask = (1 << CardBits) - 1;
    private const int StageShift = 5 * CardBits;

    private static readonly int[][] SuitPermutations =
    [
        [0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]
    ];

    /// <summary>
    /// 局面的代表编码。布局（高位到低位）：已知张数−2（2 位）、两张底牌、三个公共牌位，每张牌 5 位存牌的编号（1～27），
    /// 空位为 0。底牌与公共牌各自升序，所以同一阶段内编码的数值顺序就是逐张比较的字典序。
    /// </summary>
    public static uint Canonicalize(IReadOnlyList<Card> holeCards, IReadOnlyList<Card> communityCards)
    {
        ArgumentNullException.ThrowIfNull(holeCards);
        ArgumentNullException.ThrowIfNull(communityCards);
        if (holeCards.Count != Hand.HoleCardsPerPlayer || communityCards.Count > Hand.CommunityCardCount)
        {
            throw new ArgumentException($"需要 {Hand.HoleCardsPerPlayer} 张底牌和 0～{Hand.CommunityCardCount} 张公共牌。");
        }

        Span<int> hole = stackalloc int[2] { holeCards[0].Id, holeCards[1].Id };
        Span<int> board = stackalloc int[communityCards.Count];
        for (int i = 0; i < board.Length; i++)
        {
            board[i] = communityCards[i].Id;
        }
        RequireDistinct(hole, board);
        return Canonicalize(hole, board);
    }

    /// <summary>按牌的编号（1～27）计算代表编码。调用方保证编号合法且互不相同。</summary>
    internal static uint Canonicalize(ReadOnlySpan<int> hole, ReadOnlySpan<int> board)
    {
        uint best = uint.MaxValue;
        Span<int> h = stackalloc int[2];
        Span<int> b = stackalloc int[board.Length];
        foreach (var permutation in SuitPermutations)
        {
            h[0] = Relabel(hole[0], permutation);
            h[1] = Relabel(hole[1], permutation);
            for (int i = 0; i < board.Length; i++)
            {
                b[i] = Relabel(board[i], permutation);
            }
            h.Sort();
            b.Sort();
            uint key = Pack(h, b);
            if (key < best)
            {
                best = key;
            }
        }
        return best;
    }

    /// <summary>已知牌张数（2～5）。</summary>
    public static int KnownCards(uint key) => (int)(key >> StageShift) + MinKnownCards;

    /// <summary>还原代表局面。花色是代表花色，不是某局里的真实花色。</summary>
    public static (Card[] HoleCards, Card[] CommunityCards) Decode(uint key)
    {
        int boardCount = KnownCards(key) - MinKnownCards;
        var hole = new[] { CardAt(key, 0), CardAt(key, 1) };
        var board = new Card[boardCount];
        for (int i = 0; i < boardCount; i++)
        {
            board[i] = CardAt(key, 2 + i);
        }
        return (hole, board);
    }

    /// <summary>
    /// 某个已知张数下的全部代表编码，升序；Weight 是它代表的原始局面数，用于加权平均。
    /// 已知 5 张时要遍历 80 万种局面，约需零点几秒，不要在热路径上调用。
    /// </summary>
    public static IReadOnlyList<(uint Key, int Weight)> Enumerate(int knownCards)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(knownCards, MinKnownCards);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(knownCards, MaxKnownCards);

        int boardCount = knownCards - MinKnownCards;
        var weights = new Dictionary<uint, int>();
        Span<int> hole = stackalloc int[2];
        Span<int> board = stackalloc int[3];
        for (int h0 = 1; h0 <= Deck.TotalCards; h0++)
        for (int h1 = h0 + 1; h1 <= Deck.TotalCards; h1++)
        {
            hole[0] = h0;
            hole[1] = h1;
            ForEachBoard(boardCount, h0, h1, board, weights, hole);
        }
        return weights.OrderBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value)).ToArray();
    }

    private static void ForEachBoard(int boardCount, int h0, int h1, Span<int> board, Dictionary<uint, int> weights, Span<int> hole)
    {
        // 公共牌按编号升序枚举，每种组合恰好一次。
        void Recurse(int depth, int from, Span<int> current, Span<int> holeCards)
        {
            if (depth == boardCount)
            {
                uint key = Canonicalize(holeCards, current[..boardCount]);
                weights[key] = weights.GetValueOrDefault(key) + 1;
                return;
            }
            for (int card = from; card <= Deck.TotalCards; card++)
            {
                if (card == h0 || card == h1)
                {
                    continue;
                }
                current[depth] = card;
                Recurse(depth + 1, card + 1, current, holeCards);
            }
        }
        Recurse(0, 1, board, hole);
    }

    private static int Relabel(int id, int[] permutation)
    {
        int ordinal = id - 1;
        return permutation[ordinal / Card.RanksPerSuit] * Card.RanksPerSuit + ordinal % Card.RanksPerSuit + 1;
    }

    private static uint Pack(ReadOnlySpan<int> hole, ReadOnlySpan<int> board)
    {
        uint key = (uint)board.Length << StageShift;
        key |= (uint)hole[0] << (4 * CardBits);
        key |= (uint)hole[1] << (3 * CardBits);
        for (int i = 0; i < board.Length; i++)
        {
            key |= (uint)board[i] << ((2 - i) * CardBits);
        }
        return key;
    }

    private static Card CardAt(uint key, int slot) => Card.FromId((int)(key >> ((4 - slot) * CardBits)) & CardMask);

    private static void RequireDistinct(ReadOnlySpan<int> hole, ReadOnlySpan<int> board)
    {
        Span<int> all = stackalloc int[hole.Length + board.Length];
        hole.CopyTo(all);
        board.CopyTo(all[hole.Length..]);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] == 0)
            {
                throw new ArgumentException("含有未初始化的牌。");
            }
            for (int j = i + 1; j < all.Length; j++)
            {
                if (all[i] == all[j])
                {
                    throw new ArgumentException("已知牌中有重复的牌。");
                }
            }
        }
    }
}
