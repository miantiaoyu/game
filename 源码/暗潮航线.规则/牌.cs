namespace 暗潮航线.规则;

/// <summary>一张创建后不能改变的牌；相同花色、点数的牌具有相同的值。</summary>
public readonly record struct 牌
{
    public const int 最小点数 = 1;
    public const int 最大点数 = 9;
    public const int 每种花色的牌数 = 最大点数 - 最小点数 + 1;

    // 只保存编号。花色和点数由编号计算，避免三份数据互相矛盾。
    // 编号从 1 开始：default(牌) 的编号为 0，是“未初始化”而不是某张真牌，
    // 这样忘记赋值的数组元素能被发现，而不会冒充“红1”混进牌局。
    public byte 编号 { get; }

    public bool 是否有效 => 编号 != 0;

    public 花色 花色 => (花色)(有效序号() / 每种花色的牌数);
    public int 点数 => 有效序号() % 每种花色的牌数 + 最小点数;

    public 牌(花色 所选花色, int 所选点数)
    {
        if (!Enum.IsDefined(所选花色))
        {
            throw new ArgumentOutOfRangeException(
                nameof(所选花色), 所选花色, "花色只能是红、蓝、绿。");
        }

        if (所选点数 < 最小点数 || 所选点数 > 最大点数)
        {
            throw new ArgumentOutOfRangeException(
                nameof(所选点数), 所选点数, $"点数必须在 {最小点数} 到 {最大点数} 之间。");
        }

        // 红牌占 1～9，蓝牌占 10～18，绿牌占 19～27。
        编号 = (byte)((int)所选花色 * 每种花色的牌数 + 所选点数 - 最小点数 + 1);
    }

    public override string ToString() => 是否有效 ? $"{花色}{点数}" : "未初始化的牌";

    // 读取未初始化的牌是程序缺陷，不是玩家能触发的业务拒绝，所以抛普通异常，让房间按故障处理。
    private int 有效序号() => 是否有效
        ? 编号 - 1
        : throw new InvalidOperationException("读取了未初始化的牌。");
}
