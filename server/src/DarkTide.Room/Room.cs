using System.Security.Cryptography;
using DarkTide.Rules;

namespace DarkTide.Room;

/// <summary>房间正在等待的下一个决定。执行器据此决定计时的对象与期限。</summary>
internal enum PendingKind
{
    /// <summary>不需要计时：等待入座、整场结束。</summary>
    None,
    /// <summary>某座位的下注轮到他。</summary>
    Bet,
    /// <summary>某座位的改航轮到他。</summary>
    Reroute,
    /// <summary>上一局已结算，等待发下一局。</summary>
    NextHand
}

/// <param name="Version">进入这个等待时的状态版本；版本一变，就是另一个等待（哪怕还是同一个人）。</param>
internal readonly record struct PendingDecision(PendingKind Kind, int Seat, long Version);

/// <summary>
/// 房间：绑定身份与座位，持有整场与当前一局，按身份提供底牌视图。
/// 流程：等待入座 →（发牌）一局进行中 →（最后一个动作结束本局，自动结算）已结算 →（发牌）下一局 … → 整场结束。
/// 发牌、牌序与参与顺序来自可信流程；下注与改航来自玩家，房间按身份查出座位，客户端不能代别人操作。
/// 只能顺序调用，没有锁；并发场景通过 <see cref="RoomExecutor"/> 使用。不执行登录认证。
/// 修改只经由 <see cref="Execute"/>；具体的入座等方法为 internal，供命令分派与测试使用。
/// 相同的命令序列总是得到相同的状态与结果：持久化靠按顺序重放命令来重建房间，不另存状态快照。
/// </summary>
public sealed class Room
{
    private readonly Guid?[] _playerBySeat;
    private readonly CommandReceiptStore _processedCommands;
    // 整场按座位号保存筹码。首局发牌后不允许换人，所以座位筹码不会被别人继承。
    // 首局发牌时才创建：首局名单里的座位各带初始筹码，其余（空座、入座但不参赛）为 0。
    // 否则不参赛的座位也算“有筹码”，整场永远无法提前结束。
    private Match? _match;
    private Hand? _currentHand;

    public Guid RoomId { get; }
    public int SeatCount => _playerBySeat.Length;
    public int SeatedCount => _playerBySeat.Count(player => player.HasValue);

    /// <summary>
    /// 显式记录阶段，而不是用“局面是否为空”推断：已结算与进行中都有单局，无法从字段是否为空区分。
    /// 不变量：等待入座时没有单局；其余阶段单局非空（已结算时保留刚结算的一局供查看）。
    /// </summary>
    public RoomStage Stage { get; private set; } = RoomStage.WaitingForSeats;

    /// <summary>
    /// 每次成功修改状态后加一，查询不改变它。客户端带上自己看到的版本提交操作，
    /// 房间据此识别“基于过时画面的请求”；持久化时也用它做乐观并发检查。
    /// </summary>
    public long StateVersion { get; private set; }

    /// <param name="receiptCacheCapacity">内存里保留多少条最近命令的回执；通常取自 <see cref="RoomOptions.ReceiptCacheCapacity"/>。</param>
    public Room(Guid roomId, int seatCount, int receiptCacheCapacity = CommandReceiptStore.DefaultCapacity)
    {
        if (roomId == Guid.Empty)
        {
            throw new ArgumentException("房间编号不能为空编号。", nameof(roomId));
        }
        if (seatCount < TableLimits.MinSeats || seatCount > TableLimits.MaxSeats)
        {
            throw new ArgumentOutOfRangeException(nameof(seatCount), seatCount, $"牌桌座位总数必须在 {TableLimits.MinSeats}～{TableLimits.MaxSeats} 之间。");
        }

        this.RoomId = roomId;
        _playerBySeat = new Guid?[seatCount];
        _processedCommands = new CommandReceiptStore(receiptCacheCapacity);
    }

