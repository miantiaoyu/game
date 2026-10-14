namespace DarkTide.Rules;

/// <summary>
/// 一整场比赛：最多 12 局，筹码跨局保留，局后筹码为零者淘汰。
/// 按座位号保存筹码；入座和离座由房间负责，本对象只认座位。
/// 不改动底牌或公共牌，那些属于单局状态。
/// </summary>
public sealed class Match
{
    public const int StandardHandCount = 12;
    public const int StandardInitialChips = 100;
    // 前六局底注 1，后六局底注 2，与 标准局数 的一半对应。
    public const int EarlyAnte = 1;
    public const int LateAnte = 2;

    private readonly int[] _chipsBySeat;

    public int SeatCount => _chipsBySeat.Length;
    public int MaxHands { get; }

    /// <summary>当前局号，从 1 开始；尚未开局时为 0。</summary>
    public int CurrentHandNumber { get; private set; }

    public Match(int seatCount, int initialChips = StandardInitialChips, int maxHands = StandardHandCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(seatCount, TableLimits.MinSeats);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(seatCount, TableLimits.MaxSeats);
        ArgumentOutOfRangeException.ThrowIfNegative(initialChips);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHands, 1);

        this.MaxHands = maxHands;
        _chipsBySeat = new int[seatCount];
        Array.Fill(_chipsBySeat, initialChips);
    }

    public bool AllHandsPlayed => CurrentHandNumber >= MaxHands;

    public Chips ChipsAt(int seat) => new(_chipsBySeat[RequireSeat(seat)]);

    /// <summary>本局底注：第 12 局中位于前半段时交 前半底注，其余交 后半底注。</summary>
    public Chips CurrentAnte()
    {
        if (CurrentHandNumber < 1)
        {
            throw new InvalidOperationException("尚未开局，没有底注。");
        }

        // 用“局号是否超过一半”判断，而不是写死第 6 局，这样改变最大局数时同样成立。
        return new Chips(CurrentHandNumber * 2 <= MaxHands ? EarlyAnte : LateAnte);
    }

    public void StartNextHand()
    {
        if (AllHandsPlayed)
        {
            throw new InvalidOperationException($"整场已经进行完 {MaxHands} 局。");
        }

        CurrentHandNumber++;
    }

    /// <summary>按座位号索引的筹码快照，作为新一局 <see cref="Hand"/> 的开局余额。</summary>
    public int[] ChipsSnapshot() => (int[])_chipsBySeat.Clone();

    /// <summary>
    /// 本局还有筹码、应当参加的座位，按座位号升序。行动起点（庄位轮转）由上层决定。
    /// </summary>
    public IReadOnlyList<int> SeatsWithChips() =>
        Enumerable.Range(0, SeatCount).Where(seat => _chipsBySeat[seat] > 0).ToArray();

    /// <summary>
    /// 写回一局结算后的各座位筹码。本局内的底注、下注与退款由 <see cref="Hand"/> 记账，
    /// 整场只在开局取快照、结算时收结果，所以底注不会被扣两次。
    /// 筹码只在座位之间转移，总数必须守恒；不守恒说明结算算错了，拒绝写入并保持原状。
    /// </summary>
    public void SettleHand(IReadOnlyList<int> finalChips)
    {
        ArgumentNullException.ThrowIfNull(finalChips);
        if (CurrentHandNumber < 1)
        {
            throw new InvalidOperationException("尚未开局，没有可结算的一局。");
        }
        if (finalChips.Count != SeatCount)
        {
            throw new ArgumentException("结算筹码数量必须等于座位总数。", nameof(finalChips));
        }

        long before = 0;
        long after = 0;
        for (int seat = 0; seat < SeatCount; seat++)
        {
            if (finalChips[seat] < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(finalChips), finalChips[seat], "结算后的筹码不能为负数。");
            }
            before += _chipsBySeat[seat];
            after += finalChips[seat];
        }
        if (before != after)
        {
            throw new InvalidOperationException($"筹码不守恒：结算前共 {before}，结算后共 {after}。");
        }

        for (int seat = 0; seat < SeatCount; seat++)
        {
            _chipsBySeat[seat] = finalChips[seat];
        }
    }

    /// <summary>直接增加某座位筹码。仅供测试与工具构造局面；正常结算用 <see cref="SettleHand"/>，它会检查守恒。</summary>
    public void AddChips(int seat, Chips amount)
    {
        RequireSeat(seat);
        _chipsBySeat[seat] += amount.Amount;
    }

    /// <summary>本局结算后筹码仍为零的座位，从下一局起不再参加。</summary>
    public IReadOnlyList<int> FindEliminatedSeats()
    {
        var eliminated = new List<int>();
        for (int seat = 0; seat < SeatCount; seat++)
        {
            if (_chipsBySeat[seat] == 0)
            {
                eliminated.Add(seat);
            }
        }
        return eliminated;
    }

    public int CountSeatsWithChips()
    {
        int count = 0;
        foreach (int chips in _chipsBySeat)
        {
            if (chips > 0)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>只剩一人有筹码时整场提前结束。</summary>
    public bool IsDecided => CountSeatsWithChips() <= 1;

    private int RequireSeat(int seat)
    {
        if (seat < 0 || seat >= SeatCount)
        {
            throw new ArgumentOutOfRangeException(nameof(seat), seat, "座位编号必须在牌桌范围内。");
        }
        return seat;
    }
}
