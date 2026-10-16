using DarkTide.Rules;

namespace DarkTide.Bots.Tests;

/// <summary>随程序发布的胜率表：能加载、能复现、各阶段之间自洽。</summary>
public class EquityTableTests
{
    private static readonly EquityTable Table = EquityTable.Standard;
    private const double QuantizationStep = 1.0 / ushort.MaxValue;

    [Fact]
    public void StandardTableCoversEveryStage()
    {
        Assert.Equal(EquityTableBuilder.DefaultSeed, Table.Seed);
        Assert.Equal(81, Table.RowCount(2));
        Assert.Equal(1_629, Table.RowCount(3));
        Assert.Equal(18_288, Table.RowCount(4));
        Assert.Equal(137_016, Table.RowCount(5));
        foreach (var (known, samples) in EquityTableBuilder.DefaultSamples)
        {
            Assert.Equal(samples, Table.SamplesPerRow(known));
        }
    }

    [Fact]
    public void SpotRowsRecomputeExactly()
    {
        // 每行种子只由总种子和行编码决定：任挑几行重算，必须与表里逐位相同。
        // 已知 2 张的行抽样 100 万次、单行要几十毫秒，这里只挑 3、4、5 张的行。
        var random = new Random(42);
        foreach (int known in new[] { 3, 4, 5 })
        {
            var keys = HandClass.Enumerate(known);
            foreach (var (key, _) in keys.OrderBy(_ => random.Next()).Take(5))
            {
                var expected = EquityTableBuilder.ComputeRow(key, Table.SamplesPerRow(known), Table.Seed);
                Assert.Equal(expected, Table.RawRow(key).ToArray());
            }
        }
    }

    [Fact]
    public void HeadsUpColumnIsExactAndStagesAgree()
    {
        // 一名对手的列是精确值，所以“已知 n 张的胜率 = 下一张公共牌所有可能下的平均胜率”必须严格成立
        // （只差量化误差）。这同时检查了花色归一、查表和精确枚举三件事。
        var deck = Deck.CreateStandard();
        var random = new Random(7);
        for (int round = 0; round < 60; round++)
        {
            int boardCount = random.Next(0, 3);
            var known = deck.OrderBy(_ => random.Next()).Take(2 + boardCount).ToArray();
            var hole = known[..2];
            var board = known[2..];

            double parent = Table.Lookup(hole, board, 1);
            var nextCards = deck.Except(known).ToArray();
            double average = nextCards.Average(next => Table.Lookup(hole, board.Append(next).ToArray(), 1));

            Assert.InRange(average, parent - 0.0001, parent + 0.0001);
        }
    }

    [Fact]
    public void MultiwayColumnsAgreeAcrossStagesWithinSamplingError()
    {
        // 多人列是抽样值，阶段之间只能在抽样误差内一致。已知 2 张抽样 100 万次，误差约 0.0005；
        // 下一阶段 25 种公共牌的平均值，误差约 0.0035/√25 ≈ 0.0007 量级。取 0.006 的容差。
        var deck = Deck.CreateStandard();
        var random = new Random(9);
        for (int round = 0; round < 20; round++)
        {
            var hole = deck.OrderBy(_ => random.Next()).Take(2).ToArray();
            var nextCards = deck.Except(hole).ToArray();
            foreach (int opponents in new[] { 2, 4, 7 })
            {
                double parent = Table.Lookup(hole, [], opponents);
                double average = nextCards.Average(next => Table.Lookup(hole, [next], opponents));
                Assert.InRange(average, parent - 0.006, parent + 0.006);
            }
        }
    }

    [Fact]
    public void LookupMatchesADirectCalculationForTheRealCards()
    {
        // 查表用的是代表局面；真实花色换了，胜率不能变。
        var deck = Deck.CreateStandard();
        var random = new Random(11);
        for (int round = 0; round < 200; round++)
        {
            var known = deck.OrderBy(_ => random.Next()).Take(2 + random.Next(2, 4)).ToArray();
            int[] hole = [known[0].Id, known[1].Id];
            int[] board = known[2..].Select(card => (int)card.Id).ToArray();

            // 表里存的是 16 位量化值，误差最多半个量化步长（约 0.0000076）。
            double exact = EquityCalculator.HeadsUpExact(hole, board);
            Assert.InRange(Table.Lookup(known[..2], known[2..], 1), exact - QuantizationStep, exact + QuantizationStep);
        }
    }

    [Fact]
    public void EquityFallsAsOpponentsAreAddedAndZeroOpponentsMeansCertainty()
    {
        foreach (var (key, _) in HandClass.Enumerate(2))
        {
            for (int k = 2; k <= EquityTable.MaxOpponents; k++)
            {
                Assert.True(Table.LookupByKey(key, k) <= Table.LookupByKey(key, k - 1) + 0.002);
            }
        }
        var hole = new[] { new Card(Suit.Red, 1), new Card(Suit.Blue, 2) };
        Assert.Equal(1.0, Table.Lookup(hole, [], 0));
    }

    [Fact]
    public void FileFromAnotherRuleSetIsRejected()
    {
        using var stream = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        using (var writer = new BinaryWriter(gzip))
        {
            writer.Write(EquityTableBuilder.Magic);
            writer.Write(EquityTableBuilder.FormatVersion);
            writer.Write(new byte[32]);
        }
        stream.Position = 0;

        var error = Assert.Throws<InvalidDataException>(() => EquityTable.Load(stream));
        Assert.Contains("牌型规则", error.Message);
    }
}
