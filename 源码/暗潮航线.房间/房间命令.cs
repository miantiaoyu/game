using 暗潮航线.规则;

namespace 暗潮航线.房间;

/// <summary>
/// 会修改房间状态的请求。命令是数据而不是代码：可以比较、记录、去重，以后也能持久化和重放。
/// 构造函数检查格式，所以拿到的命令对象一定是格式合法的；是否被当前状态允许由房间判断。
/// 属性只有 get，<c>with</c> 表达式无法绕过检查改出非法命令。
/// </summary>
public abstract record 房间命令
{
    /// <summary>调用方生成；同一编号只会产生一次效果，重发返回原结果。</summary>
    public Guid 命令编号 { get; }

    /// <summary>调用方提交时看到的状态版本；为空表示不检查（仅限可信流程）。</summary>
    public long? 预期版本 { get; }

    // private protected：只有本程序集能派生，命令种类是封闭集合，房间可以穷举处理。
    private protected 房间命令(Guid 命令编号, long? 预期版本)
    {
        if (命令编号 == Guid.Empty)
        {
            throw new ArgumentException("命令编号不能为空编号。", nameof(命令编号));
        }
        if (预期版本 < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(预期版本), 预期版本, "预期版本不能为负数。");
        }

        this.命令编号 = 命令编号;
        this.预期版本 = 预期版本;
    }

    private protected static void 检查玩家编号(Guid 玩家编号)
    {
        if (玩家编号 == Guid.Empty)
        {
            throw new ArgumentException("玩家编号不能为空编号。", nameof(玩家编号));
        }
    }
}

/// <summary>玩家编号必须来自已认证身份。座位号来自客户端，越界由房间以规则拒绝回应。</summary>
public sealed record 入座命令 : 房间命令
{
    public Guid 玩家编号 { get; }
    public int 座位 { get; }

    public 入座命令(Guid 命令编号, Guid 玩家编号, int 座位, long? 预期版本 = null)
        : base(命令编号, 预期版本)
    {
        检查玩家编号(玩家编号);
        this.玩家编号 = 玩家编号;
        this.座位 = 座位;
    }
}

public sealed record 离座命令 : 房间命令
{
    public Guid 玩家编号 { get; }

    public 离座命令(Guid 命令编号, Guid 玩家编号, long? 预期版本 = null)
        : base(命令编号, 预期版本)
    {
        检查玩家编号(玩家编号);
        this.玩家编号 = 玩家编号;
    }
}

/// <summary>仅供可信开局流程使用。构造时复制输入，之后外部修改原数组不影响命令。</summary>
public sealed record 发底牌命令 : 房间命令
{
    private readonly 牌[] 牌序副本;
    private readonly int[] 顺序副本;

    public IReadOnlyList<牌> 牌序 { get; }
    public IReadOnlyList<int> 参与座位顺序 { get; }

    public 发底牌命令(Guid 命令编号, IReadOnlyList<牌> 牌序, IReadOnlyList<int> 参与座位顺序, long? 预期版本 = null)
        : base(命令编号, 预期版本)
    {
        ArgumentNullException.ThrowIfNull(牌序);
        ArgumentNullException.ThrowIfNull(参与座位顺序);
        // 只挡住过大或明显错误的载荷；内容（重复牌、座位范围）由单局完整检查。
        if (牌序.Count != 牌库.标准牌数)
        {
            throw new ArgumentException($"必须提供 {牌库.标准牌数} 张牌的牌序。", nameof(牌序));
        }
        if (参与座位顺序.Count < 单局.最少参与人数 || 参与座位顺序.Count > 单局.最多座位数)
        {
            throw new ArgumentException(
                $"参与人数必须在 {单局.最少参与人数}～{单局.最多座位数} 之间。", nameof(参与座位顺序));
        }

        牌序副本 = 牌序.ToArray();
        顺序副本 = 参与座位顺序.ToArray();
        this.牌序 = Array.AsReadOnly(牌序副本);
        this.参与座位顺序 = Array.AsReadOnly(顺序副本);
    }

    // 记录默认按引用比较集合成员；去重需要按内容判断“是不是同一个命令”，所以自定义相等。
    public bool Equals(发底牌命令? 其他) =>
        其他 is not null
        && base.Equals(其他)
        && 牌序副本.AsSpan().SequenceEqual(其他.牌序副本)
        && 顺序副本.AsSpan().SequenceEqual(其他.顺序副本);

    public override int GetHashCode()
    {
        var 哈希 = new HashCode();
        哈希.Add(base.GetHashCode());
        foreach (var 当前牌 in 牌序副本)
        {
            哈希.Add(当前牌);
        }
        foreach (int 座位 in 顺序副本)
        {
            哈希.Add(座位);
        }
        return 哈希.ToHashCode();
    }
}
