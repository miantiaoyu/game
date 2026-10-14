namespace 暗潮航线.规则;

/// <summary>从确定的标准牌序中依次抽牌。同一个实例由单个执行者顺序调用。</summary>
public sealed class 抽牌堆
{
    private readonly 牌[] 牌序;
    private int 下一张位置;

    public int 剩余张数 => 牌序.Length - 下一张位置;

    public 抽牌堆(IReadOnlyList<牌> 已确定的牌序)
    {
        ArgumentNullException.ThrowIfNull(已确定的牌序);

        // 自己保存一份副本，外部随后修改传入的数组也不会影响抽牌。
        牌序 = 已确定的牌序.ToArray();

        // 必须单独检查有效性：一张未初始化的牌与所有真牌都不相等，只查重复会放它通过。
        if (牌序.Length != 牌库.标准牌数 || 牌序.Any(当前牌 => !当前牌.是否有效)
            || 牌序.Distinct().Count() != 牌库.标准牌数)
        {
            throw new ArgumentException($"牌序必须包含完整且不重复的 {牌库.标准牌数} 张标准牌。", nameof(已确定的牌序));
        }
    }

    /// <summary>返回下一张牌；抽完后返回 null，位置不再前进。</summary>
    public 牌? 抽下一张()
    {
        if (下一张位置 >= 牌序.Length)
        {
            return null;
        }

        var 抽出的牌 = 牌序[下一张位置];
        下一张位置++;
        return 抽出的牌;
    }
}
