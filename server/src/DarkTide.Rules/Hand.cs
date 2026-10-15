namespace DarkTide.Rules;

/// <summary>
/// 一局牌：同一副 27 张牌的牌堆、各人底牌、公共牌，以及本局的钱。
/// 牌堆归本对象所有——底牌、公共牌和改航替换都从同一副牌里抽，所以牌堆属于“一局”而不是“底牌”。
///
/// 钱：开局时从 <see cref="Match"/> 取得各人余额，本局内由本对象记账。
/// <see cref="Contributions"/> 是普通奖池的唯一真相：底注与各轮下注都累加到这一个数组，
/// 主池与边池由它派生（见 <see cref="PotLayering"/>），任何地方都不单独保存“池总额”。
/// 守恒：对每个座位，开局余额 = <see cref="StackAt"/> + 累计投入（尚无改航费）。
/// 结算时由上层把余额与赢得的奖池写回整场。
///
/// 流程（规则第 3 节）：收底注 → 揭第一张公共牌 → 一注 → 揭第二张 → 二注 → 改航 → 揭第三张 → 三注 → 摊牌。
/// 只剩一名未弃牌者时直接结束；只剩不足两人能行动时跳过下注，但牌照常揭（规则 4.5）。
/// 调用方要保证顺序调用，本对象不加锁。
/// </summary>
public sealed class Hand
{
    public const int HoleCardsPerPlayer = 2;
    public const int CommunityCardCount = 3;

    private readonly DrawPile _deck;
    private readonly Card[][] _holeCards;
    private readonly List<Card> _communityCards = [];
    private readonly bool[] _participating;
    private readonly int[] _stacks;
    private readonly int[] _contributions;
    private readonly bool[] _folded;
    private readonly int[] _startingStacks;
    private BettingRound? _round;
    private HandSettlement? _settlement;

    public int SeatCount { get; }

    /// <summary>本局参与者及发牌顺序；三轮下注都按这个方向行动，不从底牌张数反推。</summary>
    public IReadOnlyList<int> ParticipantOrder { get; }
    public int ParticipantCount => ParticipantOrder.Count;
    public int RemainingCount => _deck.RemainingCount;

    /// <summary>本局底注，决定最低开注额（2 倍底注）。</summary>
    public int Ante { get; }

    /// <summary>当前操作轮次；只往前推进。</summary>
    public HandStage CurrentStage { get; private set; } = HandStage.Ante;

    /// <summary>当前是否正在进行一轮下注。</summary>
    public bool IsBetting => _round is not null;

    /// <summary>本局已经结束：只剩一名未弃牌者，或已到摊牌。</summary>
    public bool IsComplete => CurrentStage == HandStage.Showdown || CountNotFolded() <= 1;

    /// <summary>
    /// 各座位在本局的累计普通投入（含底注与已结束各轮的下注），按座位号索引。
    /// 只在两轮下注之间更新：进行中的一轮，其投入在本轮关闭、退还超额之后才并入。
    /// 这是活视图，不是快照；资金分配与结算以下注轮之间的值计算。
    /// </summary>
    public IReadOnlyList<int> Contributions { get; }

    /// <summary>已揭开的公共牌，按揭牌先后排列，最多 <see cref="CommunityCardCount"/> 张。</summary>
    public IReadOnlyList<Card> CommunityCards => _communityCards;

    /// <summary>已并入的普通投入之和：底注＋已结束各轮的下注，不含改航费。</summary>
    public int RegularPotTotal => _contributions.Sum();

