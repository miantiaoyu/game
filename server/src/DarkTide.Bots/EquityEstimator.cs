using DarkTide.Rules;

namespace DarkTide.Bots;

/// <summary>
/// 人机做决定时用的胜率：只看自己的牌、公共牌和公开弃置的牌，假设对手的牌随机、不考虑任何行为。
///
/// 没有死牌时直接查表。有死牌（有人改航过）时，表里的数会偏：表把被换掉的牌仍当作可能出现的牌。
/// 这时一名对手的胜率重新精确枚举（已知 4 张约 0.1 毫秒），多名对手再乘上表里“k 名对手 ÷ 1 名对手”的比例。
/// 比例来自没有死牌的表，所以多人时是近似：死牌只有几张，比例本身变化很小；单挑部分是精确的。
/// </summary>
public static class EquityEstimator
{
    public static double Estimate(EquityTable table, IReadOnlyList<Card> hole, IReadOnlyList<Card> board, IReadOnlyList<Card> dead, int opponents)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(dead);
        if (opponents == 0)
        {
            return 1.0;
        }
        if (dead.Count == 0)
        {
            return table.Lookup(hole, board, opponents);
        }

        double headsUp = EquityCalculator.HeadsUpExact(Ids(hole), Ids(board), Ids(dead));
        if (opponents == 1)
        {
            return headsUp;
        }
        double tableHeadsUp = table.Lookup(hole, board, 1);
        // 单挑都赢不了任何一手，多人更赢不了。
        return tableHeadsUp <= 0 ? 0 : Math.Min(1.0, headsUp * table.Lookup(hole, board, opponents) / tableHeadsUp);
    }

    /// <summary>
    /// 改航的两种结果（规则 5.1，只换第二张）：
    /// 保留＝第三张从剩余未知牌里出；换＝当前第二张变成死牌，新的第二张和第三张都从剩余未知牌里出。
    /// 两者都是“对所有可能取平均”，所以直接用少一张公共牌、多一张死牌的局面算即可，不需要逐张列举。
    /// </summary>
    public static (double Keep, double Swap) EvaluateReroute(
        EquityTable table, IReadOnlyList<Card> hole, IReadOnlyList<Card> board, IReadOnlyList<Card> dead, int opponents)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (board.Count != 2)
        {
            throw new ArgumentException("改航时公共牌恰好是两张。", nameof(board));
        }
        double keep = Estimate(table, hole, board, dead, opponents);
        double swap = Estimate(table, hole, [board[0]], [.. dead, board[1]], opponents);
        return (keep, swap);
    }

    private static int[] Ids(IReadOnlyList<Card> cards) => cards.Select(card => (int)card.Id).ToArray();
}
