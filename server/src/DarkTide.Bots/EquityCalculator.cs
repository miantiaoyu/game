namespace DarkTide.Bots;

/// <summary>
/// 胜率的离线计算。胜率（equity）＝摊牌时平均能分到的份额：独赢记 1，与 n 人并列最强记 1/(n+1)，输记 0。
///
/// 前提假设：对手的底牌是从未知牌中均匀随机抽的，也就是“对手什么牌都可能有、而且谁都不弃牌”。
/// 这是表的基准值；结合下注动作、换航、弃牌去修正，是人机决策层的事，不在表里。
///
/// 一名对手：精确枚举所有对手底牌与未揭公共牌的组合，没有误差。
/// 两名及以上：蒙特卡洛抽样。一次抽样直接发 7 名对手，前 k 名就是“k 名对手”的一次样本，
/// 所以 2～7 人共用同一批抽样，计算量只取决于抽样次数，不随人数成倍增加。
/// </summary>
internal static class EquityCalculator
{
    public const int MaxOpponents = 7;

    /// <summary>一名对手的精确胜率。</summary>
    public static double HeadsUpExact(ReadOnlySpan<int> hole, ReadOnlySpan<int> board) => HeadsUpExact(hole, board, []);

    /// <summary>
    /// 一名对手的精确胜率，排除公开的死牌（改航换掉的牌）：它们本局不会再出现，既不在对手手里，也不会被发成公共牌。
    ///
    /// 枚举顺序是“先定公共牌，再列对手的两张”：公共牌一定下来，我的牌力只算一次；
    /// 对手每张牌“配两张公共牌”的最强值也先算好，于是每对对手牌只需再查 3 次（两张底牌配一张公共牌）。
    /// 与“先列对手牌、再补公共牌”覆盖的组合完全相同、每种组合权重相同，只是少做了重复的查表。
    /// </summary>
    public static double HeadsUpExact(ReadOnlySpan<int> hole, ReadOnlySpan<int> board, ReadOnlySpan<int> dead)
    {
        Span<int> unknown = stackalloc int[27];
        int unknownCount = Unknown(hole, board, dead, unknown);
        unknown = unknown[..unknownCount];

        Span<int> full = stackalloc int[3];
        board.CopyTo(full);
        Span<bool> used = stackalloc bool[unknownCount];
        Span<int> single = stackalloc int[unknownCount];

        double share = 0;
        long total = 0;
        Complete(hole, unknown, used, single, full, board.Length, 0, ref share, ref total);
        return share / total;
    }

    // 递归补齐公共牌：按下标升序选，每种组合恰好一次。
    private static void Complete(
        ReadOnlySpan<int> hole, ReadOnlySpan<int> unknown, Span<bool> used, Span<int> single, Span<int> full,
        int filled, int from, ref double share, ref long total)
    {
        if (filled == 3)
        {
            ScoreBoard(hole, unknown, used, single, full, ref share, ref total);
            return;
        }
        for (int i = from; i < unknown.Length; i++)
        {
            used[i] = true;
            full[filled] = unknown[i];
            Complete(hole, unknown, used, single, full, filled + 1, i + 1, ref share, ref total);
            used[i] = false;
        }
    }

    private static void ScoreBoard(
        ReadOnlySpan<int> hole, ReadOnlySpan<int> unknown, ReadOnlySpan<bool> used, Span<int> single, ReadOnlySpan<int> full,
        ref double share, ref long total)
    {
        int f0 = full[0], f1 = full[1], f2 = full[2];
        int mine = StrengthTable.Best(hole[0], hole[1], f0, f1, f2);

        // 每张可能的对手牌配两张公共牌的最强值（三种配法）。
        for (int i = 0; i < unknown.Length; i++)
        {
            if (used[i]) continue;
            int c = unknown[i];
            single[i] = Math.Max(StrengthTable.Three(c, f0, f1), Math.Max(StrengthTable.Three(c, f0, f2), StrengthTable.Three(c, f1, f2)));
        }

        for (int i = 0; i < unknown.Length; i++)
        {
            if (used[i]) continue;
            int a = unknown[i];
            int singleA = single[i];
            for (int j = i + 1; j < unknown.Length; j++)
            {
                if (used[j]) continue;
                int b = unknown[j];
                int theirs = Math.Max(Math.Max(singleA, single[j]),
                    Math.Max(StrengthTable.Three(a, b, f0), Math.Max(StrengthTable.Three(a, b, f1), StrengthTable.Three(a, b, f2))));
                share += mine > theirs ? 1 : mine == theirs ? 0.5 : 0;
                total++;
            }
        }
    }

    /// <summary>
    /// 1～7 名对手的抽样胜率，下标 k-1 对应 k 名对手。种子相同结果就相同，与线程调度无关。
    /// </summary>
    public static double[] MultiwaySampled(ReadOnlySpan<int> hole, ReadOnlySpan<int> board, int samples, ulong seed)
    {
        Span<int> unknown = stackalloc int[27];
        int unknownCount = Unknown(hole, board, [], unknown);
        int missing = 3 - board.Length;
        int needed = 2 * MaxOpponents + missing;
        if (unknownCount < needed)
        {
            throw new InvalidOperationException("未知牌不够发 7 名对手。");
        }

        var random = new SplitMix64(seed);
        Span<int> pool = stackalloc int[unknownCount];
        Span<int> full = stackalloc int[3];
        Span<double> shares = stackalloc double[MaxOpponents];
        shares.Clear();

        for (int s = 0; s < samples; s++)
        {
            // 部分洗牌：只把前 needed 个位置洗成随机，后面不动。每次从原始顺序开始，样本互相独立。
            unknown[..unknownCount].CopyTo(pool);
            for (int i = 0; i < needed; i++)
            {
                int j = i + random.NextInt(unknownCount - i);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }

            board.CopyTo(full);
            for (int i = 0; i < missing; i++)
            {
                full[board.Length + i] = pool[2 * MaxOpponents + i];
            }

            int mine = StrengthTable.Best(hole[0], hole[1], full[0], full[1], full[2]);
            int strongest = -1;
            int tiedAtStrongest = 0;
            for (int k = 0; k < MaxOpponents; k++)
            {
                int theirs = StrengthTable.Best(pool[2 * k], pool[2 * k + 1], full[0], full[1], full[2]);
                if (theirs > strongest)
                {
                    strongest = theirs;
                    tiedAtStrongest = 1;
                }
                else if (theirs == strongest)
                {
                    tiedAtStrongest++;
                }
                // 前 k+1 名对手里最强的那手决定这次样本的份额。
                shares[k] += mine > strongest ? 1 : mine == strongest ? 1.0 / (tiedAtStrongest + 1) : 0;
            }
        }

        var result = new double[MaxOpponents];
        for (int k = 0; k < MaxOpponents; k++)
        {
            result[k] = shares[k] / samples;
        }
        return result;
    }

    private static int Unknown(ReadOnlySpan<int> hole, ReadOnlySpan<int> board, ReadOnlySpan<int> dead, Span<int> output)
    {
        int count = 0;
        for (int id = 1; id <= 27; id++)
        {
            if (hole.Contains(id) || board.Contains(id) || dead.Contains(id))
            {
                continue;
            }
            output[count++] = id;
        }
        return count;
    }
}