    /// <param name="startingStacks">按座位号索引的开局余额（来自 <see cref="Match"/>）；参与座位必须大于 0。</param>
    public Hand(int seatCount, IReadOnlyList<Card> drawOrder, IReadOnlyList<int> participantOrder, int ante, IReadOnlyList<int> startingStacks)
    {
        if (seatCount < TableLimits.MinSeats || seatCount > TableLimits.MaxSeats)
        {
            throw new ArgumentOutOfRangeException(nameof(seatCount), seatCount, $"牌桌座位总数必须在 {TableLimits.MinSeats}～{TableLimits.MaxSeats} 之间。");
        }

        ArgumentNullException.ThrowIfNull(drawOrder);
        ArgumentNullException.ThrowIfNull(participantOrder);
        ArgumentNullException.ThrowIfNull(startingStacks);
        // 底注至少 1：底注为 0 时最低开注额也是 0，“开注”和“过牌”无法区分。
        ArgumentOutOfRangeException.ThrowIfLessThan(ante, 1);

        var order = participantOrder.ToArray();
        if (order.Length < TableLimits.MinParticipants || order.Length > seatCount
            || order.Any(seat => seat < 0 || seat >= seatCount)
            || order.Distinct().Count() != order.Length)
        {
            throw new ArgumentException($"本局至少需要 {TableLimits.MinParticipants} 个参与座位，编号必须在牌桌范围内且不能重复。", nameof(participantOrder));
        }
        if (startingStacks.Count != seatCount)
        {
            throw new ArgumentException("开局余额数量必须等于座位总数。", nameof(startingStacks));
        }
        // 先整体校验再改动：构造失败时不留下半成品。
        foreach (int seat in order)
        {
            if (startingStacks[seat] <= 0)
            {
                // 名单由规则生成；零筹码座位出现在名单里，说明调用方把已淘汰的人算进来了。
                throw new ArgumentException($"座位 {seat} 没有筹码，不应参与本局。", nameof(startingStacks));
            }
        }

        SeatCount = seatCount;
        Ante = ante;
        // 顺序已是私有副本；只读包装只创建一次，读取属性时不再复制。
        ParticipantOrder = Array.AsReadOnly(order);

        _startingStacks = startingStacks.ToArray();
        _participating = new bool[seatCount];
        _stacks = new int[seatCount];
        _contributions = new int[seatCount];
        _folded = new bool[seatCount];
        foreach (int seat in order)
        {
            _participating[seat] = true;
            _stacks[seat] = startingStacks[seat];
        }
        // 复用自己的数组，读取属性时不再分配；因为数组后面会变，这里包的是活视图。
        Contributions = Array.AsReadOnly(_contributions);

        // 独立持有牌堆，构造完成后外部无法继续抽走本局的牌。
        _deck = new DrawPile(drawOrder);

        _holeCards = new Card[seatCount][];
        for (int seat = 0; seat < seatCount; seat++)
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
    /// 筹码不足底注时全部交出（规则：底注不足也可以全下），所以实付可能少于底注。
    /// </summary>
    public int[] CollectAntes()
    {
        RequireStage(HandStage.Ante, "底注只在本局开始时收取。");

        var paid = new int[SeatCount];
        foreach (int seat in ParticipantOrder)
        {
            paid[seat] = Math.Min(_stacks[seat], Ante);
            _stacks[seat] -= paid[seat];
            _contributions[seat] += paid[seat];
        }

        // 底注是本局的第一个操作轮；收完底注即进入第一轮下注。
        CurrentStage = HandStage.FirstBet;
        return paid;
    }

    /// <summary>
    /// 揭开本轮的公共牌，并在至少两人能行动时开始一轮下注。
    /// 返回是否开始了下注；不足两人能行动时直接跳过本轮下注，牌照常揭开（规则 4.5）。
    /// 一注揭第一张、二注揭第二张、三注揭第三张；三注只能在改航结束后开始。
    /// </summary>
    public bool StartBettingRound()
    {
        RequireNotComplete();
        if (_round is not null)
        {
            throw new InvalidOperationException("上一轮下注还没有结束。");
        }

        int cardsBefore = CurrentStage switch
        {
            HandStage.FirstBet => 0,
            HandStage.SecondBet => 1,
            HandStage.ThirdBet => 2,
            _ => throw new InvalidOperationException($"当前轮次 {CurrentStage} 不能开始下注。")
        };
        if (_communityCards.Count != cardsBefore)
        {
            throw new InvalidOperationException($"{CurrentStage} 轮已经开始过。");
        }

        RevealCommunityCard();

        var actionOrder = ParticipantOrder.Where(seat => !_folded[seat]).ToArray();
        if (actionOrder.Count(seat => _stacks[seat] > 0) < 2)
        {
            // 跳过的一轮同样算“本轮关闭”：底注阶段就可能出现无人跟的超额（对手底注不足），要在这里退还。
            RefundUnmatchedExcess();
            AfterRound();
            return false;
        }

        _round = new BettingRound(
            Ante,
            RegularPotTotal,
            SeatCount,
            actionOrder,
            actionOrder.Select(seat => _stacks[seat]).ToArray());
        // 至少两人能行动时，下注轮构造后一定有人需要行动；这里只作为防御性检查。
        if (_round.IsClosed)
        {
            CloseRound();
            return false;
        }
        return true;
    }

    /// <summary>当前行动座位；没有进行中的下注时为 -1。</summary>
    public int ActingSeat => _round?.ActingSeat ?? -1;

    /// <summary>当前行动者可以做什么。没有进行中的下注时拒绝。</summary>
    public LegalActions GetLegalActions() => RequireRound().GetLegalActions();

    /// <summary>
    /// 由 <paramref name="seat"/> 执行一个下注动作。不是该座位行动时以 <see cref="RejectionReason.NotYourTurn"/> 拒绝；
    /// 动作或金额不合规则时以 <see cref="RejectionReason.IllegalAction"/> 拒绝。拒绝前不改动任何状态。
    /// <paramref name="raiseTarget"/> 只在加注时使用，是本轮累计目标额。
    /// 返回本轮是否已经结束（结束时已完成超额退还并推进轮次）。
    /// </summary>
    public bool Act(int seat, BettingAction action, int raiseTarget = 0)
    {
        RequireSeat(seat);
        var round = RequireRound();
        if (round.ActingSeat != seat)
        {
            throw new RuleRejectedException(RejectionReason.NotYourTurn, "现在不是该座位行动。");
        }

        switch (action)
        {
            case BettingAction.Check:
                round.Check();
                break;
            case BettingAction.Call:
                round.Call();
                break;
            case BettingAction.Raise:
                round.RaiseTo(raiseTarget);
                break;
            case BettingAction.Fold:
                round.Fold();
                break;
            case BettingAction.AllIn:
                round.AllIn();
                break;
            default:
                // 未知动作不能落入默认的过牌或跟注分支（协议约定）。
                throw new RuleRejectedException(RejectionReason.IllegalAction, $"未知的下注动作 {(int)action}。");
        }

        // 有人弃牌后只剩一名未弃牌者：本轮即刻结束，不必等其他人响应。
        if (round.IsClosed || round.CountInHand() <= 1)
        {
            CloseRound();
            return true;
        }
        return false;
    }

    /// <summary>
    /// 结束改航窗口并进入三注。反向改航与改航费尚未实现，目前所有人视为放弃改航。
    /// </summary>
    public void SkipReroute()
    {
        RequireStage(HandStage.Reroute, "当前不是改航轮。");
        CurrentStage = HandStage.ThirdBet;
    }

    /// <summary>已经结算过（<see cref="Settle"/> 成功调用过）。</summary>
    public bool IsSettled => _settlement is not null;

    /// <summary>
    /// 结算本局（规则第 7 节）：按累计投入划分主池与边池，每个池在有资格者之间比牌，
    /// 并列平分，余数从起始位沿下注方向依次补一枚。返回各座位的最终筹码，可以直接交给 <see cref="Match.SettleHand"/>。
    ///
    /// 只能在本局结束后调用，且只能调用一次；再次调用返回同一结果。
    /// 只剩一人未弃牌时不比牌：所有池都只有他有资格，他的底牌不需要公开。
    /// 至少两人比牌时三张公共牌一定已经揭开：提前结束只发生在只剩一人的时候。
    /// 改航附加池尚未实现，这里只分普通池。
    /// </summary>
    public HandSettlement Settle()
    {
        if (_settlement is not null)
        {
            return _settlement;
        }
        if (!IsComplete || _round is not null)
        {
            throw new InvalidOperationException("本局尚未结束，不能结算。");
        }

        var folded = FoldedSnapshot();
        var pots = PotLayering.SplitRegularPots(_contributions, folded);
        bool showdown = pots.Any(pot => pot.EligibleSeats.Count >= 2);

        var values = new HandValue?[SeatCount];
        if (showdown)
        {
            if (_communityCards.Count != CommunityCardCount)
            {
                throw new InvalidOperationException("需要比牌，但公共牌没有全部揭开。");
            }
            foreach (int seat in ParticipantOrder)
            {
                if (!folded[seat])
                {
                    values[seat] = HandEvaluator.EvaluateBest(_holeCards[seat], _communityCards);
                }
            }
        }

        var results = PotAward.Distribute(pots, values, ParticipantOrder);
        // 不参加本局的座位（例如有筹码但这局没被排进名单）原样保留开局余额；本局只记了参与者的余额。
        var finalStacks = new int[SeatCount];
        for (int seat = 0; seat < SeatCount; seat++)
        {
            finalStacks[seat] = _participating[seat] ? _stacks[seat] : _startingStacks[seat];
        }
        foreach (var result in results)
        {
            for (int i = 0; i < result.Winners.Count; i++)
            {
                finalStacks[result.Winners[i]] += result.Shares[i];
            }
        }

        // 守恒：筹码只在本局参与者之间转移。不成立说明某处账算错了，不能写回整场。
        if (finalStacks.Sum() != _startingStacks.Sum())
        {
            throw new InvalidOperationException($"结算后筹码不守恒：开局共 {_startingStacks.Sum()}，结算后共 {finalStacks.Sum()}。");
        }

        _settlement = new HandSettlement(Array.AsReadOnly(finalStacks), results, showdown, Array.AsReadOnly(values));
        return _settlement;
    }

    /// <summary>剩余可用筹码：开局余额减去已投入（含进行中一轮的投入）。</summary>
    public int StackAt(int seat)
    {
        RequireSeat(seat);
        return _round is not null && _participating[seat] && !_folded[seat] ? _round.StackAt(seat) : _stacks[seat];
    }

    /// <summary>进行中一轮里该座位已投入的金额；没有进行中的下注时为 0。</summary>
    public int CommittedThisRound(int seat)
    {
        RequireSeat(seat);
        return _round is not null && _participating[seat] && !_folded[seat] ? _round.CommittedAt(seat) : 0;
    }

    public bool HasFolded(int seat)
    {
        RequireSeat(seat);
        return _folded[seat] || (_round is not null && _participating[seat] && !_folded[seat] && _round.HasFolded(seat));
    }

    /// <summary>参与本局、未弃牌且没有可用筹码。</summary>
    public bool IsAllIn(int seat) => _participating[RequireSeat(seat)] && !HasFolded(seat) && StackAt(seat) == 0;

    /// <summary>按座位号索引的弃牌状态快照，供奖池划分使用。</summary>
    public IReadOnlyList<bool> FoldedSnapshot() => Enumerable.Range(0, SeatCount).Select(HasFolded).ToArray();

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

    /// <summary>
    /// 本轮关闭：把本轮投入并入累计投入，退还未被匹配的超额（规则 4.5），推进轮次。
    /// 退款按“本局累计投入”计算而不是“本轮投入”：最高者的超额是相对第二高的累计投入而言的。
    /// 退款后的不变量：累计投入的最大值至少被两个座位持有，或为 0。
    /// </summary>
    private void CloseRound()
    {
        var round = _round ?? throw new InvalidOperationException("没有进行中的下注轮。");
        foreach (int seat in ParticipantOrder)
        {
            if (_folded[seat])
            {
                continue;
            }
            _contributions[seat] += round.CommittedAt(seat);
            _stacks[seat] = round.StackAt(seat);
            _folded[seat] = round.HasFolded(seat);
        }
        _round = null;

        RefundUnmatchedExcess();
        AfterRound();
    }

    private void RefundUnmatchedExcess()
    {
        int max = _contributions.Max();
        if (max == 0 || _contributions.Count(amount => amount == max) >= 2)
        {
            return;
        }

        int leader = Array.IndexOf(_contributions, max);
        int second = _contributions.Where((_, seat) => seat != leader).DefaultIfEmpty(0).Max();
        // 弃牌者放弃了争夺，不可能独自持有最高投入：最后加注的人一定还在局里。
        if (_folded[leader])
        {
            throw new InvalidOperationException("弃牌者不可能持有未被匹配的超额投入。");
        }

        int refund = max - second;
        _contributions[leader] -= refund;
        _stacks[leader] += refund;
    }

    /// <summary>一轮下注结束（或被跳过）后推进轮次。只剩一名未弃牌者时直接到摊牌。</summary>
    private void AfterRound()
    {
        if (CountNotFolded() <= 1)
        {
            CurrentStage = HandStage.Showdown;
            return;
        }

        CurrentStage = CurrentStage switch
        {
            HandStage.FirstBet => HandStage.SecondBet,
            HandStage.SecondBet => HandStage.Reroute,
            HandStage.ThirdBet => HandStage.Showdown,
            _ => throw new InvalidOperationException($"轮次 {CurrentStage} 之后不应结束下注。")
        };
    }

    /// <summary>从牌库顶揭下一张公共牌。三张揭完后不能再揭。</summary>
    private void RevealCommunityCard()
    {
        if (_communityCards.Count >= CommunityCardCount)
        {
            // 可信流程调用错了，属于服务端缺陷，不是玩家能触发的业务拒绝。
            throw new InvalidOperationException($"本局 {CommunityCardCount} 张公共牌已经全部揭开。");
        }

        var revealed = _deck.DrawNext()
            ?? throw new InvalidOperationException("完整标准牌库应足够揭完所有公共牌。");
        _communityCards.Add(revealed);
    }

    private int CountNotFolded()
    {
        int count = 0;
        foreach (int seat in ParticipantOrder)
        {
            if (!_folded[seat] && !(_round?.HasFolded(seat) ?? false))
            {
                count++;
            }
        }
        return count;
    }

    // 玩家可能在两轮之间或结算时发来下注：这是正常的状态竞争，以业务拒绝回应。
    private BettingRound RequireRound() =>
        _round ?? throw new RuleRejectedException(RejectionReason.StageNotAllowed, "当前没有进行中的下注。");

    private void RequireStage(HandStage expected, string message)
    {
        if (CurrentStage != expected)
        {
            throw new InvalidOperationException(message);
        }
    }

    private void RequireNotComplete()
    {
        if (IsComplete)
        {
            throw new InvalidOperationException("本局已经结束。");
        }
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
