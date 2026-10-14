using DarkTide.Rules;

namespace DarkTide.Rules.Tests;

public class PotLayeringTests
{
    private static IReadOnlyList<Pot> Split(int[] Commit, params int[] foldedSeats)
    {
        var folded = new bool[Commit.Length];
        foreach (int seat in foldedSeats)
        {
            folded[seat] = true;
        }
        return PotLayering.SplitRegularPots(Commit, folded);
    }

    [Fact]
    public void EqualContributionsProduceSinglePot()
    {
        var pot = Split(new[] { 30, 30, 30 });

        var only = Assert.Single(pot);
        Assert.Equal(90, only.Total.Amount);
        Assert.Equal(new[] { 0, 1, 2 }, only.EligibleSeats);
    }

    [Fact]
    public void AllInCreatesMainPotAndSidePot()
    {
        // 规则第 6 节例：A 全下 10，B、C 各投入 30 → 主池 30（三人）、边池 40（仅 B、C）。
        var pot = Split(new[] { 10, 30, 30 });

        Assert.Equal(2, pot.Count);
        Assert.Equal(30, pot[0].Total.Amount);
        Assert.Equal(new[] { 0, 1, 2 }, pot[0].EligibleSeats);
        Assert.Equal(40, pot[1].Total.Amount);
        Assert.Equal(new[] { 1, 2 }, pot[1].EligibleSeats);
        Assert.Equal(70, PotLayering.TotalAmount(pot));
    }

    [Fact]
    public void ShortAnteAlsoCreatesSidePot()
    {
        // 规则例：底注 2，三人实际只能交 1/2/2 → 主池 3（三人）、边池 2（仅足额两人）。
        var pot = Split(new[] { 1, 2, 2 });

        Assert.Equal(2, pot.Count);
        Assert.Equal(3, pot[0].Total.Amount);
        Assert.Equal(new[] { 0, 1, 2 }, pot[0].EligibleSeats);
        Assert.Equal(2, pot[1].Total.Amount);
        Assert.Equal(new[] { 1, 2 }, pot[1].EligibleSeats);
    }

    [Fact]
    public void MultipleAllInLevelsLayerPotsAndConserveTotal()
    {
        var Commit = new[] { 5, 10, 20, 30 };
        var pot = Split(Commit);

        Assert.Equal(65, PotLayering.TotalAmount(pot));
        Assert.Equal(Commit.Sum(), PotLayering.TotalAmount(pot));
        Assert.Equal(new[] { 20, 15, 20, 10 }, pot.Select(current => current.Total.Amount));
        Assert.Equal(new[] { 4, 3, 2, 1 }, pot.Select(current => current.EligibleSeats.Count));
    }

    [Fact]
    public void FoldedMoneyStaysInPotButLosesEligibility()
    {
        // 全下 10 的座位 1 是出了钱的普通出资人；两人各投 30，其中 1 号出资 30 后退到弃牌。
        var pot = Split(new[] { 10, 30, 30 }, foldedSeats: 1);

        // 两层资格不同（{0,2} 与 {2}），不能合并：
        // 主池 30 由 0、2 争夺；边池 40 只有 2 有资格，直接归他。
        Assert.Equal(2, pot.Count);
        Assert.Equal(30, pot[0].Total.Amount);
        Assert.Equal(new[] { 0, 2 }, pot[0].EligibleSeats);
        Assert.Equal(40, pot[1].Total.Amount);
        Assert.Equal(new[] { 2 }, pot[1].EligibleSeats);
        // 弃牌者出的 30 仍然在池里。
        Assert.Equal(70, PotLayering.TotalAmount(pot));
    }

    [Fact]
    public void AdjacentLayersWithSameEligibilityMerge()
    {
        // 0 投 10，1、2 各投 20。第一层三人都有资格，第二层只有 1、2 —— 资格不同，不合并。
        // 若把 0 也算进第二层就会错误合并，这里正是检查资格是按“出到本层的人”算的。
        var pot = Split(new[] { 10, 20, 20 });

        Assert.Equal(2, pot.Count);
        Assert.Equal(30, pot[0].Total.Amount);
        Assert.Equal(new[] { 0, 1, 2 }, pot[0].EligibleSeats);
        Assert.Equal(20, pot[1].Total.Amount);
        Assert.Equal(new[] { 1, 2 }, pot[1].EligibleSeats);
    }

    [Fact]
    public void FoldingMakesAdjacentLayersMerge()
    {
        // 0 投 10，1 投 20，2 投 20：第一层资格 {0,1,2}，第二层 {1,2} —— 不同，不合并。
        // 但若 0 弃牌，第一层资格变成 {1,2}，与第二层相同，则合并成一池 50。
        var pot = Split(new[] { 10, 20, 20 }, foldedSeats: 0);

        var only = Assert.Single(pot);
        Assert.Equal(50, only.Total.Amount);
        Assert.Equal(new[] { 1, 2 }, only.EligibleSeats);
    }

    [Fact]
    public void SingleContributorIsTheOnlyEligibleSeat()
    {
        var pot = Split(new[] { 7, 0, 0 });

        var only = Assert.Single(pot);
        Assert.Equal(7, only.Total.Amount);
        Assert.Equal(new[] { 0 }, only.EligibleSeats);
    }

