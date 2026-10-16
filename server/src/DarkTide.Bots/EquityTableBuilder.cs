using System.IO.Compression;
using System.Security.Cryptography;

namespace DarkTide.Bots;

/// <summary>
/// 离线生成胜率表：已知 2～5 张牌的每个等价类 × 1～7 名对手。只在开发时运行（server/tools/DarkTide.EquityTables），
/// 结果作为嵌入资源随程序发布，运行时只查表不计算。
///
/// 一名对手的列精确枚举；两名以上抽样。每行的随机种子由“总种子 + 该行编码”算出，
/// 所以结果与线程数、计算顺序无关：同一份代码、同一个总种子，永远生成逐字节相同的表，测试可以抽查重算。
///
/// 抽样次数按阶段分配：行数少的阶段多抽，标准误大致是 0.5/√次数。
/// 已知 2 张：100 万次（约 ±0.0005）；3 张：20 万（±0.0011）；4 张：5 万（±0.0022）；5 张：2 万（±0.0035）。
/// 再多抽收益很小：人机拿到胜率后还要按对手动作、换航修正，那些修正的不确定性远大于这点抽样误差。
/// </summary>
public static class EquityTableBuilder
{
    public const ulong DefaultSeed = 20261016;
    public const int FormatVersion = 1;
    internal static ReadOnlySpan<byte> Magic => "DTEQ"u8;

    public static IReadOnlyDictionary<int, int> DefaultSamples { get; } = new Dictionary<int, int>
    {
        [2] = 1_000_000,
        [3] = 200_000,
        [4] = 50_000,
        [5] = 20_000,
    };

    /// <summary>一行：1～7 名对手的胜率，量化为 0～65535（÷65535 即胜率；量化误差约 0.00001，远小于抽样误差）。</summary>
    public static ushort[] ComputeRow(uint key, int samples, ulong seed)
    {
        var (holeCards, boardCards) = HandClass.Decode(key);
        int[] hole = [holeCards[0].Id, holeCards[1].Id];
        int[] board = boardCards.Select(card => (int)card.Id).ToArray();

        var sampled = EquityCalculator.MultiwaySampled(hole, board, samples, RowSeed(seed, key));
        // 一名对手用精确值替换抽样值：精确枚举不贵，而单挑是最常见的情形。
        sampled[0] = EquityCalculator.HeadsUpExact(hole, board);
        return sampled.Select(Quantize).ToArray();
    }

    /// <summary>生成全部四个阶段并写入（gzip 压缩）。<paramref name="progress"/> 报告完成的阶段与耗时。</summary>
    public static void Write(Stream output, ulong seed, IReadOnlyDictionary<int, int> samples, Action<string>? progress = null)
    {
        using var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true);
        using var writer = new BinaryWriter(gzip);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(StrengthFingerprint());
        writer.Write(seed);
        writer.Write(HandClass.MaxKnownCards - HandClass.MinKnownCards + 1);

        for (int known = HandClass.MinKnownCards; known <= HandClass.MaxKnownCards; known++)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var keys = HandClass.Enumerate(known).Select(entry => entry.Key).ToArray();
            var rows = new ushort[keys.Length][];
            int count = samples[known];
            Parallel.For(0, keys.Length, index => rows[index] = ComputeRow(keys[index], count, seed));

            writer.Write(known);
            writer.Write(keys.Length);
            writer.Write(count);
            for (int index = 0; index < keys.Length; index++)
            {
                writer.Write(keys[index]);
                foreach (ushort value in rows[index])
                {
                    writer.Write(value);
                }
            }
            progress?.Invoke($"已知 {known} 张：{keys.Length} 行，每行抽样 {count:N0} 次，用时 {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s");
        }
    }

    /// <summary>
    /// 牌力表的指纹。表里的胜率依赖牌型大小；规则判定一改，指纹就变，加载旧表时直接报错，
    /// 而不是让人机拿着按旧规则算的胜率打新规则的牌。
    /// </summary>
    internal static byte[] StrengthFingerprint() => SHA256.HashData(StrengthTable.RawForFingerprint());

    internal static ushort Quantize(double equity) => (ushort)Math.Round(Math.Clamp(equity, 0, 1) * ushort.MaxValue);

    // 让每行的随机序列互不相关：把行编码混进总种子后再过一遍 SplitMix。
    private static ulong RowSeed(ulong seed, uint key) => new SplitMix64(seed ^ ((ulong)key * 0x9E3779B97F4A7C15UL)).Next();
}
