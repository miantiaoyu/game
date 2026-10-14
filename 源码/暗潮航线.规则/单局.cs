namespace 暗潮航线.规则;

/// <summary>
/// 一局牌：同一副 27 张牌的牌堆、各人底牌、公共牌，以及本局的钱。
/// 牌堆归本对象所有——底牌、公共牌和改航替换都从同一副牌里抽，所以牌堆属于“一局”而不是“底牌”。
///
/// 钱的唯一真相是 <see cref="各座位累计投入"/>：底注与各轮下注都累加到这一个数组，
/// 主池与边池由它派生（见 <see cref="奖池划分"/>），任何地方都不单独保存“池总额”。
/// 本对象只记账，不改动整场筹码；筹码的实际增减由上层按记录结算。
/// 调用方要保证顺序调用，本对象不加锁。
/// </summary>
public sealed class 单局
{
    public const int 最少座位数 = 2;
    public const int 最多座位数 = 8;
    // 与最少座位数数值相同，但含义不同：一个是牌桌容量，一个是本局人数。
    public const int 最少参与人数 = 2;
    public const int 每人底牌数 = 2;
    public const int 公共牌张数 = 3;

    private readonly 抽牌堆 牌堆;
    private readonly 牌[][] 全部底牌;
    private readonly List<牌> 已揭公共牌 = [];
    private readonly int[] 累计投入;
    private readonly bool[] 已弃牌;
    private readonly bool[] 已全下;

    public int 座位总数 { get; }

    /// <summary>本局参与者及发牌顺序；后续行动顺序以此为准，不从底牌张数反推。</summary>
    public IReadOnlyList<int> 参与座位顺序 { get; }
    public int 本局参与人数 => 参与座位顺序.Count;
    public int 剩余张数 => 牌堆.剩余张数;

    /// <summary>当前操作轮次；只往前推进。</summary>
    public 操作轮 当前轮次 { get; private set; } = 操作轮.底注;

    /// <summary>
    /// 各座位在本局的累计普通投入（含底注与各轮下注），按座位号索引。
    /// 这是活视图：随着下注进行会同步变化，不是快照。资金分配与结算以此刻的值计算。
    /// </summary>
    public IReadOnlyList<int> 各座位累计投入 { get; }

    /// <summary>已揭开的公共牌，按揭牌先后排列，最多 <see cref="公共牌张数"/> 张。</summary>
    public IReadOnlyList<牌> 公共牌 => 已揭公共牌;

    /// <summary>本局全部投入之和：底注＋各轮下注，不含改航费。</summary>
    public int 普通池总额 => 累计投入.Sum();

    public 单局(int 座位总数, IReadOnlyList<牌> 牌序, IReadOnlyList<int> 参与座位顺序)
    {
        if (座位总数 < 最少座位数 || 座位总数 > 最多座位数)
        {
            throw new ArgumentOutOfRangeException(nameof(座位总数), 座位总数, $"牌桌座位总数必须在 {最少座位数}～{最多座位数} 之间。");
        }

        ArgumentNullException.ThrowIfNull(牌序);
        ArgumentNullException.ThrowIfNull(参与座位顺序);

        var 顺序 = 参与座位顺序.ToArray();
        if (顺序.Length < 最少参与人数 || 顺序.Length > 座位总数
            || 顺序.Any(座位 => 座位 < 0 || 座位 >= 座位总数)
            || 顺序.Distinct().Count() != 顺序.Length)
        {
            throw new ArgumentException($"本局至少需要 {最少参与人数} 个参与座位，编号必须在牌桌范围内且不能重复。", nameof(参与座位顺序));
        }

        this.座位总数 = 座位总数;
        // 顺序已是私有副本；只读包装只创建一次，读取属性时不再复制。
        this.参与座位顺序 = Array.AsReadOnly(顺序);

        累计投入 = new int[座位总数];
        已弃牌 = new bool[座位总数];
        已全下 = new bool[座位总数];
        // 复用自己的数组，读取属性时不再分配；因为数组后面会变，这里包的是活视图。
        各座位累计投入 = Array.AsReadOnly(累计投入);

        // 独立持有牌堆，构造完成后外部无法继续抽走本局的牌。
        牌堆 = new 抽牌堆(牌序);

        全部底牌 = new 牌[座位总数][];
        for (int 座位 = 0; 座位 < 座位总数; 座位++)
        {
            全部底牌[座位] = Array.Empty<牌>();
        }
        foreach (int 座位 in 顺序)
        {
            全部底牌[座位] = new 牌[每人底牌数];
        }

        // 先给每人一张，再按相同顺序发第二张。
        for (int 轮次 = 0; 轮次 < 每人底牌数; 轮次++)
        {
            foreach (int 座位 in 顺序)
            {
                全部底牌[座位][轮次] = 牌堆.抽下一张()
                    ?? throw new InvalidOperationException("完整标准牌库应足够发放所有参与者的底牌。");
            }
        }
    }

