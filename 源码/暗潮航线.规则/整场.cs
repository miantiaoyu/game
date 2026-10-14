namespace 暗潮航线.规则;

/// <summary>
/// 一整场比赛：最多 12 局，筹码跨局保留，局后筹码为零者淘汰。
/// 按座位号保存筹码；入座和离座由房间负责，本对象只认座位。
/// 不改动底牌或公共牌，那些属于单局状态。
/// </summary>
public sealed class 整场
{
    public const int 标准局数 = 12;
    public const int 标准初始筹码 = 100;
    // 前六局底注 1，后六局底注 2，与 标准局数 的一半对应。
    public const int 前半底注 = 1;
    public const int 后半底注 = 2;

    private readonly int[] 座位筹码;

    public int 座位总数 => 座位筹码.Length;
    public int 最大局数 { get; }

    /// <summary>当前局号，从 1 开始；尚未开局时为 0。</summary>
    public int 当前局号 { get; private set; }

    public 整场(int 座位总数, int 每人初始筹码 = 标准初始筹码, int 最大局数 = 标准局数)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(座位总数, 单局.最少座位数);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(座位总数, 单局.最多座位数);
        ArgumentOutOfRangeException.ThrowIfNegative(每人初始筹码);
        ArgumentOutOfRangeException.ThrowIfLessThan(最大局数, 1);

        this.最大局数 = 最大局数;
        座位筹码 = new int[座位总数];
        Array.Fill(座位筹码, 每人初始筹码);
    }

    public bool 全部局数已完成 => 当前局号 >= 最大局数;

    public 筹码 查看筹码(int 座位) => new(座位筹码[检查座位(座位)]);

    /// <summary>本局底注：第 12 局中位于前半段时交 前半底注，其余交 后半底注。</summary>
    public 筹码 计算底注()
    {
        if (当前局号 < 1)
        {
            throw new InvalidOperationException("尚未开局，没有底注。");
        }

        // 用“局号是否超过一半”判断，而不是写死第 6 局，这样改变最大局数时同样成立。
        return new 筹码(当前局号 * 2 <= 最大局数 ? 前半底注 : 后半底注);
    }

    public void 开始下一局()
    {
        if (全部局数已完成)
        {
            throw new InvalidOperationException($"整场已经进行完 {最大局数} 局。");
        }

        当前局号++;
    }

    /// <summary>
    /// 收取本局底注。名单由调用方给出，已淘汰的座位不应出现在名单里。
    /// 按标准规则，筹码不足底注时也要全部交出，所以某个座位可能拿不出足额底注。
    /// </summary>
    public void 收取底注(IEnumerable<int> 参与座位)
    {
        ArgumentNullException.ThrowIfNull(参与座位);
        var 底注 = 计算底注();
        var 已收取 = new bool[座位总数];

        foreach (int 座位 in 参与座位)
        {
            检查座位(座位);
            if (已收取[座位])
            {
                throw new ArgumentException($"座位 {座位} 在底注名单中出现了多次。", nameof(参与座位));
            }
            if (座位筹码[座位] == 0)
            {
                // 名单应由规则生成，出现零筹码座位说明调用方把已淘汰的人算进来了。
                throw new ArgumentException($"座位 {座位} 已经没有筹码，不应参与本局。", nameof(参与座位));
            }

            已收取[座位] = true;
            座位筹码[座位] -= Math.Min(座位筹码[座位], 底注.数量);
        }
    }

    /// <summary>把某个奖池判给一名玩家。结算分池时使用；筹码守恒由上层保证。</summary>
    public void 增加筹码(int 座位, 筹码 数量)
    {
        检查座位(座位);
        座位筹码[座位] += 数量.数量;
    }

    /// <summary>本局结算后筹码仍为零的座位，从下一局起不再参加。</summary>
    public IReadOnlyList<int> 找出被淘汰座位()
    {
        var 淘汰 = new List<int>();
        for (int 座位 = 0; 座位 < 座位总数; 座位++)
        {
            if (座位筹码[座位] == 0)
            {
                淘汰.Add(座位);
            }
        }
        return 淘汰;
    }

    public int 统计仍有筹码人数()
    {
        int 人数 = 0;
        foreach (int 筹码数 in 座位筹码)
        {
            if (筹码数 > 0)
            {
                人数++;
            }
        }
        return 人数;
    }

    /// <summary>只剩一人有筹码时整场提前结束。</summary>
    public bool 已可判定胜负 => 统计仍有筹码人数() <= 1;

    private int 检查座位(int 座位)
    {
        if (座位 < 0 || 座位 >= 座位总数)
        {
            throw new ArgumentOutOfRangeException(nameof(座位), 座位, "座位编号必须在牌桌范围内。");
        }
        return 座位;
    }
}
