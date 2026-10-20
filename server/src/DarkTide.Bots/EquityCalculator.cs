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
    /// </summary>
    public static double HeadsUpExact(ReadOnlySpan<int> hole, ReadOnlySpan<int> board, ReadOnlySpan<int> dead)
    {
        Span<int> unknown = stackalloc int[27];
        int unknownCount = Unknown(hole, board, dead, unknown);
        unknown = unknown[..unknownCount];
        int missing = 3 - board.Length;

        Span<int> full = stackalloc int[3];
        board.CopyTo(full);

        double share = 0;
        long total = 0;
        for (int i = 0; i < unknown.Length; i++)
        for (int j = i + 1; j < unknown.Length; j++)
        {
            int o0 = unknown[i], o1 = unknown[j];
            // 未揭的公共牌从剩下的牌里选，按编号升序枚举，每种组合恰好一次。
            ForEachCompletion(unknown, i, j, missing, board.Length, full, ref share, ref total, hole, o0, o1);
        }
        return share / total;
    }

    private static void ForEachCompletion(
        ReadOnlySpan<int> unknown, int skipA, int skipB, int missing, int known, Span<int> full,
        ref double share, ref long total, ReadOnlySpan<int> hole, int o0, int o1)
    {
        switch (missing)
        {
            case 0:
                Score(hole, o0, o1, full, ref share, ref total);
                return;
            case 1:
                for (int x = 0; x < unknown.Length; x++)
                {
                    if (x == skipA || x == skipB) continue;
                    full[known] = unknown[x];
                    Score(hole, o0, o1, full, ref share, ref total);
                }
                return;
            case 2:
                for (int x = 0; x < unknown.Length; x++)
                {
                    if (x == skipA || x == skipB) continue;
                    full[known] = unknown[x];
                    for (int y = x + 1; y < unknown.Length; y++)
                    {
                        if (y == skipA || y == skipB) continue;
                        full[known + 1] = unknown[y];
                        Score(hole, o0, o1, full, ref share, ref total);
                    }
                }
                return;
            default:
                for (int x = 0; x < unknown.Length; x++)
                {
                    if (x == skipA || x == skipB) continue;
                    full[0] = unknown[x];
                    for (int y = x + 1; y < unknown.Length; y++)
                    {
                        if (y == skipA || y == skipB) continue;
                        full[1] = unknown[y];
                        for (int z = y + 1; z < unknown.Length; z++)
                        {
                            if (z == skipA || z == skipB) continue;
                            full[2] = unknown[z];
                            Score(hole, o0, o1, full, ref share, ref total);
                        }
                    }
                }
                return;
        }
    }

    private static void Score(ReadOnlySpan<int> hole, int o0, int o1, ReadOnlySpan<int> full, ref double share, ref long total)
    {
        int mine = StrengthTable.Best(hole[0], hole[1], full[0], full[1], full[2]);
        int theirs = StrengthTable.Best(o0, o1, full[0], full[1], full[2]);
        share += mine > theirs ? 1 : mine == theirs ? 0.5 : 0;
        total++;
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
