using 暗潮航线.规则;

namespace 暗潮航线.房间;

/// <summary>
/// 本地房间原型：绑定身份与座位，按身份提供底牌视图。
/// 只能顺序调用，没有锁；并发场景通过 房间执行器 使用。不执行登录认证。
/// 修改只经由 执行(命令)；具体的 入座 等方法为 internal，供命令分派与测试使用。
/// </summary>
public sealed class 房间
{
    private readonly Guid?[] 座位玩家;
    private readonly 命令记录表 已处理命令 = new();
    private 单局? 当前单局;

    public Guid 房间编号 { get; }
    public int 座位总数 => 座位玩家.Length;
    public int 已入座人数 => 座位玩家.Count(玩家 => 玩家.HasValue);

    /// <summary>
    /// 显式记录阶段，而不是用“局面是否为空”推断：以后整场结束、单局结算等阶段
    /// 无法从某个字段是否为空区分出来。不变量：仅在 已发底牌 阶段 当前单局 非空。
    /// </summary>
    public 房间阶段 阶段 { get; private set; } = 房间阶段.等待入座;

    /// <summary>
    /// 每次成功修改状态后加一，查询不改变它。客户端带上自己看到的版本提交操作，
    /// 房间据此识别“基于过时画面的请求”；持久化时也用它做乐观并发检查。
    /// </summary>
    public long 状态版本 { get; private set; }

    public 房间(Guid 房间编号, int 座位总数)
    {
        if (房间编号 == Guid.Empty)
        {
            throw new ArgumentException("房间编号不能为空编号。", nameof(房间编号));
        }
        if (座位总数 < 单局.最少座位数 || 座位总数 > 单局.最多座位数)
        {
            throw new ArgumentOutOfRangeException(nameof(座位总数), 座位总数, $"牌桌座位总数必须在 {单局.最少座位数}～{单局.最多座位数} 之间。");
        }

        this.房间编号 = 房间编号;
        座位玩家 = new Guid?[座位总数];
    }

    /// <summary>
    /// 修改房间的唯一公开入口。顺序固定：先查原结果，再查版本，最后执行规则。
    /// 先查原结果是因为：命令成功后版本已经前进，重发若先比版本，会把成功误报为“版本不符”。
    /// </summary>
    public 命令回执 执行(房间命令 命令)
    {
        ArgumentNullException.ThrowIfNull(命令);

        if (已处理命令.尝试查找(命令.命令编号, out var 已有))
        {
            // 同编号同内容：返回原结果（成功回执或原拒绝），不再执行。
            // 同编号不同内容：拒绝，但不记录，以免覆盖原命令的结果。
            return 已有.命令 == 命令
                ? 已有.重现结果()
                : throw new 规则拒绝异常(拒绝原因.命令编号冲突, "该命令编号已用于另一项操作。");
        }

        try
        {
            if (命令.预期版本 is long 预期 && 预期 != 状态版本)
            {
                throw new 规则拒绝异常(拒绝原因.版本不符, $"房间状态已变化（当前版本 {状态版本}），请刷新后重新操作。");
            }

            // 命令种类是封闭集合，用 switch 穷举；新增命令却忘了处理时，会落到最后一个分支并作为故障暴露。
            switch (命令)
            {
                case 入座命令 入座请求:
                    入座(入座请求.玩家编号, 入座请求.座位);
                    break;
                case 离座命令 离座请求:
                    离座(离座请求.玩家编号);
                    break;
                case 发底牌命令 发牌请求:
                    发底牌(发牌请求.牌序, 发牌请求.参与座位顺序);
                    break;
                default:
                    throw new InvalidOperationException($"未处理的命令类型：{命令.GetType().Name}。");
            }
        }
        catch (规则拒绝异常 拒绝)
        {
            // 拒绝也是确定的结果：同一命令重发时应得到同样的拒绝，而不是在状态变化后意外成功。
            已处理命令.记录拒绝(命令, 拒绝);
            throw;
        }

        var 回执 = new 命令回执(命令.命令编号, 状态版本);
        已处理命令.记录成功(命令, 回执);
        return 回执;
    }

