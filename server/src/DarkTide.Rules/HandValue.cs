namespace DarkTide.Rules;

/// <summary>
/// 三张牌的强弱，可以直接比较大小。相等即并列（花色不分胜负）。
///
/// 编码成一个整数：牌型占最高位，其后依次是规则规定的比较数字，每个数字占 4 位。
/// 这样“先比牌型，再按规定顺序逐个比数字”就变成一次整数比较，不必为每种牌型写比较逻辑。
/// 例：对子 7 带 3 → 牌型 1，数字 7、3 → 0x1_730；散牌 9、5、2 → 0x0_952。
/// 用不到的数字位填 0，因为 0 不是合法点数，不会和真实数字混淆。
/// </summary>
public readonly record struct HandValue : IComparable<HandValue>
{
    private readonly int _encoded;

    private HandValue(int encoded) => _encoded = encoded;

    public HandCategory Category => (HandCategory)(_encoded >> 12);

    /// <summary>按比较顺序排列的数字，长度随牌型而定（三条、顺子类 1 个，对子 2 个，同花、散牌 3 个）。</summary>
    public IReadOnlyList<int> Ranks
    {
        get
        {
            var ranks = new List<int>(3);
            for (int shift = 8; shift >= 0; shift -= 4)
            {
                int rank = (_encoded >> shift) & 0xF;
                if (rank != 0)
                {
                    ranks.Add(rank);
                }
            }
            return ranks;
        }
    }

    internal static HandValue Create(HandCategory category, int first, int second = 0, int third = 0) =>
        new(((int)category << 12) | (first << 8) | (second << 4) | third);

    public int CompareTo(HandValue other) => _encoded.CompareTo(other._encoded);

    public static bool operator <(HandValue left, HandValue right) => left._encoded < right._encoded;
    public static bool operator >(HandValue left, HandValue right) => left._encoded > right._encoded;
    public static bool operator <=(HandValue left, HandValue right) => left._encoded <= right._encoded;
    public static bool operator >=(HandValue left, HandValue right) => left._encoded >= right._encoded;

    public override string ToString() => $"{Category}({string.Join(",", Ranks)})";
}
