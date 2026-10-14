using DarkTide.Rules;

namespace DarkTide.Room;

/// <summary>
/// 本地房间原型：绑定身份与座位，按身份提供底牌视图。
/// 只能顺序调用，没有锁；并发场景通过 <see cref="RoomExecutor"/> 使用。不执行登录认证。
/// 修改只经由 执行(命令)；具体的 入座 等方法为 internal，供命令分派与测试使用。
/// </summary>
public sealed class Room
{
    private readonly Guid?[] _playerBySeat;
    private readonly CommandReceiptStore _processedCommands;
    // 整场按座位号保存筹码。目前只能打一局，且发牌后不允许换人，所以座位筹码不会被别人继承。
    private readonly Match _match;
    private Hand? _currentHand;

    public Guid RoomId { get; }
    public int SeatCount => _playerBySeat.Length;
    public int SeatedCount => _playerBySeat.Count(player => player.HasValue);

    /// <summary>
    /// 显式记录阶段，而不是用“局面是否为空”推断：以后整场结束、单局结算等阶段
    /// 无法从某个字段是否为空区分出来。不变量：仅在 已发底牌 阶段 当前单局 非空。
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
        _match = new Match(seatCount);
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

    /// <summary>可信流程提供牌序与参与顺序；目前只能发一次底牌，尚无整场与多局流程。</summary>
    internal void Deal(IReadOnlyList<Card> drawOrder, IReadOnlyList<int> participantOrder)
    {
        RequireStage(RoomStage.WaitingForSeats, "已发底牌，不允许重复发牌。");
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

        // 单局继续检查名单人数、重复座位与牌序。
        // 先在局部变量里构造；成功后才一起修改字段，失败时房间保持原状。
        // 底注取第 1 局（局号在 Hand 构造成功之后才推进，构造失败时整场不变）。
        var newHand = new Hand(SeatCount, drawOrder, order, Match.EarlyAnte, _match.ChipsSnapshot());
        _match.StartNextHand();
        _currentHand = newHand;
        Stage = RoomStage.CardsDealt;
        StateVersion++;
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
        return new RoomOverview(RoomId, Stage, StateVersion, Array.AsReadOnly(seatOccupied), participantOrder);
    }

    /// <summary>玩家编号必须来自已认证身份；不能直接使用客户端自报编号。</summary>
    public PlayerHandView CreatePlayerView(Guid playerId)
    {
        int seat = FindSeat(playerId)
            ?? throw new RuleRejectedException(RejectionReason.NotSeated, "该玩家未在本房间入座，无权查看底牌视图。");
        RequireStage(RoomStage.CardsDealt, "尚未发底牌。");

        // 阶段检查已保证局面存在；若为空说明不变量被破坏，应作为故障暴露，而不是当成业务拒绝。
        var hand = _currentHand ?? throw new InvalidOperationException("已发底牌阶段缺少单局。");
        return hand.CreatePlayerView(seat);
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
