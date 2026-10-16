using DarkTide.Rules;

namespace DarkTide.Bots.Tests;

public class EquityCalculatorTests
{
    private static int Id(Suit suit, int rank) => new Card(suit, rank).Id;
    private const Suit R = Suit.Red, B = Suit.Blue, G = Suit.Green;

    public static TheoryData<string, int[], int[], double> PythonReference => new()
    {
        // 期望值由 simulation/three_suit_analysis.py 的 best_score 独立枚举得出（Python 与 C# 两套实现）。
        { "红9蓝9", [Id(R, 9), Id(B, 9)], [], 0.535953322040 },
        { "红1蓝2", [Id(R, 1), Id(B, 2)], [], 0.338851872765 },
        { "红8红9 + 绿9", [Id(R, 8), Id(R, 9)], [Id(G, 9)], 0.609644582471 },
        { "红8红9 + 绿9 红1", [Id(R, 8), Id(R, 9)], [Id(G, 9), Id(R, 1)], 0.757763975155 },
        { "红8红9 + 绿9 红1 蓝5", [Id(R, 8), Id(R, 9)], [Id(G, 9), Id(R, 1), Id(B, 5)], 0.781385281385 },
    };

    [Theory]
    [MemberData(nameof(PythonReference))]
    public void HeadsUpExactMatchesThePythonReference(string name, int[] hole, int[] board, double expected)
    {
        Assert.Equal(expected, EquityCalculator.HeadsUpExact(hole, board), 10);
        _ = name;
    }

    [Fact]
    public void StrengthLookupAgreesWithTheRulesEvaluator()
    {
        var deck = Deck.CreateStandard();
        var random = new Random(3);
        for (int round = 0; round < 20000; round++)
        {
            var five = deck.OrderBy(_ => random.Next()).Take(5).ToArray();
            var other = deck.Except(five).OrderBy(_ => random.Next()).Take(2).ToArray();
            var board = five[2..];
            // 名次的大小关系必须与规则层的比较完全一致，包括并列。
            int mine = StrengthTable.Best(five[0].Id, five[1].Id, board[0].Id, board[1].Id, board[2].Id);
            int theirs = StrengthTable.Best(other[0].Id, other[1].Id, board[0].Id, board[1].Id, board[2].Id);
            var expected = HandEvaluator.EvaluateBest(five[..2], board).CompareTo(HandEvaluator.EvaluateBest(other, board));
            Assert.Equal(Math.Sign(expected), Math.Sign(mine.CompareTo(theirs)));
        }
    }

    [Fact]
    public void SampledHeadsUpConvergesToTheExactValue()
    {
        int[] hole = [Id(R, 8), Id(R, 9)];
        int[] board = [Id(G, 9)];
        double exact = EquityCalculator.HeadsUpExact(hole, board);

        double sampled = EquityCalculator.MultiwaySampled(hole, board, 200_000, seed: 11)[0];

        // 20 万次抽样的标准误约 0.001，取 4 倍容差。
        Assert.InRange(sampled, exact - 0.004, exact + 0.004);
    }

    [Fact]
    public void SameSeedGivesTheSameNumbersAndMoreOpponentsNeverHelp()
    {
        int[] hole = [Id(R, 9), Id(B, 9)];
        var first = EquityCalculator.MultiwaySampled(hole, [], 20_000, seed: 5);
        var second = EquityCalculator.MultiwaySampled(hole, [], 20_000, seed: 5);

        Assert.Equal(first, second);
        // 同一批样本里多加一名对手，份额只会持平或下降。
        for (int k = 1; k < first.Length; k++)
        {
            Assert.True(first[k] <= first[k - 1] + 1e-12);
        }
    }
}
