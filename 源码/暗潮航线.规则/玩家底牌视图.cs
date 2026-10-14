namespace 暗潮航线.规则;

/// <summary>单个玩家可以看到的底牌信息；不持有内部局面或牌堆的引用。</summary>
public sealed class 玩家底牌视图
{
    public int 本人座位 { get; }
    public IReadOnlyList<牌> 本人底牌 { get; }
    public IReadOnlyList<座位底牌概况> 其他座位 { get; }

    internal 玩家底牌视图(
        int 本人座位,
        IReadOnlyList<牌> 本人底牌,
        IReadOnlyList<座位底牌概况> 其他座位)
    {
        this.本人座位 = 本人座位;
        this.本人底牌 = Array.AsReadOnly(本人底牌.ToArray());
        this.其他座位 = Array.AsReadOnly(其他座位.ToArray());
    }
}