    /// <summary>
    /// 修改房间的唯一公开入口。顺序固定：先查原结果，再查版本，最后执行规则。
    /// 先查原结果是因为：命令成功后版本已经前进，重发若先比版本，会把成功误报为“版本不符”。
    /// </summary>
    public CommandReceipt Execute(RoomCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_processedCommands.TryGet(command.CommandId, out var existing))
        {
            // 同编号同内容：返回原结果（成功回执或原拒绝），不再执行。
            // 同编号不同内容：拒绝，但不记录，以免覆盖原命令的结果。
            return existing.command == command
                ? existing.ReplayResult()
                : throw new RuleRejectedException(RejectionReason.CommandIdConflict, "该命令编号已用于另一项操作。");
        }

        try
        {
            if (command.ExpectedVersion is long expected && expected != StateVersion)
            {
                throw new RuleRejectedException(RejectionReason.VersionMismatch, $"房间状态已变化（当前版本 {StateVersion}），请刷新后重新操作。");
            }

            // 命令种类是封闭集合，用 switch 穷举；新增命令却忘了处理时，会落到最后一个分支并作为故障暴露。
            switch (command)
            {
                case TakeSeatCommand takeSeatRequest:
                    TakeSeat(takeSeatRequest.PlayerId, takeSeatRequest.Seat);
                    break;
                case LeaveSeatCommand leaveSeatRequest:
                    LeaveSeat(leaveSeatRequest.PlayerId);
                    break;
                case DealCommand dealRequest:
                    Deal(dealRequest.DrawOrder, dealRequest.ParticipantOrder);
                    break;
                case BetCommand betRequest:
                    Bet(betRequest.PlayerId, betRequest.Action, betRequest.RaiseTarget);
                    break;
                case RerouteCommand rerouteRequest:
                    Reroute(rerouteRequest.PlayerId, rerouteRequest.Reroute);
                    break;
                case TimeoutCommand:
                    TimeOut();
                    break;
                default:
                    throw new InvalidOperationException($"未处理的命令类型：{command.GetType().Name}。");
            }
        }
        catch (RuleRejectedException Reject)
        {
            // 拒绝也是确定的结果：同一命令重发时应得到同样的拒绝，而不是在状态变化后意外成功。
            _processedCommands.RecordRejection(command, Reject);
            throw;
        }

