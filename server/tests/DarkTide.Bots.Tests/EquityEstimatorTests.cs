using DarkTide.Rules;

namespace DarkTide.Bots.Tests;

public class EquityEstimatorTests
{
    private static Card C(Suit suit, int rank) => new(suit, rank);
    private const Suit R = Suit.Red, B = Suit.Blue, G = Suit.Green;
    private static readonly EquityTable Table = EquityTable.Standard;

    [Fact]
    public void DeadCardsAreExcludedExactlyForOneOpponent()
    {
        // 期望值由 simulation/three_suit_analysis.py 独立枚举（死牌从未知牌中排除）。
        Card[] hole = [C(R, 8), C(R, 7)];
        Assert.Equal(0.521539961014, EquityEstimator.Estimate(Table, hole, [C(G, 2)], [C(B, 1), C(R, 9), C(R, 6)], 1), 10);
        Assert.Equal(0.346240601504, EquityEstimator.Estimate(Table, hole, [C(G, 2), C(B, 1)], [C(R, 9), C(R, 6)], 1), 10);
        Assert.Equal(0.014285714286, EquityEstimator.Estimate(Table, hole, [C(G, 2), C(B, 1), C(B, 5)], [C(R, 9)], 1), 10);
    }

    [Fact]
    public void WithoutDeadCardsItIsATableLookup()
    {
        Card[] hole = [C(R, 8), C(R, 7)];
        Card[] board = [C(G, 2), C(B, 1)];
        for (int k = 1; k <= EquityTable.MaxOpponents; k++)
        {
            Assert.Equal(Table.Lookup(hole, board, k), EquityEstimator.Estimate(Table, hole, board, [], k));
        }
        Assert.Equal(1.0, EquityEstimator.Estimate(Table, hole, board, [C(R, 9)], 0));
    }

    [Fact]
    public void RerouteKeepAndSwapMatchTheUsersDefinition()
    {
        // 保留：第三张从剩余未知牌出。换：第二张变死牌，第二、三张都从剩余未知牌出。
        // 直接逐张列举对照：换后的胜率＝新第二张每种可能下胜率的平均。
        Card[] hole = [C(R, 8), C(R, 7)];
        Card[] board = [C(G, 2), C(B, 1)];
        Card[] dead = [C(R, 9)];

        var (keep, swap) = EquityEstimator.EvaluateReroute(Table, hole, board, dead, 1);

        var unseen = Deck.CreateStandard().Except(hole).Except(board).Except(dead).ToArray();
        double expectedSwap = unseen.Average(next => EquityEstimator.Estimate(Table, hole, [board[0], next], [.. dead, board[1]], 1));
        Assert.Equal(EquityEstimator.Estimate(Table, hole, board, dead, 1), keep, 12);
        Assert.Equal(expectedSwap, swap, 10);
        // 第二张蓝 1 对红 8 红 7 没用，换了应当更好。
        Assert.True(swap > keep);
    }

    [Fact]
    public void GoodSecondCardIsWorthMoreKeptThanSwapped()
    {
        // 红 9 让红 8 红 7 凑成同花顺，换掉就亏了。
        var (keep, swap) = EquityEstimator.EvaluateReroute(Table, [C(R, 8), C(R, 7)], [C(G, 2), C(R, 9)], [], 3);
        Assert.True(keep > 0.9);
        Assert.True(swap < 0.45);
    }
}
