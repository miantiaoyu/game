namespace DarkTide.Rules;

/// <summary>
/// 一局牌：同一副 27 张牌的牌堆、各人底牌、公共牌，以及本局的钱。
/// 牌堆归本对象所有——底牌、公共牌和改航替换都从同一副牌里抽，所以牌堆属于“一局”而不是“底牌”。
///
/// 钱的唯一真相是 <see cref="Contributions"/>：底注与各轮下注都累加到这一个数组，
/// 主池与边池由它派生（见 <see cref="PotLayering"/>），任何地方都不单独保存“池总额”。
/// 本对象只记账，不改动整场筹码；筹码的实际增减由上层按记录结算。
/// 调用方要保证顺序调用，本对象不加锁。
/// </summary>
public sealed class Hand
{
    public const int HoleCardsPerPlayer = 2;
    public const int CommunityCardCount = 3;

    private readonly DrawPile _deck;
    private readonly Card[][] _holeCards;
    private readonly List<Card> _communityCards = [];
    private readonly int[] _contributions;
    private readonly bool[] _folded;
    private readonly bool[] _allIn;

    public int SeatCount { get; }

    /// <summary>本局参与者及发牌顺序；后续行动顺序以此为准，不从底牌张数反推。</summary>
    public IReadOnlyList<int> ParticipantOrder { get; }
    public int ParticipantCount => ParticipantOrder.Count;
    public int RemainingCount => _deck.RemainingCount;

    /// <summary>当前操作轮次；只往前推进。</summary>
    public HandStage CurrentStage { get; private set; } = HandStage.Ante;

    /// <summary>
    /// 各座位在本局的累计普通投入（含底注与各轮下注），按座位号索引。
    /// 这是活视图：随着下注进行会同步变化，不是快照。资金分配与结算以此刻的值计算。
    /// </summary>
    public IReadOnlyList<int> Contributions { get; }

    /// <summary>已揭开的公共牌，按揭牌先后排列，最多 <see cref="CommunityCardCount"/> 张。</summary>
    public IReadOnlyList<Card> CommunityCards => _communityCards;

    /// <summary>本局全部投入之和：底注＋各轮下注，不含改航费。</summary>
    public int RegularPotTotal => _contributions.Sum();

    public Hand(int SeatCount, IReadOnlyList<Card> drawOrder, IReadOnlyList<int> ParticipantOrder)
    {
        if (SeatCount < TableLimits.MinSeats || SeatCount > TableLimits.MaxSeats)
        {
            throw new ArgumentOutOfRangeException(nameof(SeatCount), SeatCount, $"牌桌座位总数必须在 {TableLimits.MinSeats}～{TableLimits.MaxSeats} 之间。");
        }

        ArgumentNullException.ThrowIfNull(drawOrder);
        ArgumentNullException.ThrowIfNull(ParticipantOrder);

        var order = ParticipantOrder.ToArray();
        if (order.Length < TableLimits.MinParticipants || order.Length > SeatCount
            || order.Any(seat => seat < 0 || seat >= SeatCount)
            || order.Distinct().Count() != order.Length)
        {
            throw new ArgumentException($"本局至少需要 {TableLimits.MinParticipants} 个参与座位，编号必须在牌桌范围内且不能重复。", nameof(ParticipantOrder));
        }

        this.SeatCount = SeatCount;
        // 顺序已是私有副本；只读包装只创建一次，读取属性时不再复制。
        this.ParticipantOrder = Array.AsReadOnly(order);

        _contributions = new int[SeatCount];
        _folded = new bool[SeatCount];
        _allIn = new bool[SeatCount];
        // 复用自己的数组，读取属性时不再分配；因为数组后面会变，这里包的是活视图。
        Contributions = Array.AsReadOnly(_contributions);

        // 独立持有牌堆，构造完成后外部无法继续抽走本局的牌。
        _deck = new DrawPile(drawOrder);

        _holeCards = new Card[SeatCount][];
        for (int seat = 0; seat < SeatCount; seat++)
        {
            _holeCards[seat] = Array.Empty<Card>();
        }
        foreach (int seat in order)
        {
            _holeCards[seat] = new Card[HoleCardsPerPlayer];
        }

        // 先给每人一张，再按相同顺序发第二张。
        for (int round = 0; round < HoleCardsPerPlayer; round++)
        {
            foreach (int seat in order)
            {
                _holeCards[seat][round] = _deck.DrawNext()
                    ?? throw new InvalidOperationException("完整标准牌库应足够发放所有参与者的底牌。");
            }
        }
    }

