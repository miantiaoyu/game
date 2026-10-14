namespace 暗潮航线.规则;

/// <summary>
/// 一轮下注的状态机。只管规则：谁该行动、能做什么、每个动作改变什么。
/// 不负责发牌与结算，也不直接改整场筹码；本轮的投入只记在这里，结算时再处理。
/// 术语（见 docs/standard-rules.md 第 4 节）：
/// 目标额＝本轮每人应投到的累计金额；跟注额＝本人距离目标额还差多少。
/// 池 P＝本轮开始前已计入普通池的总额（含底注与之前各轮），不含改航附加池。
/// </summary>
public sealed class 下注轮
{
    private readonly int[] 行动顺序;      // 本局在座的座位，按行动方向排列
    private readonly int[] 本轮投入;      // 每人在本轮已投入的累计金额
    private readonly int[] 剩余筹码;      // 每人还能投入多少（进入本轮时的余额）
    private readonly bool[] 已弃牌;
    private readonly bool[] 已全下;
    private readonly bool[] 待行动;       // 本人是否还需要响应本轮

    // 加价权（规则 4.4）：尚未行动、或此前只过牌且尚未面对下注的人保留加价权；
    // 已经下注或跟注的人，要等目标额在自己上次行动后累计增加够一个完整加价幅度，才重新获得。
    private readonly bool[] 尚未行动;
    private readonly bool[] 上次只过牌;
    private readonly int[] 上次行动时目标额;
    private readonly bool[] 有加价权;

    private int 行动位置 = -1;

    public int 底注 { get; }
    public int 起始池 { get; }
    /// <summary>本轮累计目标额。</summary>
    public int 目标额 { get; private set; }
    /// <summary>本次完整加价所需的最小增额；每轮重新从 2 倍底注开始。</summary>
    public int 最小完整加价 { get; private set; }
    /// <summary>本轮各座位投入之和。</summary>
    public int 本轮总投入 { get; private set; }

    public bool 本轮结束 => 行动位置 < 0;
    public int 当前行动座位 => 本轮结束 ? -1 : 行动顺序[行动位置];

    /// <summary>
    /// 开始一轮下注。
    /// </summary>
    /// <param name="行动顺序">本轮的行动方向；只包含本局仍在座的人。</param>
    /// <param name="各座位余额">按 <b>行动顺序</b> 逐人给出的余额，长度必须等于行动顺序的长度。</param>
    public 下注轮(int 底注, int 起始池, int 座位总数, IReadOnlyList<int> 行动顺序, IReadOnlyList<int> 各座位余额)
    {
        ArgumentNullException.ThrowIfNull(行动顺序);
        ArgumentNullException.ThrowIfNull(各座位余额);
        ArgumentOutOfRangeException.ThrowIfNegative(底注);
        ArgumentOutOfRangeException.ThrowIfNegative(起始池);
        if (座位总数 < 单局.最少座位数 || 座位总数 > 单局.最多座位数)
        {
            throw new ArgumentOutOfRangeException(nameof(座位总数), 座位总数, "座位总数必须在 2～8 之间。");
        }
        if (行动顺序.Count < 2)
        {
            throw new ArgumentException("一轮下注至少需要两名玩家。", nameof(行动顺序));
        }
        if (各座位余额.Count != 行动顺序.Count)
        {
            throw new ArgumentException("余额数量必须等于行动顺序的人数。", nameof(各座位余额));
        }

        this.底注 = 底注;
        this.起始池 = 起始池;
        最小完整加价 = 底注 * 2;
        本轮投入 = new int[座位总数];
        剩余筹码 = new int[座位总数];
        已弃牌 = new bool[座位总数];
        已全下 = new bool[座位总数];
        待行动 = new bool[座位总数];
        尚未行动 = new bool[座位总数];
        上次只过牌 = new bool[座位总数];
        上次行动时目标额 = new int[座位总数];
        有加价权 = new bool[座位总数];
        this.行动顺序 = new int[行动顺序.Count];

        var 已出现 = new bool[座位总数];
        for (int 序号 = 0; 序号 < 行动顺序.Count; 序号++)
        {
            int 座位 = 行动顺序[序号];
            if (座位 < 0 || 座位 >= 座位总数 || 已出现[座位])
            {
                throw new ArgumentException("行动顺序中的座位必须有效且不重复。", nameof(行动顺序));
            }
            if (各座位余额[序号] < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(各座位余额), "余额不能为负数。");
            }

            已出现[座位] = true;
            this.行动顺序[序号] = 座位;
            剩余筹码[座位] = 各座位余额[序号];
            // 一开始就没有筹码的人等同全下，不会成为行动对象。
            已全下[座位] = 剩余筹码[座位] == 0;
            有加价权[座位] = true;
            尚未行动[座位] = true;
            待行动[座位] = true;
        }

