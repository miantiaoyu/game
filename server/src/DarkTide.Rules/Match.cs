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

    public Match(int SeatCount, int initialChips = StandardInitialChips, int MaxHands = StandardHandCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(SeatCount, TableLimits.MinSeats);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SeatCount, TableLimits.MaxSeats);
        ArgumentOutOfRangeException.ThrowIfNegative(initialChips);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxHands, 1);

        this.MaxHands = MaxHands;
        _chipsBySeat = new int[SeatCount];
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

    /// <summary>
    /// 收取本局底注。名单由调用方给出，已淘汰的座位不应出现在名单里。
    /// 按标准规则，筹码不足底注时也要全部交出，所以某个座位可能拿不出足额底注。
    /// </summary>
    public void CollectAntes(IEnumerable<int> participatingSeats)
    {
        ArgumentNullException.ThrowIfNull(participatingSeats);
        var ante = CurrentAnte();
        var collected = new bool[SeatCount];

        foreach (int seat in participatingSeats)
        {
            RequireSeat(seat);
            if (collected[seat])
            {
                throw new ArgumentException($"座位 {seat} 在底注名单中出现了多次。", nameof(participatingSeats));
            }
            if (_chipsBySeat[seat] == 0)
            {
                // 名单应由规则生成，出现零筹码座位说明调用方把已淘汰的人算进来了。
                throw new ArgumentException($"座位 {seat} 已经没有筹码，不应参与本局。", nameof(participatingSeats));
            }

            collected[seat] = true;
            _chipsBySeat[seat] -= Math.Min(_chipsBySeat[seat], ante.Amount);
        }
    }

    /// <summary>把某个奖池判给一名玩家。结算分池时使用；筹码守恒由上层保证。</summary>
    public void AddChips(int seat, Chips Amount)
    {
        RequireSeat(seat);
        _chipsBySeat[seat] += Amount.Amount;
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