        var receipt = new CommandReceipt(command.CommandId, StateVersion);
        _processedCommands.RecordSuccess(command, receipt);
        return receipt;
    }

    /// <summary>内存回执缓存里是否有这个命令编号。命中时 <see cref="Execute"/> 只回放原结果，不改变状态。</summary>
    internal bool HasCachedResult(Guid commandId) => _processedCommands.TryGet(commandId, out _);

    /// <summary>发牌前绑定玩家与座位；同一玩家在本房间只能占一个座位。</summary>
    internal void TakeSeat(Guid playerId, int seat)
    {
        RequirePlayerId(playerId);
        RequireStage(RoomStage.WaitingForSeats, "已发底牌，不允许更换成员。");
        if (seat < 0 || seat >= SeatCount)
        {
            throw new RuleRejectedException(RejectionReason.SeatOutOfRange, "座位编号必须在当前牌桌范围内。");
        }
        if (FindSeat(playerId) is not null)
        {
            throw new RuleRejectedException(RejectionReason.AlreadySeated, "该玩家已经在本房间入座。");
        }
        if (_playerBySeat[seat] is not null)
        {
            throw new RuleRejectedException(RejectionReason.SeatTaken, "该座位已有玩家。");
        }

        _playerBySeat[seat] = playerId;
        StateVersion++;
    }

    /// <summary>仅在发牌前释放自己的座位。断线不应调用此方法。</summary>
    internal void LeaveSeat(Guid playerId)
    {
        RequireStage(RoomStage.WaitingForSeats, "已发底牌，不允许更换成员。");
        int seat = FindSeat(playerId)
            ?? throw new RuleRejectedException(RejectionReason.NotSeated, "该玩家未在本房间入座。");

        _playerBySeat[seat] = null;
        StateVersion++;
    }

    public int? FindSeat(Guid playerId)
    {
        RequirePlayerId(playerId);
        for (int seat = 0; seat < SeatCount; seat++)
        {
            if (_playerBySeat[seat] == playerId)
            {
                return seat;
            }
        }
        return null;
    }

    /// <summary>
    /// 可信流程提供牌序与参与顺序，开始下一局。首局在等待入座时发，之后在上一局结算后发。
    /// 首局名单决定谁参加整场：入座但不在名单里的人不参赛（保持“已入座、本局未参与”）；
    /// 名单第一位是首局起始位（由可信流程随机选），其余按顺时针（座位号递增、绕回 0）排列。
    /// 之后每局名单由规则唯一确定（<see cref="ExpectedNextLineup"/>）：起始标记顺时针移到下一位仍有筹码的玩家（规则第 1 节）。
    /// 构造单局后立即收底注，并揭开第一张公共牌开始一注，所以发牌之后就轮到第一位玩家行动。
    /// </summary>
    internal void Deal(IReadOnlyList<Card> drawOrder, IReadOnlyList<int> participantOrder)
    {
        if (Stage is not (RoomStage.WaitingForSeats or RoomStage.HandSettled))
        {
            throw new RuleRejectedException(RejectionReason.StageNotAllowed,
                Stage == RoomStage.MatchFinished ? "整场已经结束。" : "本局尚未结束，不能发下一局。");
        }
        ArgumentNullException.ThrowIfNull(drawOrder);
        ArgumentNullException.ThrowIfNull(participantOrder);
        var order = participantOrder.ToArray();

        foreach (int seat in order)
        {
            if (seat < 0 || seat >= SeatCount)
            {
                throw new ArgumentException("参与座位编号超出牌桌范围。", nameof(participantOrder));
            }
            if (_playerBySeat[seat] is null)
            {
                // 决定名单后、入队执行前可能有人离座，属于正常的状态竞争。
                throw new RuleRejectedException(RejectionReason.EmptySeatInLineup, "参与发牌的座位必须已有玩家。");
            }
        }

        // 先在局部变量里构造，全部成功后才写回字段。
        Match match;
        if (_match is null)
        {
            if (!IsClockwiseFromFirst(order))
            {
                throw new ArgumentException("首局名单必须从起始位开始按顺时针排列。", nameof(participantOrder));
            }
            // 名单的人数与重复由单局检查；这里只需要一份按座位的初始筹码。重复座位会让单局构造失败，整场随之丢弃。
            var initial = new int[SeatCount];
            foreach (int seat in order)
            {
                initial[seat] = Match.StandardInitialChips;
            }
            match = new Match(initial);
        }
        else
        {
            match = _match;
            if (!order.SequenceEqual(ExpectedNextLineup()))
            {
                // 名单由可信流程按规则生成；不一致说明流程算错了，不是玩家能触发的情况。
                throw new ArgumentException("名单必须是仍有筹码的座位，从顺移后的起始位开始按顺时针排列。", nameof(participantOrder));
            }
        }

        // 单局继续检查名单人数、重复座位与牌序；构造失败时整场与房间都不变。
        var newHand = new Hand(SeatCount, drawOrder, order, match.NextHandAnte().Amount, match.ChipsSnapshot());
        match.StartNextHand();
        newHand.CollectAntes();
        _match = match;
        _currentHand = newHand;
        Stage = RoomStage.CardsDealt;
        AdvanceUntilInput();
        StateVersion++;
    }

    /// <summary>轮到本人下注时的动作。不是本人、不在下注中、金额不合规则，都以业务拒绝回应且不改动状态。</summary>
    internal void Bet(Guid playerId, BettingAction action, int raiseTarget)
    {
        var (hand, seat) = RequirePlayerInHand(playerId);
        hand.Act(seat, action, raiseTarget);
        AdvanceUntilInput();
        StateVersion++;
    }

    /// <summary>轮到本人改航时：换第二张或放弃。</summary>
    internal void Reroute(Guid playerId, bool reroute)
    {
        var (hand, seat) = RequirePlayerInHand(playerId);
        if (reroute)
        {
            hand.Reroute(seat);
        }
        else
        {
            hand.PassReroute(seat);
        }
        AdvanceUntilInput();
        StateVersion++;
    }

    /// <summary>超时：能过牌就过牌，否则弃牌；改航窗口里放弃。只在一局进行中有意义。</summary>
    internal void TimeOut()
    {
        RequireStage(RoomStage.CardsDealt, "当前没有进行中的一局。");
        var hand = _currentHand ?? throw new InvalidOperationException("一局进行中却缺少单局。");
        if (hand.CurrentStage == HandStage.Reroute)
        {
            hand.PassReroute(hand.RerouteActingSeat);
        }
        else
        {
            // 有待跟注额时不能过牌，弃牌一定合法（规则：无待跟注额时才不能弃牌）。
            hand.Act(hand.ActingSeat, hand.GetLegalActions().CanCheck ? BettingAction.Check : BettingAction.Fold);
        }
        AdvanceUntilInput();
        StateVersion++;
    }

    /// <summary>房间正在等待的决定；执行器据此计时。</summary>
    internal PendingDecision Pending => Stage switch
    {
        RoomStage.CardsDealt when _currentHand!.CurrentStage == HandStage.Reroute =>
            new PendingDecision(PendingKind.Reroute, _currentHand.RerouteActingSeat, StateVersion),
        RoomStage.CardsDealt => new PendingDecision(PendingKind.Bet, _currentHand!.ActingSeat, StateVersion),
        RoomStage.HandSettled => new PendingDecision(PendingKind.NextHand, -1, StateVersion),
        _ => new PendingDecision(PendingKind.None, -1, StateVersion),
    };

    /// <summary>
    /// 下一局的名单（规则第 1 节）：起始标记从上一局起始位顺时针移到下一位仍有筹码的玩家，
    /// 名单是仍有筹码的座位，从新起始位开始按顺时针排列。座位号递增即顺时针。
    /// </summary>
    internal int[] ExpectedNextLineup()
    {
        RequireStage(RoomStage.HandSettled, "上一局尚未结算。");
        var match = _match ?? throw new InvalidOperationException("已结算却没有整场。");
        var chips = match.ChipsSnapshot();
        int previousStart = (_currentHand ?? throw new InvalidOperationException("已结算却没有单局。")).ParticipantOrder[0];
        // 从上一局起始位的下一位开始找；绕满一圈仍可能回到他自己（只剩他一人时整场已结束，不会走到这里）。
        int start = Enumerable.Range(1, SeatCount).Select(step => (previousStart + step) % SeatCount).First(seat => chips[seat] > 0);
        return Clockwise(start).Where(seat => chips[seat] > 0).ToArray();
    }

    /// <summary>
    /// 开始整场的发牌命令：入座者全部参赛，安全随机数洗牌并随机选首局起始位（规则第 1 节）。
    /// 只生成命令、不改状态；随机结果写进命令，所以重放时不再生成随机数。
    /// </summary>
    /// <param name="expectedVersion">发起者看到的版本：房主按下“开始”时看到的入座名单必须仍是当前的，否则拒绝（有人刚离座或入座）。</param>
    internal DealCommand CreateFirstDeal(long? expectedVersion = null)
    {
        if (expectedVersion is long expected && expected != StateVersion)
        {
            throw new RuleRejectedException(RejectionReason.VersionMismatch, $"提交时看到的版本是 {expected}，当前是 {StateVersion}；请刷新后重新决定。");
        }
        RequireStage(RoomStage.WaitingForSeats, "整场已经开始。");
        var seated = Enumerable.Range(0, SeatCount).Where(seat => _playerBySeat[seat] is not null).ToArray();
        if (seated.Length < TableLimits.MinParticipants)
        {
            throw new RuleRejectedException(RejectionReason.NotEnoughPlayers, $"至少需要 {TableLimits.MinParticipants} 人入座才能开始。");
        }
        int start = seated[RandomNumberGenerator.GetInt32(seated.Length)];
        return new DealCommand(Guid.NewGuid(), Shuffler.ShuffleStandard(), FirstHandLineup(start), StateVersion);
    }

    /// <summary>下一局的发牌命令：新洗一副牌，名单按起始标记顺移。只生成命令、不改状态。</summary>
    internal DealCommand CreateNextDeal() =>
        new(Guid.NewGuid(), Shuffler.ShuffleStandard(), ExpectedNextLineup(), StateVersion);

    /// <summary>首局名单：从给定起始位开始，按顺时针列出所有入座的人。</summary>
    internal int[] FirstHandLineup(int startSeat)
    {
        RequireStage(RoomStage.WaitingForSeats, "整场已经开始。");
        if (_playerBySeat[startSeat] is null)
        {
            throw new ArgumentException("起始位必须有人入座。", nameof(startSeat));
        }
        return Clockwise(startSeat).Where(seat => _playerBySeat[seat] is not null).ToArray();
    }

    private IEnumerable<int> Clockwise(int start) => Enumerable.Range(0, SeatCount).Select(offset => (start + offset) % SeatCount);

    // 每个座位相对第一位的顺时针距离严格递增，即从第一位开始绕一圈、不回头。
    private bool IsClockwiseFromFirst(int[] order)
    {
        for (int i = 1; i < order.Length; i++)
        {
            int previous = (order[i - 1] - order[0] + SeatCount) % SeatCount;
            int current = (order[i] - order[0] + SeatCount) % SeatCount;
            if (current <= previous)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 推进到下一个需要玩家输入的地方：开始下一轮下注（不足两人能行动时规则自动跳过并揭牌），
    /// 或停在改航窗口；本局结束就结算并写回整场。这一步不需要任何人决定，所以与触发它的动作在同一条命令里完成，
    /// 不会出现“该开下一轮了但还没人开”的中间状态。
    /// </summary>
    private void AdvanceUntilInput()
    {
        var hand = _currentHand ?? throw new InvalidOperationException("没有进行中的单局。");
        var match = _match ?? throw new InvalidOperationException("没有整场。");
        while (!hand.IsComplete && !hand.IsBetting && hand.CurrentStage != HandStage.Reroute)
        {
            hand.StartBettingRound();
        }
        if (!hand.IsComplete)
        {
            return;
        }

        match.SettleHand(hand.Settle().FinalStacks);
        Stage = match.AllHandsPlayed || match.IsDecided ? RoomStage.MatchFinished : RoomStage.HandSettled;
    }

    private (Hand Hand, int Seat) RequirePlayerInHand(Guid playerId)
    {
        RequirePlayerId(playerId);
        int seat = FindSeat(playerId)
            ?? throw new RuleRejectedException(RejectionReason.NotSeated, "该玩家未在本房间入座。");
        RequireStage(RoomStage.CardsDealt, "当前没有进行中的一局。");
        var hand = _currentHand ?? throw new InvalidOperationException("一局进行中却缺少单局。");
        return (hand, seat);
    }

    /// <summary>每次生成新快照；之后房间再变化，已交出的快照不受影响。</summary>
    public RoomOverview CreateOverview()
    {
        var seatOccupied = new bool[SeatCount];
        for (int seat = 0; seat < SeatCount; seat++)
        {
            seatOccupied[seat] = _playerBySeat[seat].HasValue;
        }

        // 局面里的参与顺序本身不可变，可以直接共享，不必复制。
        IReadOnlyList<int> participantOrder = _currentHand?.ParticipantOrder ?? Array.Empty<int>();
        // 首局发牌前还没有整场，也还不知道谁参赛：全部显示 0。
        var matchChips = _match?.ChipsSnapshot() ?? new int[SeatCount];
        return new RoomOverview(RoomId, Stage, StateVersion, Array.AsReadOnly(seatOccupied), participantOrder,
            Array.AsReadOnly(matchChips), _match?.CurrentHandNumber ?? 0, _currentHand is null ? null : DescribeHand(_currentHand));
    }

    // 只放公开信息：金额、弃牌、公共牌、弃置牌、报价、合法动作；底牌只在摊牌结算后放未弃牌者的。
    private HandOverview DescribeHand(Hand hand)
    {
        int seats = hand.SeatCount;
        var stacks = new int[seats];
        var committed = new int[seats];
        var folded = new bool[seats];
        for (int seat = 0; seat < seats; seat++)
        {
            stacks[seat] = hand.StackAt(seat);
            committed[seat] = hand.CommittedThisRound(seat);
            folded[seat] = hand.HasFolded(seat);
        }

        SettlementOverview? settlement = null;
        if (hand.IsSettled)
        {
            var result = hand.Settle();
            var shown = new Dictionary<int, IReadOnlyList<Card>>();
            if (result.Showdown)
            {
                foreach (int seat in hand.ParticipantOrder.Where(seat => !hand.HasFolded(seat)))
                {
                    shown[seat] = hand.CreatePlayerView(seat).HoleCards;
                }
            }
            settlement = new SettlementOverview(result.FinalStacks, result.Pots, result.BonusPots, result.Showdown, shown);
        }

        return new HandOverview(
            _match?.CurrentHandNumber ?? 0,
            hand.Ante,
            hand.CurrentStage,
            hand.CommunityCards.ToArray(),
            hand.DiscardedCards.ToArray(),
            Array.AsReadOnly(stacks),
            hand.Contributions.ToArray(),
            Array.AsReadOnly(committed),
            hand.RerouteFees.ToArray(),
            Array.AsReadOnly(folded),
            hand.ActingSeat,
            hand.IsBetting ? hand.GetLegalActions() : null,
            hand.RerouteQuotes,
            hand.RerouteActingSeat,
            settlement);
    }

    /// <summary>玩家编号必须来自已认证身份；不能直接使用客户端自报编号。</summary>
    public PlayerHandView CreatePlayerView(Guid playerId)
    {
        int seat = FindSeat(playerId)
            ?? throw new RuleRejectedException(RejectionReason.NotSeated, "该玩家未在本房间入座，无权查看底牌视图。");
        if (Stage == RoomStage.WaitingForSeats)
        {
            throw new RuleRejectedException(RejectionReason.StageNotAllowed, "尚未发底牌。");
        }

        // 阶段检查已保证局面存在；若为空说明不变量被破坏，应作为故障暴露，而不是当成业务拒绝。
        // 结算后仍可查看自己刚才的底牌，直到下一局发牌。
        var hand = _currentHand ?? throw new InvalidOperationException("已发底牌阶段缺少单局。");
        return hand.CreatePlayerView(seat);
    }

    /// <summary>每个座位上的玩家（空座为空）。返回副本。</summary>
    internal Guid?[] SeatPlayersSnapshot() => (Guid?[])_playerBySeat.Clone();

    /// <summary>每个座位的底牌（本局未参与的座位为空列表）；还没发过牌时为空。只供发布快照使用，不对外。</summary>
    internal IReadOnlyList<Card>[]? HoleCardsBySeat()
    {
        if (Stage == RoomStage.WaitingForSeats)
        {
            return null;
        }
        var hand = _currentHand ?? throw new InvalidOperationException("已发底牌阶段缺少单局。");
        return Enumerable.Range(0, SeatCount).Select(seat => hand.CreatePlayerView(seat).HoleCards).ToArray();
    }

    private void RequireStage(RoomStage expectedStage, string message)
    {
        if (Stage != expectedStage)
        {
            throw new RuleRejectedException(RejectionReason.StageNotAllowed, message);
        }
    }

    private static void RequirePlayerId(Guid playerId)
    {
        if (playerId == Guid.Empty)
        {
            throw new ArgumentException("玩家编号不能为空编号。", nameof(playerId));
        }
    }
}