    /// <summary>发牌前绑定玩家与座位；同一玩家在本房间只能占一个座位。</summary>
    internal void 入座(Guid 玩家编号, int 座位)
    {
        检查玩家编号(玩家编号);
        要求阶段(房间阶段.等待入座, "已发底牌，不允许更换成员。");
        if (座位 < 0 || 座位 >= 座位总数)
        {
            throw new 规则拒绝异常(拒绝原因.座位超出范围, "座位编号必须在当前牌桌范围内。");
        }
        if (查找座位(玩家编号) is not null)
        {
            throw new 规则拒绝异常(拒绝原因.玩家已入座, "该玩家已经在本房间入座。");
        }
        if (座位玩家[座位] is not null)
        {
            throw new 规则拒绝异常(拒绝原因.座位已有玩家, "该座位已有玩家。");
        }

        座位玩家[座位] = 玩家编号;
        状态版本++;
    }

    /// <summary>仅在发牌前释放自己的座位。断线不应调用此方法。</summary>
    internal void 离座(Guid 玩家编号)
    {
        要求阶段(房间阶段.等待入座, "已发底牌，不允许更换成员。");
        int 座位 = 查找座位(玩家编号)
            ?? throw new 规则拒绝异常(拒绝原因.玩家未入座, "该玩家未在本房间入座。");

        座位玩家[座位] = null;
        状态版本++;
    }

    public int? 查找座位(Guid 玩家编号)
    {
        检查玩家编号(玩家编号);
        for (int 座位 = 0; 座位 < 座位总数; 座位++)
        {
            if (座位玩家[座位] == 玩家编号)
            {
                return 座位;
            }
        }
        return null;
    }

    /// <summary>可信流程提供牌序与参与顺序；目前只能发一次底牌，尚无整场与多局流程。</summary>
    internal void 发底牌(IReadOnlyList<牌> 牌序, IReadOnlyList<int> 参与座位顺序)
    {
        要求阶段(房间阶段.等待入座, "已发底牌，不允许重复发牌。");
        ArgumentNullException.ThrowIfNull(牌序);
        ArgumentNullException.ThrowIfNull(参与座位顺序);
        var 顺序 = 参与座位顺序.ToArray();

        foreach (int 座位 in 顺序)
        {
            if (座位 < 0 || 座位 >= 座位总数)
            {
                throw new ArgumentException("参与座位编号超出牌桌范围。", nameof(参与座位顺序));
            }
            if (座位玩家[座位] is null)
            {
                // 决定名单后、入队执行前可能有人离座，属于正常的状态竞争。
                throw new 规则拒绝异常(拒绝原因.参与座位无玩家, "参与发牌的座位必须已有玩家。");
            }
        }

        // 单局继续检查名单人数、重复座位与牌序。
        // 先在局部变量里构造；成功后才一起修改三个字段，失败时房间保持原状。
        var 新局面 = new 单局(座位总数, 牌序, 顺序);
        当前单局 = 新局面;
        阶段 = 房间阶段.已发底牌;
        状态版本++;
    }

    /// <summary>每次生成新快照；之后房间再变化，已交出的快照不受影响。</summary>
    public 房间概况 生成概况()
    {
        var 座位有人 = new bool[座位总数];
        for (int 座位 = 0; 座位 < 座位总数; 座位++)
        {
            座位有人[座位] = 座位玩家[座位].HasValue;
        }

        // 局面里的参与顺序本身不可变，可以直接共享，不必复制。
        IReadOnlyList<int> 参与顺序 = 当前单局?.参与座位顺序 ?? Array.Empty<int>();
        return new 房间概况(房间编号, 阶段, 状态版本, Array.AsReadOnly(座位有人), 参与顺序);
    }

    /// <summary>玩家编号必须来自已认证身份；不能直接使用客户端自报编号。</summary>
    public 玩家底牌视图 获取本人视图(Guid 玩家编号)
    {
        int 本人座位 = 查找座位(玩家编号)
            ?? throw new 规则拒绝异常(拒绝原因.玩家未入座, "该玩家未在本房间入座，无权查看底牌视图。");
        要求阶段(房间阶段.已发底牌, "尚未发底牌。");

        // 阶段检查已保证局面存在；若为空说明不变量被破坏，应作为故障暴露，而不是当成业务拒绝。
        var 局面 = 当前单局 ?? throw new InvalidOperationException("已发底牌阶段缺少单局。");
        return 局面.生成玩家视图(本人座位);
    }

    private void 要求阶段(房间阶段 预期阶段, string 说明)
    {
        if (阶段 != 预期阶段)
        {
            throw new 规则拒绝异常(拒绝原因.当前阶段不允许, 说明);
        }
    }

    private static void 检查玩家编号(Guid 玩家编号)
    {
        if (玩家编号 == Guid.Empty)
        {
            throw new ArgumentException("玩家编号不能为空编号。", nameof(玩家编号));
        }
    }
}