    [Fact]
    public void NoContributionsProduceNoPots()
    {
        Assert.Empty(Split(new[] { 0, 0 }));
    }

    [Fact]
    public void MismatchedLengthsAndNegativeContributionsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => PotLayering.SplitRegularPots(new[] { 1, 2 }, new bool[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PotLayering.SplitRegularPots(new[] { -1, 2 }, new bool[2]));
        Assert.Throws<ArgumentNullException>(() => PotLayering.SplitRegularPots(null!, new bool[2]));
    }

    [Fact]
    public void RandomCasesMatchIndependentPerChipAlgorithm()
    {
        // 独立参考实现：把筹码按“每一枚”的高度逐层枚举，而不是按额度跳跃。
        // 两者结果必须完全一致，等于用另一种写法交叉验证分层逻辑。
        var random = new Random(20261014);
        for (int round = 0; round < 2000; round++)
        {
            int seatCount = random.Next(2, 7);
            var Commit = new int[seatCount];
            var folded = new bool[seatCount];
            for (int seat = 0; seat < seatCount; seat++)
            {
                Commit[seat] = random.Next(0, 6) * random.Next(0, 3);   // 偶发 0，制造“全下到 0”这类边界
                folded[seat] = random.Next(3) == 0;
            }

            if (Commit.All(amount => amount == 0))
            {
                continue;
            }

            var actual = PotLayering.SplitRegularPots(Commit, folded);
            var expected = EnumerateByChip(Commit, folded);

            // 先把每层压成可比较的文本：ValueTuple 里的 List<int> 是按引用比较的，直接比序列永远不等。
            var expectedText = expected.Select(item => $"{item.amount}:{string.Join('|', item.eligible)}").ToList();
            var actualText = actual.Select(pot => $"{pot.Total.Amount}:{string.Join('|', pot.EligibleSeats)}").ToList();

            Assert.True(expectedText.SequenceEqual(actualText),
                $"投入=[{string.Join(',', Commit)}] 弃牌=[{string.Join(',', folded.Select(value => value ? 1 : 0))}]\n" +
                $"预期={string.Join(" ", expectedText)}\n实际={string.Join(" ", actualText)}");
        }
    }

    [Fact]
    public void TotalIsConservedAndEligibilityIsValid()
    {
        var random = new Random(20261015);
        for (int round = 0; round < 2000; round++)
        {
            int seatCount = random.Next(2, 7);
            var Commit = new int[seatCount];
            var folded = new bool[seatCount];
            for (int seat = 0; seat < seatCount; seat++)
            {
                Commit[seat] = random.Next(0, 8);
                folded[seat] = random.Next(3) == 0;
            }

            var pot = PotLayering.SplitRegularPots(Commit, folded);
            string context = $"投入=[{string.Join(',', Commit)}] 弃牌=[{string.Join(',', folded.Select(value => value ? 1 : 0))}]";

            // 1. 金额守恒：所有层加起来等于全部投入，一枚不多一枚不少。
            Assert.True(Commit.Sum() == PotLayering.TotalAmount(pot), context);
            // 2. 不存在金额为零的空层。
            Assert.All(pot, current => Assert.True(current.Total.Amount > 0, context));
            // 3. 资格名单里的人必须出过钱、且没弃牌。
            Assert.All(pot, current => Assert.All(current.EligibleSeats, seat =>
            {
                Assert.True(Commit[seat] > 0, context);
                Assert.False(folded[seat], context);
            }));
            // 4. 资格名单随层高单调缩小：后一层是前一层的子集。
            for (int index = 1; index < pot.Count; index++)
            {
                Assert.True(pot[index].EligibleSeats.All(seat => pot[index - 1].EligibleSeats.Contains(seat)), context);
            }
            // 5. 相邻两层的资格必须不同，否则说明该合并的没合并。
            for (int index = 1; index < pot.Count; index++)
            {
                Assert.False(pot[index].EligibleSeats.SequenceEqual(pot[index - 1].EligibleSeats), context);
            }
        }
    }

    /// <summary>
    /// 独立参考实现：把筹码按“每一枚的高度”逐层枚举，而不是按额度跳跃。
    /// 高度 h 上，凡是投入达到 h 的人都各出 1 枚，所以该高度贡献的人数枚筹码。
    /// </summary>
    private static List<(int amount, List<int> eligible)> EnumerateByChip(int[] Commit, bool[] folded)
    {
        var result = new List<(int amount, List<int> eligible)>();
        int maxContribution = Commit.Max();
        for (int height = 1; height <= maxContribution; height++)
        {
            var contributors = Enumerable.Range(0, Commit.Length).Where(seat => Commit[seat] >= height).ToList();
            var eligible = contributors.Where(seat => !folded[seat]).ToList();
            if (result.Count > 0 && result[^1].eligible.SequenceEqual(eligible))
            {
                // 资格不变就把这一层并进上一层；并进去的是“人数”枚，不是 1 枚。
                result[^1] = (result[^1].amount + contributors.Count, eligible);
            }
            else
            {
                result.Add((contributors.Count, eligible));
            }
        }
        return result;
    }

    [Fact]
    public void EligibleSeatsAreReadOnly()
    {
        var pot = Split(new[] { 5, 5 });

        Assert.False(pot[0].EligibleSeats is int[]);
    }
}