    /// <summary>
    /// 收取底注并记入累计投入，返回每个座位按座位号索引的实付金额。
    /// 规则允许筹码不足底注时全部交出，所以实付可能少于底注；这里用 <c>Math.Min</c> 而不是让余额变负。
    /// 余额由调用方给出，本对象不持有整场筹码。
    /// </summary>
    public int[] 收取底注(int 底注金额, IReadOnlyList<int> 各座位可用筹码)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(底注金额);
        ArgumentNullException.ThrowIfNull(各座位可用筹码);
        if (各座位可用筹码.Count != 座位总数)
        {
            throw new ArgumentException("可用筹码数量必须等于座位总数。", nameof(各座位可用筹码));
        }
        if (当前轮次 != 操作轮.底注)
        {
            throw new InvalidOperationException("底注只在本局开始时收取。");
        }

        // 先整体校验再改动：中途抛异常时累计投入不能被改掉一半。
        foreach (int 座位 in 参与座位顺序)
        {
            if (各座位可用筹码[座位] < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(各座位可用筹码), 各座位可用筹码[座位], "可用筹码不能为负数。");
            }
        }

        var 实付 = new int[座位总数];
        foreach (int 座位 in 参与座位顺序)
        {
            int 余额 = 各座位可用筹码[座位];
            实付[座位] = Math.Min(余额, 底注金额);
            累计投入[座位] += 实付[座位];
            // 把手上所有筹码都投进去了，本局就等同全下（规则：底注不足也可以全下）。
            已全下[座位] = 实付[座位] == 余额;
        }

        // 底注是本局的第一个操作轮；收完底注即进入第一轮下注。
        当前轮次 = 操作轮.一注;
        return 实付;
    }

    /// <summary>从牌库顶揭下一张公共牌。三张揭完后不能再揭。</summary>
    public 牌 揭公共牌()
    {
        if (已揭公共牌.Count >= 公共牌张数)
        {
            // 可信流程调用错了，属于服务端缺陷，不是玩家能触发的业务拒绝。
            throw new InvalidOperationException($"本局 {公共牌张数} 张公共牌已经全部揭开。");
        }

        var 揭开的牌 = 牌堆.抽下一张()
            ?? throw new InvalidOperationException("完整标准牌库应足够揭完所有公共牌。");
        已揭公共牌.Add(揭开的牌);
        return 揭开的牌;
    }

    /// <summary>推进到下一个操作轮次；只能往前，不能回退或停留。</summary>
    public void 推进到(操作轮 下一轮次)
    {
        if (下一轮次 <= 当前轮次)
        {
            throw new InvalidOperationException($"操作轮次只能往前推进，当前是 {当前轮次}。");
        }

        当前轮次 = 下一轮次;
    }

    public bool 已弃牌座位(int 座位) => 已弃牌[检查座位(座位)];
    public bool 已全下座位(int 座位) => 已全下[检查座位(座位)];

    /// <summary>本轮结束后同步弃牌与全下状态。本局内状态只增不减：弃牌的人不会回到本局。</summary>
    public void 记录本轮状态(int 座位, bool 弃牌, bool 全下)
    {
        检查座位(座位);
        已弃牌[座位] |= 弃牌;
        已全下[座位] |= 全下;
    }

    /// <summary>仅供可信调用方使用；本人座位必须来自服务端的身份与座位绑定。</summary>
    public 玩家底牌视图 生成玩家视图(int 本人座位)
    {
        检查座位(本人座位);

        var 其他座位 = new List<座位底牌概况>(座位总数 - 1);
        for (int 座位 = 0; 座位 < 座位总数; 座位++)
        {
            if (座位 != 本人座位)
            {
                // 只写入座位和数量，不把别人的牌放进视图。
                其他座位.Add(new 座位底牌概况(座位, 全部底牌[座位].Length));
            }
        }

        return new 玩家底牌视图(本人座位, 全部底牌[本人座位], 其他座位);
    }

    private int 检查座位(int 座位)
    {
        if (座位 < 0 || 座位 >= 座位总数)
        {
            throw new ArgumentOutOfRangeException(nameof(座位), 座位, "座位编号必须在当前牌桌范围内。");
        }
        return 座位;
    }
}