    /// <summary>
    /// 收取底注并记入累计投入，返回每个座位按座位号索引的实付金额。
    /// 规则允许筹码不足底注时全部交出，所以实付可能少于底注；这里用 <c>Math.Min</c> 而不是让余额变负。
    /// 余额由调用方给出，本对象不持有整场筹码。
    /// </summary>
    public int[] CollectAntes(int anteAmount, IReadOnlyList<int> availableChips)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(anteAmount);
        ArgumentNullException.ThrowIfNull(availableChips);
        if (availableChips.Count != SeatCount)
        {
            throw new ArgumentException("可用筹码数量必须等于座位总数。", nameof(availableChips));
        }
        if (CurrentStage != HandStage.Ante)
        {
            throw new InvalidOperationException("底注只在本局开始时收取。");
        }

        // 先整体校验再改动：中途抛异常时累计投入不能被改掉一半。
        foreach (int seat in ParticipantOrder)
        {
            if (availableChips[seat] < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(availableChips), availableChips[seat], "可用筹码不能为负数。");
            }
        }

        var paid = new int[SeatCount];
        foreach (int seat in ParticipantOrder)
        {
            int balance = availableChips[seat];
            paid[seat] = Math.Min(balance, anteAmount);
            _contributions[seat] += paid[seat];
            // 把手上所有筹码都投进去了，本局就等同全下（规则：底注不足也可以全下）。
            _allIn[seat] = paid[seat] == balance;
        }

        // 底注是本局的第一个操作轮；收完底注即进入第一轮下注。
        CurrentStage = HandStage.FirstBet;
        return paid;
    }

    /// <summary>从牌库顶揭下一张公共牌。三张揭完后不能再揭。</summary>
    public Card RevealCommunityCard()
    {
        if (_communityCards.Count >= CommunityCardCount)
        {
            // 可信流程调用错了，属于服务端缺陷，不是玩家能触发的业务拒绝。
            throw new InvalidOperationException($"本局 {CommunityCardCount} 张公共牌已经全部揭开。");
        }

        var revealed = _deck.DrawNext()
            ?? throw new InvalidOperationException("完整标准牌库应足够揭完所有公共牌。");
        _communityCards.Add(revealed);
        return revealed;
    }

    /// <summary>推进到下一个操作轮次；只能往前，不能回退或停留。</summary>
    public void AdvanceTo(HandStage stage)
    {
        if (stage <= CurrentStage)
        {
            throw new InvalidOperationException($"操作轮次只能往前推进，当前是 {CurrentStage}。");
        }

        CurrentStage = stage;
    }

    public bool HasFolded(int seat) => _folded[RequireSeat(seat)];
    public bool IsAllIn(int seat) => _allIn[RequireSeat(seat)];

    /// <summary>本轮结束后同步弃牌与全下状态。本局内状态只增不减：弃牌的人不会回到本局。</summary>
    public void RecordSeatState(int seat, bool Fold, bool AllIn)
    {
        RequireSeat(seat);
        _folded[seat] |= Fold;
        _allIn[seat] |= AllIn;
    }

    /// <summary>仅供可信调用方使用；本人座位必须来自服务端的身份与座位绑定。</summary>
    public PlayerHandView CreatePlayerView(int seat)
    {
        RequireSeat(seat);

        var otherSeats = new List<SeatCardSummary>(SeatCount - 1);
        for (int other = 0; other < SeatCount; other++)
        {
            if (other != seat)
            {
                // 只写入座位和数量，不把别人的牌放进视图。
                otherSeats.Add(new SeatCardSummary(other, _holeCards[other].Length));
            }
        }

        return new PlayerHandView(seat, _holeCards[seat], otherSeats);
    }

    private int RequireSeat(int seat)
    {
        if (seat < 0 || seat >= SeatCount)
        {
            throw new ArgumentOutOfRangeException(nameof(seat), seat, "座位编号必须在当前牌桌范围内。");
        }
        return seat;
    }
}
