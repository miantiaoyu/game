using System.IO.Compression;
using DarkTide.Rules;

namespace DarkTide.Bots;

/// <summary>
/// 预先算好的胜率表：已知 2～5 张牌（两张底牌 + 0～3 张公共牌）× 1～7 名对手。
///
/// 胜率的含义见 <see cref="EquityCalculator"/>：假设对手底牌随机、无人弃牌，摊牌时平均能分到的份额。
/// 它是人机判断的起点，不是结论：对手动作、换航弃置的牌、局势都要在决策层另行修正。
///
/// 全进程共享一份只读数据（约 3 MB），首次使用时从嵌入资源加载。查一次：花色归一（6 种重排）+ 二分查找，微秒级。
/// </summary>
public sealed class EquityTable
{
    public const int MaxOpponents = EquityCalculator.MaxOpponents;
    private const string ResourceName = "DarkTide.Bots.Data.equity-tables.bin.gz";

    private static readonly Lazy<EquityTable> LazyStandard = new(() =>
    {
        using var stream = typeof(EquityTable).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"缺少嵌入的胜率表 {ResourceName}，请运行 server/tools/DarkTide.EquityTables 生成。");
        return Load(stream);
    });

    private readonly Stage[] _stages;

    private sealed record Stage(int KnownCards, int Samples, uint[] Keys, ushort[] Values);

    private EquityTable(ulong seed, Stage[] stages)
    {
        Seed = seed;
        _stages = stages;
    }

    /// <summary>随程序发布的标准胜率表。</summary>
    public static EquityTable Standard => LazyStandard.Value;

    public ulong Seed { get; }

    /// <summary>某阶段的行数（等价类数）。</summary>
    public int RowCount(int knownCards) => GetStage(knownCards).Keys.Length;

    /// <summary>某阶段每行的抽样次数（一名对手的列是精确值，不受影响）。</summary>
    public int SamplesPerRow(int knownCards) => GetStage(knownCards).Samples;

    /// <summary>
    /// 查胜率。<paramref name="opponents"/> 是仍未弃牌的对手数；为 0 时返回 1（别人都弃了）。
    /// </summary>
    public double Lookup(IReadOnlyList<Card> holeCards, IReadOnlyList<Card> communityCards, int opponents)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(opponents);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(opponents, MaxOpponents);
        uint key = HandClass.Canonicalize(holeCards, communityCards);
        return opponents == 0 ? 1.0 : LookupByKey(key, opponents);
    }

    /// <summary>按等价类编码查胜率，供测试与导出使用。</summary>
    public double LookupByKey(uint key, int opponents)
    {
        var stage = GetStage(HandClass.KnownCards(key));
        int row = Array.BinarySearch(stage.Keys, key);
        if (row < 0)
        {
            throw new KeyNotFoundException("该编码不是代表局面；请先经过 HandClass.Canonicalize。");
        }
        return stage.Values[row * MaxOpponents + opponents - 1] / (double)ushort.MaxValue;
    }

    internal ReadOnlySpan<ushort> RawRow(uint key)
    {
        var stage = GetStage(HandClass.KnownCards(key));
        int row = Array.BinarySearch(stage.Keys, key);
        return stage.Values.AsSpan(row * MaxOpponents, MaxOpponents);
    }

    public static EquityTable Load(Stream input)
    {
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip);
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual(EquityTableBuilder.Magic))
        {
            throw new InvalidDataException("不是胜率表文件。");
        }
        int version = reader.ReadInt32();
        if (version != EquityTableBuilder.FormatVersion)
        {
            throw new InvalidDataException($"不支持的胜率表格式版本 {version}。");
        }
        if (!reader.ReadBytes(32).AsSpan().SequenceEqual(EquityTableBuilder.StrengthFingerprint()))
        {
            throw new InvalidDataException("胜率表是按另一套牌型规则生成的，请重新生成。");
        }
        ulong seed = reader.ReadUInt64();
        int stageCount = reader.ReadInt32();

        var stages = new Stage[stageCount];
        for (int s = 0; s < stageCount; s++)
        {
            int known = reader.ReadInt32();
            int rows = reader.ReadInt32();
            int samples = reader.ReadInt32();
            var keys = new uint[rows];
            var values = new ushort[rows * MaxOpponents];
            for (int row = 0; row < rows; row++)
            {
                keys[row] = reader.ReadUInt32();
                for (int k = 0; k < MaxOpponents; k++)
                {
                    values[row * MaxOpponents + k] = reader.ReadUInt16();
                }
            }
            stages[s] = new Stage(known, samples, keys, values);
        }
        return new EquityTable(seed, stages);
    }

    private Stage GetStage(int knownCards) =>
        _stages.FirstOrDefault(stage => stage.KnownCards == knownCards)
        ?? throw new ArgumentOutOfRangeException(nameof(knownCards), knownCards, "已知牌张数必须是 2～5。");
}