        推到下一名可行动者(起点: 0);
    }

    public int 查看本轮投入(int 座位) => 本轮投入[检查在局内(座位)];
    public int 查看剩余筹码(int 座位) => 剩余筹码[检查在局内(座位)];
    public bool 已弃牌座位(int 座位) => 已弃牌[检查在局内(座位)];
    public bool 已全下座位(int 座位) => 已全下[检查在局内(座位)];
    public bool 有加价权座位(int 座位) => 有加价权[检查在局内(座位)];

    /// <summary>还留在本局、有资格分池的座位数（含已全下者，不含已弃牌者）。</summary>
    public int 统计在局人数()
    {
        int 人数 = 0;
        foreach (int 座位 in 行动顺序)
        {
            if (!已弃牌[座位])
            {
                人数++;
            }
        }
        return 人数;
    }

    /// <summary>还能继续行动的人数：未弃牌且还有余额。少于两人时本轮不再继续。</summary>
    public int 统计可行动人数()
    {
        int 人数 = 0;
        foreach (int 座位 in 行动顺序)
        {
            if (!已弃牌[座位] && !已全下[座位])
            {
                人数++;
            }
        }
        return 人数;
    }

    /// <summary>本轮是否完全没有发生下注。</summary>
    public bool 本轮无人下注 => 目标额 == 0;

    /// <summary>本轮开始前已投入的池加上本轮已投入，即当前普通池。</summary>
    public int 当前普通池 => 起始池 + 本轮总投入;

    /// <summary>当前行动玩家可以做什么。本轮结束后调用会抛出。</summary>
    public 合法动作 生成合法动作()
    {
        int 座位 = 当前行动座位;
        if (座位 < 0)
        {
            throw new InvalidOperationException("本轮下注已经结束。");
        }

        int 跟注额 = 目标额 - 本轮投入[座位];
        int 最高新增 = 计算最高新增投入(座位, 跟注额);
        int 最高目标额 = 本轮投入[座位] + 最高新增;
        // 首次开注至少 2 倍底注；池小于这个数时也要保证最低开注做得到。
        int 最低开注目标额 = 本轮投入[座位] + Math.Max(底注 * 2, 目标额 + 最小完整加价 - 本轮投入[座位]);
        int 最小加注目标额 = Math.Min(目标额 + 最小完整加价, 最高目标额);
        if (跟注额 == 0)
        {
            最小加注目标额 = Math.Min(最低开注目标额, 最高目标额);
        }

        return new 合法动作(
            可过牌: 跟注额 == 0,
            可跟注: 跟注额 > 0 && 剩余筹码[座位] > 0,
            跟注额: Math.Min(跟注额, 剩余筹码[座位]),
            可加注: 有加价权[座位] && 还有别人能响应(),
            最小加注目标额: 最小加注目标额,
            最大加注目标额: 最高目标额,
            可弃牌: 目标额 > 0,
            可全下: 剩余筹码[座位] > 0);
    }

    public void 过牌() => 执行(下注动作.过牌, 0);
    public void 跟注() => 执行(下注动作.跟注, 0);
    public void 弃牌() => 执行(下注动作.弃牌, 0);
    public void 全下() => 执行(下注动作.全下, 0);
    /// <summary>加注到指定累计目标额；金额必须落在合法范围内。</summary>
    public void 加注到(int 目标) => 执行(下注动作.加注, 目标);

    private void 执行(下注动作 动作, int 目标)
    {
        int 座位 = 当前行动座位;
        if (座位 < 0)
        {
            throw new 规则拒绝异常(拒绝原因.当前阶段不允许, "本轮下注已经结束。");
        }
        // 已弃牌或已全下的人不会成为当前行动座位，见 推到下一名可行动者。

        int 跟注额 = 目标额 - 本轮投入[座位];

        switch (动作)
        {
            case 下注动作.过牌:
                要求(跟注额 == 0, "有待跟注额时不能过牌。");
                break;

            case 下注动作.跟注:
                要求(跟注额 > 0, "没有待跟注额时不需要跟注。");
                投入(座位, 跟注额);
                break;

            case 下注动作.弃牌:
                // 规则：无待跟注额时不主动弃牌。
                要求(目标额 > 0, "无待跟注额时不允许主动弃牌。");
                已弃牌[座位] = true;
                待行动[座位] = false;
                break;

            case 下注动作.加注:
            {
                var 合法 = 生成合法动作();
                要求(合法.可加注, "本玩家当前没有加价权。");
                要求(目标 >= 合法.最小加注目标额 && 目标 <= 合法.最大加注目标额,
                    $"加注目标额必须在 {合法.最小加注目标额}～{合法.最大加注目标额} 之间。");
                加价(座位, 目标);
                break;
            }

            case 下注动作.全下:
            {
                要求(剩余筹码[座位] > 0, "没有可用筹码，无法全下。");
                // 全下能到多少：本人已投入 + 本次最多新增。最多新增已受余额与底池上限限制。
                int 目标候选 = 本轮投入[座位] + 计算最高新增投入(座位, 跟注额);
                if (目标候选 <= 目标额)
                {
                    // 连目标额都够不到：这是不足额跟注，直接全部投入，不抬高目标额，也就不需要加价权。
                    投入(座位, 跟注额);
                    break;
                }
                // 能抬高目标额：这是加注性质的全下。余额连跟齐都不够时，属于规则 4.4 的不足额情形，
                // 即使没有加价权也允许；余额够跟齐时需要有加价权。两种情形都必须有人能响应（规则 4.5）。
                要求(有加价权[座位] || 剩余筹码[座位] < 跟注额, "本玩家当前没有加价权。");
                要求(还有别人能响应(), "没有其他玩家能响应，不能向无人能跟的池加注。");
                加价(座位, 目标候选);
                break;
            }

        }

        待行动[座位] = false;
        尚未行动[座位] = false;
        // 只过牌、且当时还没有人下注的人，之后面对下注仍保留加价权（规则 4.4）。
        上次只过牌[座位] = 动作 == 下注动作.过牌;
        上次行动时目标额[座位] = 目标额;
        推到下一名可行动者(起点: 行动位置 + 1);
    }

    /// <summary>把目标额提高到指定值，并按是否达到完整幅度决定是否重开行动、刷新加价权。</summary>
    private void 加价(int 座位, int 新目标)
    {
        int 增额 = 新目标 - 目标额;
        if (增额 <= 0)
        {
            throw new InvalidOperationException("加注必须提高目标额。");
        }

        投入(座位, 新目标 - 本轮投入[座位]);
        目标额 = 新目标;

        // 只有达到完整幅度才更新最小加价幅度；不足额全下不改变这一档（规则 4.4）。
        if (增额 >= 最小完整加价)
        {
            最小完整加价 = 增额;
        }

        // 其他人是否还需要响应：没跟齐目标额的人一定要响应；已经跟齐的人只在完整加价时重新获得机会。
        bool 完整加价 = 增额 >= 最小完整加价;
        foreach (int 其他 in 行动顺序)
        {
            if (其他 == 座位 || 已弃牌[其他] || 已全下[其他])
            {
                continue;
            }
            待行动[其他] = 本轮投入[其他] < 目标额 || 完整加价;
            有加价权[其他] = 尚未行动[其他] || 上次只过牌[其他] || 已在完整幅度后重新获得(其他);
        }
    }

    /// <summary>
    /// 已经下注或跟注的人，要在自己上次行动后目标额累计增加够一个完整加价幅度，才重新获得加价权。
    /// </summary>
    private bool 已在完整幅度后重新获得(int 座位) =>
        目标额 - 上次行动时目标额[座位] >= 最小完整加价;

    /// <summary>投入金额，把待行动名单交给下一位。金额不能超过本人剩余筹码。</summary>
    private void 投入(int 座位, int 金额)
    {
        int 实投 = Math.Min(金额, 剩余筹码[座位]);
        本轮投入[座位] += 实投;
        本轮总投入 += 实投;
        剩余筹码[座位] -= 实投;
        if (剩余筹码[座位] == 0)
        {
            已全下[座位] = true;
        }
    }

    /// <summary>
    /// 底池限注下本人本次最多还能新增投入多少（规则 4.3）。
    /// 记 P 为行动前可计入的普通下注池总额（含底注、之前各轮与本轮已投入的部分，含待响应的注），
    /// C 为本人要补齐的跟注额：先跟 C、池变 P＋C，最多再加 P＋C，所以本次最多新增 P＋2C。
    /// 无人下注时 C＝0，最高开注额即为 P。
    /// 另外保证最低开注 2a 始终做得到：池总额小于 2a 时（例如两人桌只交过 1 枚底注），
    /// 若严格按 P 封顶，就会出现“规则要求至少开 2a、又不允许开 2a”的矛盾。
    /// </summary>
    private int 计算最高新增投入(int 座位, int 跟注额)
    {
        int 池 = 当前普通池;
        int 上限 = 池 + 2 * 跟注额;
        if (跟注额 == 0)
        {
            上限 = Math.Max(上限, 底注 * 2 - 本轮投入[座位]);
        }
        return Math.Min(剩余筹码[座位], 上限);
    }

    /// <summary>
    /// 是否还有别人可能响应本次加注：未弃牌且还有筹码的人。
    /// 已全下者不再行动，只剩自己有时不许向无人能跟的池加注（规则 4.5）。
    /// </summary>
    private bool 还有别人能响应()
    {
        foreach (int 座位 in 行动顺序)
        {
            if (座位 != 当前行动座位 && !已弃牌[座位] && !已全下[座位])
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 从指定位置开始，找下一个还需要行动的人；找不到就结束本轮。
    /// 只有一人还有筹码时，不继续向无人能跟的池下注（规则 4.5），本轮直接结束。
    /// </summary>
    private void 推到下一名可行动者(int 起点)
    {
        if (统计可行动人数() < 2)
        {
            行动位置 = -1;
            return;
        }

        for (int 步 = 0; 步 < 行动顺序.Length; 步++)
        {
            int 位置 = (起点 + 步) % 行动顺序.Length;
            int 座位 = 行动顺序[位置];
            if (!已弃牌[座位] && !已全下[座位] && 待行动[座位])
            {
                行动位置 = 位置;
                return;
            }
        }

        行动位置 = -1;
    }

    private static void 要求(bool 条件, string 说明)
    {
        if (!条件)
        {
            throw new 规则拒绝异常(拒绝原因.当前阶段不允许, 说明);
        }
    }

    private int 检查在局内(int 座位)
    {
        if (座位 < 0 || 座位 >= 已弃牌.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(座位), 座位, "座位编号超出范围。");
        }
        if (Array.IndexOf(行动顺序, 座位) < 0)
        {
            throw new 规则拒绝异常(拒绝原因.当前阶段不允许, "该座位不在本轮下注名单中。");
        }
        return 座位;
    }
}
