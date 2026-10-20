namespace DarkTide.Bots;

/// <summary>
/// 小而确定的伪随机数发生器（SplitMix64），用于离线生成胜率表的抽样，以及自我对战平台里可复现的洗牌。
///
/// 不用 System.Random：它的带种子序列没有承诺跨 .NET 版本不变，而胜率表要求“同样的种子永远算出同样的表”，
/// 这样测试才能抽查重算、逐位比对。它不是密码学安全的，绝不能用来洗牌——洗牌用 <see cref="DarkTide.Rules.Shuffler"/>。
/// </summary>
public struct SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>[0, n) 的整数。乘法取高位的做法偏差约 n/2^32，n 不超过 27，可以忽略。</summary>
    public int NextInt(int n) => (int)(((Next() >> 32) * (ulong)n) >> 32);
}
