using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using DarkTide.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DarkTide.Room;

/// <summary>请求在消费循环里的处理结果。</summary>
internal enum RequestOutcome
{
    Applied,
    Rejected,
    Faulted
}

/// <summary>
/// 多个调用方可以提交请求，同一房间只有一个消费循环执行请求。
/// 身份认证尚未接入。
///
/// 持久化：配置了 <see cref="IRoomJournal"/> 时，每条命令的最终结果写入日志后才完成调用方的任务，
/// 所以任务成功＝已持久提交。没有日志时（开发与单元测试）任务成功只代表内存状态已改变。
/// 写入失败时结果未知，房间停止，调用方收到 <see cref="RoomPersistenceException"/>。
///
/// 运维可见性：日志与指标都只输出标识符、枚举与数量。**绝不输出牌**
/// （牌序、底牌、公共牌），一副牌序泄漏就等于作弊；相关类型已覆盖 ToString 做脱敏。
///
/// 停止：<see cref="StopAsync"/> 最多等待 <see cref="RoomOptions.ShutdownTimeout"/>。
/// 超时后放弃仍在排队的请求（它们没有执行，调用方可以安全重试）；正在执行的那一条照常做完。
///
/// 计时：每处理完一条命令，按房间当前等待的决定重设唯一的计时器（行动时限、局间间隔）。
/// 计时器到期只往队列里放一条内部请求；轮到它时重新读当前状态：期限确实已过、等待没变，才生成超时或发牌命令，
/// 经过与玩家命令相同的执行与写日志路径。玩家在期限前进入队列的动作排在它前面，先执行、等待改变，
/// 内部请求随即无事可做——按“谁先进入队列”裁决，不比墙上时钟（设计文档“请求防重与流量控制”第 7 节）。
///
/// 活性：任何时刻队列里最多一条计时请求，并为它在队列里预留一个位置，所以玩家刷满队列也挤不掉它，它也不会被丢弃。
/// 计时器比期限略早触发（系统计时器的精度）时，按剩余时间重新计时，不会因此停住。
/// 自己生成的超时被规则拒绝，说明房间逻辑有误：按故障停止房间（可见、可恢复），而不是静默停在这一步。
/// </summary>
public sealed class RoomExecutor : IAsyncDisposable
{
    // 查询只读、可以随时重发，额度小；与命令分开计数，频繁刷新视图挤不掉真正的操作。
    // 计时请求是第三类：同一时刻最多排一条（见 _clockQueued），队列里为它预留一个位置。
    private const int ClockSlots = 1;

    private readonly Room _room;
    private readonly Channel<QueuedRequest> _queue;
    private readonly Task _processingTask;
    private readonly int _commandCapacity;
    private readonly int _queryCapacity;
    private readonly TimeSpan _shutdownTimeout;
    private readonly ILogger _logger;
    private readonly IRoomJournal? _journal;
    private readonly TimeSpan _journalTimeout;
    // 下一条日志记录的序号；只在消费循环里读写。
    private long _nextSequence;
    // 已写入日志的全部命令编号；只在消费循环里读写。内存回执缓存只留最近的结果，
    // 这个集合让“缓存未命中”能区分两种情况：没见过的编号一定是新命令，不必查库；
    // 见过的才是重发，到日志里取原结果。一场约数百条，每条约 40 字节。
    private readonly HashSet<Guid> _journaledCommandIds;

    // 每名玩家最多一个尚未得到结果的命令（设计文档“请求防重与流量控制”第 6 节）。
    // 值是那条命令及其任务：同一编号同一内容的重发直接共享原任务，不再占队列位置，也不会被当成第二个意图拒绝。
    // 名额在命令得到确定结果（成功、规则拒绝、故障或被放弃）时才释放，与调用方是否还在等无关。
    private readonly ConcurrentDictionary<Guid, PendingCommand> _pendingByPlayer = new();
    private readonly PlayerRateLimiter _commandLimiter;
    private readonly PlayerRateLimiter _queryLimiter;

    private sealed record PendingCommand(RoomCommand Command, Task<CommandReceipt> Result);
    private int _waitingCommands;
    private int _waitingQueries;
    // 队列里是否已有一条计时请求。计时器线程置 1，消费循环取到它时清 0。
    private int _clockQueued;

    private readonly TimeProvider _time;
    private readonly TimeSpan _turnTimeout;
    private readonly TimeSpan _nextHandDelay;
    // 只在消费循环里读写：当前计时器、它对应的等待和期限。
    // 期限用单调时钟判断（不受系统改时间影响）；UTC 时间只给客户端画倒计时。
    private ITimer? _timer;
    private PendingDecision _timedFor;
    private long _deadlineTimestamp;
    private DateTimeOffset? _deadline;
    private readonly TaskCompletionSource _matchFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 最新的已发布状态。消费循环每次状态（或截止时间）变化后生成一份新的不可变快照并替换它，再唤醒等待者。
    // 订阅者读这个字段，不进房间队列：8 人桌加旁观者同时刷新也不会占满查询名额。
    // 房间从不等待订阅者，也不为每个订阅者做任何事；推送成本（序列化、写 socket）全在订阅者自己的任务里。
    private PublishedRoomState _published;
    private int _stopping;
    // 超时放弃后置位：消费循环看到它就不再取新请求。
    private int _abandoned;
    // 谁先抢到，谁负责把队列里剩下的请求全部拒绝；保证每个请求要么被执行、要么恰好被拒绝一次。
    private int _drainClaimed;
    private int _droppedRequests;
    // 消费循环先写入故障，再置位停止接收；提交方看到停止后再读故障，就能读到写入的值。
    private Exception? _fault;

    /// <summary>创建空房间并开始处理。</summary>
    public RoomExecutor(Guid roomId, int seatCount, RoomOptions? options = null, ILogger? logger = null, TimeProvider? time = null)
        : this(CreateRoom(roomId, seatCount, options), options, Task.CompletedTask, logger, time: time)
    {
    }

    /// <summary>
    /// 接管一个已有房间，例如以后从持久状态重建的房间。
    /// 调用后由执行器独占该对象，调用方不得再直接调用它的方法，否则会绕过队列。
    /// 回执缓存容量随房间对象创建时确定，这里的 options 不再改变它。
    /// </summary>
    public RoomExecutor(Room initialRoom, RoomOptions? options = null, ILogger? logger = null)
        : this(initialRoom, options, Task.CompletedTask, logger)
    {
    }

    // 测试可以暂缓消费，以确定性验证满队列和关闭时的行为。
    // 正常构造传入已完成任务，立即开始等待请求。
    internal RoomExecutor(
        Room initialRoom,
        RoomOptions? options,
        Task startSignal,
        ILogger? logger = null,
        IRoomJournal? journal = null,
        long nextSequence = 1,
        IEnumerable<Guid>? journaledCommandIds = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(initialRoom);
        ArgumentNullException.ThrowIfNull(startSignal);
        options ??= RoomOptions.Default;
        options.EnsureValid();

        _room = initialRoom;
        // 构造时复制取值：之后配置对象换了，也不会让正在运行的房间半途改变行为。
        _commandCapacity = options.CommandQueueCapacity;
        _queryCapacity = options.QueryQueueCapacity;
        _shutdownTimeout = options.ShutdownTimeout;
        _journal = journal;
        _journalTimeout = options.JournalTimeout;
        _time = time ?? TimeProvider.System;
        _turnTimeout = options.TurnTimeout;
        _nextHandDelay = options.NextHandDelay;
        _commandLimiter = new PlayerRateLimiter(options.PlayerCommandsPerSecond, options.PlayerCommandBurst, _time);
        _queryLimiter = new PlayerRateLimiter(options.PlayerQueriesPerSecond, options.PlayerQueryBurst, _time);
        ArgumentOutOfRangeException.ThrowIfLessThan(nextSequence, 1);
        _nextSequence = nextSequence;
        _journaledCommandIds = journaledCommandIds is null ? [] : [.. journaledCommandIds];
        // 没有传入日志器时用 NullLogger：调用方不必为了构造一个房间而准备日志基础设施。
        _logger = logger ?? NullLogger.Instance;
        // 命令和查询共用一条队列，才能保证“先提交的修改先生效，后提交的查询能看到它”。
        // 名额由两个计数器分别控制；队列容量取两者之和，只作为最后一道保护。
        // SingleReader = false：超时放弃时，StopAsync 要在消费循环之外取走剩余请求。
        // 消费循环卡住时，这是让排队请求不永远挂起的唯一办法。
        _queue = Channel.CreateBounded<QueuedRequest>(new BoundedChannelOptions(_commandCapacity + _queryCapacity + ClockSlots)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        // 构造时先发布一份：循环开始前（测试暂缓消费时）订阅者也有东西可读。
        _published = PublishedRoomState.Capture(_room, null);
        _processingTask = ProcessQueueAsync(startSignal);
    }

    /// <summary>
    /// 创建一个持久房间：先在日志中登记，成功后才开始处理。房间编号已存在时抛 <see cref="RoomAlreadyExistsException"/>。
    /// </summary>
    public static async Task<RoomExecutor> CreatePersistentAsync(
        Guid roomId,
        int seatCount,
        IRoomJournal journal,
        RoomOptions? options = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default,
        TimeProvider? time = null,
        Guid? createdBy = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        // 先在内存里构造：参数非法时不在日志里留下无法加载的房间。
        var room = CreateRoom(roomId, seatCount, options);
        await WithTimeout(
            token => journal.CreateRoomAsync(roomId, seatCount, createdBy, token),
            (options ?? RoomOptions.Default).JournalTimeout,
            cancellationToken).ConfigureAwait(false);
        return new RoomExecutor(room, options, Task.CompletedTask, logger, journal, nextSequence: 1, time: time) { CreatedBy = createdBy };
    }

    /// <summary>
    /// 从日志恢复房间：按顺序重放全部命令，并逐条核对结果与记录一致。
    /// 不一致说明规则代码与日志不匹配（例如部署了改变行为的版本），抛 <see cref="JournalReplayMismatchException"/>，不加载。
    /// 房间不存在时抛 <see cref="KeyNotFoundException"/>。
    /// </summary>
    public static async Task<RoomExecutor> RestoreAsync(
        Guid roomId,
        IRoomJournal journal,
        RoomOptions? options = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var stored = await journal.LoadAsync(roomId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"日志中没有房间 {roomId}。");
        // 先核对版本再重放：版本不同时，重放可能“碰巧”不报错却得到不同的状态，逐条核对结果也不一定能发现。
        if (!RulesVersion.CanReplay(stored.RulesVersion))
        {
            throw new RulesVersionMismatchException(roomId, stored.RulesVersion);
        }
        var room = CreateRoom(roomId, stored.SeatCount, options);

        long expected = 1;
        foreach (var entry in stored.Entries)
        {
            if (entry.Sequence != expected)
            {
                throw new JournalReplayMismatchException(roomId, entry.Sequence, $"序号不连续，应为 {expected}");
            }
            Replay(room, entry);
            expected++;
        }

        logger?.LogInformation(
            "Room {RoomId} restored from {EntryCount} journal entries at state version {StateVersion}.",
            roomId, stored.Entries.Count, room.StateVersion);
        // 恢复后的计时从现在重新开始：停机期间不算玩家的时间，重启不会让所有人一下全部超时。
        return new RoomExecutor(room, options, Task.CompletedTask, logger, journal, expected,
            stored.Entries.Select(entry => entry.Command.CommandId), time) { CreatedBy = stored.CreatedBy };
    }

    private static void Replay(Room room, JournalEntry entry)
    {
        try
        {
            var receipt = room.Execute(entry.Command);
            if (entry.Outcome.AppliedVersion != receipt.CommittedVersion)
            {
                throw new JournalReplayMismatchException(room.RoomId, entry.Sequence,
                    $"重放得到成功（版本 {receipt.CommittedVersion}），记录为 {Describe(entry.Outcome)}");
            }
        }
        catch (RuleRejectedException rejection)
        {
            if (entry.Outcome.Rejection?.Reason != rejection.Reason)
            {
                throw new JournalReplayMismatchException(room.RoomId, entry.Sequence,
                    $"重放得到拒绝 {rejection.Reason}，记录为 {Describe(entry.Outcome)}");
            }
        }
        catch (Exception error) when (error is not JournalReplayMismatchException)
        {
            throw new JournalReplayMismatchException(room.RoomId, entry.Sequence, $"重放时出现异常 {error.GetType().Name}: {error.Message}");
        }
    }

    private static string Describe(CommandOutcome outcome) => outcome.AppliedVersion is long version
        ? $"成功（版本 {version}）"
        : $"拒绝 {outcome.Rejection!.Reason}";

    private static Room CreateRoom(Guid roomId, int seatCount, RoomOptions? options)
    {
        options ??= RoomOptions.Default;
        options.EnsureValid();
        return new Room(roomId, seatCount, options.ReceiptCacheCapacity);
    }

    /// <summary>房间编号创建后不变，可以在任意线程读取。</summary>
    public Guid RoomId => _room.RoomId;

    /// <summary>房主：建房的玩家，只有他能开始整场。可信流程建的房间为空。创建后不变。</summary>
    public Guid? CreatedBy { get; init; }

    /// <summary>
    /// 正常关闭或故障停止后完成，本身不会以异常结束。
    /// 目录模块等待它，再根据 故障原因 决定摘除还是重建房间。
    /// </summary>
    public Task Stopped => _processingTask;

    /// <summary>为空表示没有发生故障（仍在运行或已正常关闭）。</summary>
    public Exception? FaultReason => Volatile.Read(ref _fault);

    /// <summary>是否已经不再接收新请求（已关闭或已故障）。</summary>
    public bool IsStopped => Volatile.Read(ref _stopping) != 0;

    /// <summary>当前在排队等待执行的命令数。供健康检查与容量观测读取。</summary>
    public int WaitingCommandCount => Volatile.Read(ref _waitingCommands);

    /// <summary>当前在排队等待执行的查询数。</summary>
    public int WaitingQueryCount => Volatile.Read(ref _waitingQueries);

    /// <summary>当前有未完成命令的玩家数。</summary>
    public int PlayersWithPendingCommand => _pendingByPlayer.Count;

    /// <summary>
    /// 命令在构造时已检查格式并复制输入，所以这里可以直接入队。
    /// 玩家发出的命令依次经过：同命令合并 → 每玩家一个未完成名额 → 速率限制 → 房间队列。
    /// 前两步不消耗速率预算：重发与“上一个还没完”都不是新的写入。
    /// </summary>
    public Task<CommandReceipt> ExecuteAsync(RoomCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ActorId is not Guid actor)
        {
            // 发牌（含牌序）和超时只能由房间自己生成。公开入口一旦接受它们，网络层的一个疏忽
            // 就能让客户端指定牌序或替别人超时；所以从类型上关死，而不是指望调用方记得过滤。
            throw new ArgumentException($"{command.GetType().Name} 不是玩家命令，不能经公开入口提交。", nameof(command));
        }

        if (_pendingByPlayer.TryGetValue(actor, out var pending))
        {
            // 同编号同内容：客户端没等到结果就重发了，等同一个结果即可。
            // 编号相同但内容不同，或是另一个意图：先把上一个处理完。
            return pending.Command == command
                ? pending.Result
                : RejectBusy(command, BusyReason.PlayerCommandPending);
        }

        if (!_commandLimiter.TryTake(actor, out var retryAfter))
        {
            return RejectBusy(command, BusyReason.RateLimited, retryAfter);
        }

        // 先占名额再提交：两个线程同时为同一玩家提交时只有一个能占到。
        var request = new CommandRequest(command);
        var slot = new PendingCommand(command, request.Result);
        if (!_pendingByPlayer.TryAdd(actor, slot))
        {
            // 与另一次提交竞争失败：按对方的命令重新判断（合并或拒绝）。预算已经扣了，不退还，竞争本身就是快速连发。
            return _pendingByPlayer.TryGetValue(actor, out var winner) && winner.Command == command
                ? winner.Result
                : RejectBusy(command, BusyReason.PlayerCommandPending);
        }

        // 无论以何种方式结束（含入队失败、故障、关闭时放弃）都释放名额，只释放自己占的那一项。
        // 必须在结果交给调用方之前释放：调用方拿到结果后立刻提交下一个命令是正常用法，不能被自己的上一个挡住。
        request.BeforeComplete = () => _pendingByPlayer.TryRemove(new KeyValuePair<Guid, PendingCommand>(actor, slot));

        return Submit(request, ref _waitingCommands, _commandCapacity);
    }

    /// <summary>
    /// 可信流程的命令（发牌等），只供本程序集与测试。正常运行时发牌与超时由执行器自己生成（<see cref="StartMatchAsync"/> 与计时器）。
    /// </summary>
    internal Task<CommandReceipt> ExecuteSystemAsync(RoomCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ActorId is not null)
        {
            throw new ArgumentException("玩家命令应经 ExecuteAsync 提交，以受每玩家名额与限流约束。", nameof(command));
        }
        return Submit(new CommandRequest(command), ref _waitingCommands, _commandCapacity);
    }

    // 查询的参数契约不依赖房间状态，在调用方线程检查：不占名额，也不会在队列里被当成故障。
    public Task<int?> FindSeatAsync(Guid playerId)
    {
        RequirePlayerId(playerId);
        return SubmitPlayerQuery(playerId, new QueryRequest<int?>(currentRoom => currentRoom.FindSeat(playerId)));
    }

    public Task<PlayerHandView> CreatePlayerViewAsync(Guid playerId)
    {
        RequirePlayerId(playerId);
        return SubmitPlayerQuery(playerId, new QueryRequest<PlayerHandView>(currentRoom => currentRoom.CreatePlayerView(playerId)));
    }

    private Task<T> SubmitPlayerQuery<T>(Guid playerId, QueryRequest<T> request)
    {
        if (!_queryLimiter.TryTake(playerId, out var retryAfter))
        {
            RejectSubmission(request, "rate_limited");
            request.Reject(new RoomBusyException(BusyReason.RateLimited, retryAfter));
            return request.Result;
        }
        return Submit(request, ref _waitingQueries, _queryCapacity);
    }

    private Task<CommandReceipt> RejectBusy(RoomCommand command, BusyReason reason, TimeSpan? retryAfter = null)
    {
        var request = new CommandRequest(command);
        RejectSubmission(request, reason switch
        {
            BusyReason.PlayerCommandPending => "player_pending",
            BusyReason.RateLimited => "rate_limited",
            _ => "busy"
        });
        request.Reject(new RoomBusyException(reason, retryAfter));
        return request.Result;
    }

    public Task<RoomOverview> CreateOverviewAsync() =>
        Submit(new QueryRequest<RoomOverview>(currentRoom => currentRoom.CreateOverview() with { Deadline = _deadline }), ref _waitingQueries, _queryCapacity);

    /// <summary>
    /// 开始整场：入座者全部参赛，服务端洗牌并随机选首局起始位。之后每局结算、间隔到期后自动发下一局。
    /// 只供可信流程调用（房主点开始、人数凑齐等）。入座不足两人时以 <see cref="RejectionReason.NotEnoughPlayers"/> 拒绝。
    /// </summary>
    public Task<CommandReceipt> StartMatchAsync(long? expectedVersion = null) =>
        Submit(new GeneratedCommandRequest(room => room.CreateFirstDeal(expectedVersion), "StartMatch"), ref _waitingCommands, _commandCapacity);

    // 仅供测试：消费循环退出后检查房间状态。运行期间读取会绕过队列，所以不对外公开。
    internal Room RoomForTest => _room;

    // 仅供测试：在消费循环里执行任意同步代码，用来模拟“一条请求卡住不返回”。
    internal Task<T> RunInLoopForTestAsync<T>(Func<Room, T> work) =>
        Submit(new QueryRequest<T>(work), ref _waitingQueries, _queryCapacity);

    private Task<T> Submit<T>(QueuedRequest<T> request, ref int waiting, int limit)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            RejectSubmission(request, "stopped");
            request.Reject(CreateStopException());
        }
        // 先占名额再入队。Interlocked 让多个提交方同时占位时也不会超过上限；超了就退回名额。
        else if (Interlocked.Increment(ref waiting) > limit)
        {
            Interlocked.Decrement(ref waiting);
            if (Volatile.Read(ref _stopping) != 0)
            {
                RejectSubmission(request, "stopped");
                request.Reject(CreateStopException());
            }
            else
            {
                RejectSubmission(request, "busy");
                request.Reject(new RoomBusyException(BusyReason.QueueFull));
            }
        }
        // 名额已保证队列有空位，TryWrite 只会因为队列已关闭而失败。
        else if (!_queue.Writer.TryWrite(request))
        {
            Interlocked.Decrement(ref waiting);
            RejectSubmission(request, "stopped");
            request.Reject(CreateStopException());
        }
        return request.Result;
    }

    private void RejectSubmission(QueuedRequest request, string reason)
    {
        RoomMetrics.RejectedSubmissions.Add(1, new KeyValuePair<string, object?>("reason", reason));
        // 按玩家的拒绝是客户端行为，可能被刷成千上万条：只计数，不逐条写日志（设计文档：大量拒绝日志聚合计数）。
        if (reason is "player_pending" or "rate_limited")
        {
            return;
        }
        _logger.LogWarning(
            "Room {RoomId} refused an incoming {RequestKind}: {Reason}. Waiting commands {WaitingCommands}/{CommandLimit}, queries {WaitingQueries}/{QueryLimit}.",
            RoomId, request.Kind, reason,
            Volatile.Read(ref _waitingCommands), _commandCapacity,
            Volatile.Read(ref _waitingQueries), _queryCapacity);
    }

    // 让调用方区分“房间正常关闭”和“房间坏了”，两者的上层处理完全不同。
    private Exception CreateStopException() => Volatile.Read(ref _fault) is { } reason
        ? new RoomFaultedException(reason)
        : new ObjectDisposedException(nameof(RoomExecutor), "房间执行器已停止接收请求。");

    private async Task ProcessQueueAsync(Task startSignal)
    {
        try
        {
            await startSignal.ConfigureAwait(false);
            // 从日志恢复的房间可能正等着某人行动或已经结束：不等第一条请求，开始时就按当前状态处理。
            AfterCommand();
            var reader = _queue.Reader;
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                // 放弃后不再取新请求；剩下的由 StopAsync 拒绝。
                if (Volatile.Read(ref _abandoned) != 0)
                {
                    break;
                }
                if (!reader.TryRead(out var request))
                {
                    continue;
                }

                // 离开队列即归还名额：额度限制的是“排队等待”的请求数。
                ReleaseQuota(request);

                var startedAt = Stopwatch.GetTimestamp();
                try
                {
                    await request.ExecuteAsync(this).ConfigureAwait(false);
                }
                finally
                {
                    // 故障也要记账，否则指标里的耗时与请求数会对不上。
                    RecordResult(request, Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
                }
                // 命令之后的计时与发布在请求内部、完成结果之前做（见 QueuedRequest.ExecuteAsync）。
            }
        }
        catch (Exception error)
        {
            // 不再向外抛：故障通过 故障原因 公开。若让任务以异常结束而无人等待，
            // 异常只会在垃圾回收时以“未观察的任务异常”出现，目录模块无从得知。
            Volatile.Write(ref _fault, error);
            RoomMetrics.Faults.Add(1, new KeyValuePair<string, object?>("stage", _room.Stage.ToString()));
            _logger.LogError(error, "Room {RoomId} stopped after an unexpected error; queued requests were failed.", RoomId);
        }
        finally
        {
            Interlocked.Exchange(ref _stopping, 1);
            // 唤醒所有等待变化的订阅者：它们看到房间已停止就关闭连接，不会永远挂着。
            Volatile.Read(ref _published).MarkSuperseded();
            _timer?.Dispose();
            _timer = null;
            _queue.Writer.TryComplete();
            // 意外故障时，使每个已接收请求都结束等待，不继续操作可疑状态。
            // 正常关闭时队列已空；超时放弃时 StopAsync 已经认领了清理，这里什么也不做。
            DrainIfClaimed();

            _logger.LogInformation(
                "Room {RoomId} stopped. Faulted: {Faulted}. Abandoned requests: {AbandonedRequests}.",
                RoomId, Volatile.Read(ref _fault) is not null, Volatile.Read(ref _droppedRequests));
        }
    }

    private void RecordResult(QueuedRequest request, double elapsedMilliseconds)
    {
        var kind = new KeyValuePair<string, object?>("kind", request.Kind);
        switch (request.Outcome)
        {
            case RequestOutcome.Applied:
                RoomMetrics.Requests.Add(1, kind, new KeyValuePair<string, object?>("outcome", "applied"));
                _logger.LogDebug(
                    "Room {RoomId} applied {RequestKind} in {ElapsedMs:F3} ms.", RoomId, request.Kind, elapsedMilliseconds);
                break;

            case RequestOutcome.Rejected:
                var reason = request.RejectionReason!.Value.ToString();
                RoomMetrics.Requests.Add(1, kind,
                    new KeyValuePair<string, object?>("outcome", "rejected"),
                    new KeyValuePair<string, object?>("reason", reason));
                _logger.LogDebug(
                    "Room {RoomId} rejected {RequestKind} by rule {Reason}.", RoomId, request.Kind, reason);
                break;

            default:
                RoomMetrics.Requests.Add(1, kind, new KeyValuePair<string, object?>("outcome", "faulted"));
                break;
        }

        RoomMetrics.RequestDuration.Record(elapsedMilliseconds, kind);
    }

    /// <summary>
    /// 停止接收新请求，在 <see cref="RoomOptions.ShutdownTimeout"/> 内等待已接收的请求处理完。
    /// 超时或 <paramref name="cancellationToken"/> 取消时，放弃仍在排队的请求：
    /// 它们没有执行，调用方收到 <see cref="ObjectDisposedException"/>，可以安全重试。
    ///
    /// 返回值的 <see cref="RoomStopResult.ConsumerStopped"/> 为 false 时，可能仍有一条请求在执行。
    /// 在 <see cref="Stopped"/> 完成之前，不得把这个房间交给其他所有者，否则两边可能同时提交。
    ///
    /// 可以重复调用，也可以与 <see cref="DisposeAsync"/> 并发调用；不会因房间故障而抛出。
    /// </summary>
    public async Task<RoomStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0)
        {
            _logger.LogInformation(
                "Room {RoomId} stopping. Waiting commands {WaitingCommands}, queries {WaitingQueries}; timeout {ShutdownTimeout}.",
                RoomId, Volatile.Read(ref _waitingCommands), Volatile.Read(ref _waitingQueries), _shutdownTimeout);
        }
        _queue.Writer.TryComplete();

        try
        {
            await _processingTask.WaitAsync(_shutdownTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Abandon("timeout");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Abandon("cancelled");
        }

        return CreateStopResult();
    }

    /// <summary>等同于 <see cref="StopAsync"/>，丢弃结果；最长等待时间同样受 <see cref="RoomOptions.ShutdownTimeout"/> 限制。</summary>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void Abandon(string trigger)
    {
        Interlocked.Exchange(ref _abandoned, 1);
        // 消费循环卡住时它不会走到 finally，只能由这里清理，否则排队请求会永远挂起。
        int dropped = DrainIfClaimed();
        if (dropped == 0 && Volatile.Read(ref _droppedRequests) == 0 && _processingTask.IsCompleted)
        {
            return;
        }

        RoomMetrics.AbandonedRequests.Add(dropped, new KeyValuePair<string, object?>("trigger", trigger));
        _logger.LogWarning(
            "Room {RoomId} did not drain within {ShutdownTimeout} ({Trigger}); {AbandonedRequests} queued requests were abandoned without executing. Consumer stopped: {ConsumerStopped}.",
            RoomId, _shutdownTimeout, trigger, Volatile.Read(ref _droppedRequests), _processingTask.IsCompleted);
        if (!_processingTask.IsCompleted)
        {
            _logger.LogError(
                "Room {RoomId} still has a request executing after shutdown was abandoned; do not transfer ownership until it stops.",
                RoomId);
        }
    }

    /// <summary>认领清理权并拒绝队列里剩余的请求，返回本次拒绝的数量；未认领到时返回 0。</summary>
    private int DrainIfClaimed()
    {
        if (Interlocked.Exchange(ref _drainClaimed, 1) != 0)
        {
            return 0;
        }

        int dropped = 0;
        while (_queue.Reader.TryRead(out var request))
        {
            ReleaseQuota(request);
            request.Reject(CreateStopException());
            dropped++;
        }
        Interlocked.Add(ref _droppedRequests, dropped);
        return dropped;
    }

    private void ReleaseQuota(QueuedRequest request)
    {
        if (request is ClockRequest)
        {
            // 计时请求不按计数占额度，而是用 _clockQueued 保证最多一条；清除在处理它时进行。
            return;
        }
        if (request.IsQuery)
        {
            Interlocked.Decrement(ref _waitingQueries);
        }
        else
        {
            Interlocked.Decrement(ref _waitingCommands);
        }
    }

    // 每次按当前状态计算，所以重复调用 StopAsync 得到一致的结果。
    private RoomStopResult CreateStopResult()
    {
        bool consumerStopped = _processingTask.IsCompleted;
        bool drained = consumerStopped
            && Volatile.Read(ref _abandoned) == 0
            && Volatile.Read(ref _fault) is null;
        return new RoomStopResult(drained, Volatile.Read(ref _droppedRequests), consumerStopped);
    }

    private static void RequirePlayerId(Guid playerId)
    {
        if (playerId == Guid.Empty)
        {
            throw new ArgumentException("玩家编号不能为空编号。", nameof(playerId));
        }
    }

    /// <summary>
    /// 整场结束时完成（打满局数或只剩一人有筹码）。房间目录据此在保留一段时间后关闭房间。
    /// 房间停止而整场未结束时不会完成；需要同时等 <see cref="Stopped"/>。
    /// </summary>
    public Task MatchFinished => _matchFinished.Task;

    // 每处理完一条命令（以及循环开始时）调用，只在消费循环里。
    private void AfterCommand()
    {
        if (_room.Stage == RoomStage.MatchFinished)
        {
            _matchFinished.TrySetResult();
        }
        RearmClock();
        Publish();
    }

    // 只在消费循环（以及构造时）调用。
    private void Publish()
    {
        var current = _published;
        if (current.StateVersion == _room.StateVersion && current.Deadline == _deadline)
        {
            return;
        }
        // 先换上新的再唤醒旧的等待者：醒来的人读到的一定是新快照，等的一定是下一次变化。
        Volatile.Write(ref _published, PublishedRoomState.Capture(_room, _deadline));
        current.MarkSuperseded();
    }

    /// <summary>
    /// 最新的已发布状态（不可变）。用法：取它、发送、再 await 它的 <see cref="PublishedRoomState.Superseded"/>，醒来后重新取。
    /// 取与等之间若有变化，任务已完成，不会漏掉。房间停止时最后一份的任务也会完成，调用方据 <see cref="IsStopped"/> 判断是否退出。
    /// </summary>
    public PublishedRoomState Published => Volatile.Read(ref _published);

    /// <summary>
    /// 按房间当前等待的决定重设计时器。等待没变（同一版本）就不动，所以被拒绝的命令不会重置期限。
    /// </summary>
    private void RearmClock()
    {
        var pending = _room.Pending;
        if (pending == _timedFor && (_deadline is not null || pending.Kind == PendingKind.None))
        {
            return;
        }
        StopTimer();
        _timedFor = pending;

        var delay = pending.Kind switch
        {
            PendingKind.Bet or PendingKind.Reroute => _turnTimeout,
            PendingKind.NextHand => _nextHandDelay,
            _ => TimeSpan.Zero
        };
        if (delay <= TimeSpan.Zero)
        {
            return;
        }
        _deadline = _time.GetUtcNow() + delay;
        _deadlineTimestamp = _time.GetTimestamp() + (long)(delay.TotalSeconds * _time.TimestampFrequency);
        StartTimer(delay);
    }

    private void StartTimer(TimeSpan delay)
    {
        _timer?.Dispose();
        _timer = _time.CreateTimer(static state => ((RoomExecutor)state!).OnClock(), this, delay, Timeout.InfiniteTimeSpan);
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
        _deadline = null;
    }

    // 计时器线程：只入队，不碰房间。已有一条在排队时不再放第二条：它被处理时会重新读状态，一条就够。
    private void OnClock()
    {
        if (Volatile.Read(ref _stopping) != 0 || Interlocked.Exchange(ref _clockQueued, 1) != 0)
        {
            return;
        }
        var request = new ClockRequest();
        // 没有人等这个结果；停止时它会被拒绝，读走异常，免得成为未观察的任务异常。
        ObserveLater(request.Result);
        // 预留的位置保证不会因为队列满而失败；失败只可能是队列已关闭（正在停止）。
        if (!_queue.Writer.TryWrite(request))
        {
            Volatile.Write(ref _clockQueued, 0);
        }
    }

    /// <summary>
    /// 轮到计时请求：期限已过且等待没变，就生成对应命令（超时或下一局发牌）并像普通命令一样执行、写日志。
    /// 期限还没到（计时器早触发了一点）就按剩余时间重新计时；等待已变或不再计时就什么也不做。
    /// </summary>
    private async ValueTask HandleClockAsync()
    {
        Volatile.Write(ref _clockQueued, 0);
        if (_deadline is null || _room.Pending != _timedFor)
        {
            return;
        }
        var remaining = TimeSpan.FromSeconds((_deadlineTimestamp - _time.GetTimestamp()) / (double)_time.TimestampFrequency);
        if (remaining > TimeSpan.Zero)
        {
            StartTimer(remaining);
            return;
        }

        var pending = _timedFor;
        RoomCommand command = pending.Kind switch
        {
            PendingKind.Bet or PendingKind.Reroute => new TimeoutCommand(Guid.NewGuid(), pending.Version),
            PendingKind.NextHand => _room.CreateNextDeal(),
            _ => throw new InvalidOperationException($"不应为 {pending.Kind} 计时。")
        };
        try
        {
            await ProcessCommandAsync(command).ConfigureAwait(false);
        }
        catch (RuleRejectedException error)
        {
            // 刚核对过等待，不应被拒绝。真发生了说明房间逻辑有误；按故障停止，比静默停在这一步可见得多。
            throw new InvalidOperationException($"房间拒绝了自己生成的 {command.GetType().Name}（{error.Reason}）。", error);
        }
    }

    /// <summary>
    /// 处理一条命令。没有日志时直接在内存执行；有日志时，结果写入日志之后才返回。
    /// 顺序：内存缓存命中 → 回放原结果（不访问数据库）；编号写过日志但已出缓存 → 到日志取原结果；
    /// 否则是新命令：执行规则，把结果（成功或规则拒绝）写入日志，写入成功才把结果交给调用方。
    /// 新命令只访问数据库一次。
    /// </summary>
    private async ValueTask<CommandReceipt> ProcessCommandAsync(RoomCommand command)
    {
        if (_journal is null || _room.HasCachedResult(command.CommandId))
        {
            return _room.Execute(command);
        }

        if (_journaledCommandIds.Contains(command.CommandId))
        {
            JournalEntry? stored;
            try
            {
                stored = await WithTimeout(token => _journal.FindAsync(RoomId, command.CommandId, token), _journalTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // 查不到就无法判断原结果，不能冒险再执行一次。
                throw new RoomPersistenceException(command.CommandId, error);
            }
            // 集合说写过、日志里却没有：内存与数据库不一致，不能在这种状态上继续。
            var entry = stored ?? throw new RoomPersistenceException(command.CommandId,
                new InvalidOperationException("该命令编号已记录为写入日志，但日志中找不到。"));
            return entry.Command == command
                ? entry.Outcome.Replay(command.CommandId)
                : throw new RuleRejectedException(RejectionReason.CommandIdConflict, "该命令编号已用于另一项操作。");
        }

        CommandReceipt? receipt = null;
        ExceptionDispatchInfo? rejection = null;
        CommandOutcome outcome;
        try
        {
            receipt = _room.Execute(command);
            outcome = CommandOutcome.Applied(receipt.CommittedVersion);
        }
        catch (RuleRejectedException error)
        {
            rejection = ExceptionDispatchInfo.Capture(error);
            outcome = CommandOutcome.Rejected(error.Reason, error.Message);
        }

        try
        {
            // 超时同时覆盖等待连接池与等待数据库：卡住的数据库只能占住这个房间有限的时间。
            var entry = new JournalEntry(_nextSequence, command, outcome);
            await WithTimeout(token => _journal.AppendAsync(RoomId, entry, token), _journalTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // 内存已经改变，但不知道日志是否提交（超时取消也一样：取消可能在提交之后才到达）。
            // 不能确认成功，也不能宣称失败。房间随之故障停止。
            throw new RoomPersistenceException(command.CommandId, error);
        }
        _nextSequence++;
        _journaledCommandIds.Add(command.CommandId);

        rejection?.Throw();
        return receipt!;
    }

    /// <summary>
    /// 在时限内等待一次日志操作。不只把取消令牌交给驱动：数据库进程停顿（连接仍在、但不响应）时，
    /// Npgsql 的取消请求本身也收不到回应，实测会挂住 10 秒以上。所以执行器到时就不再等待，
    /// 不管底层操作是否已经停下。被丢下的操作仍可能稍后提交，这正是“结果未知”的含义：
    /// 房间随之停止，恢复时以数据库里的实际记录为准。
    /// </summary>
    private static async Task WithTimeout(Func<CancellationToken, Task> operation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        var task = operation(source.Token);
        try
        {
            await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveLater(task);
            throw;
        }
    }

    private static async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> operation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        var task = operation(source.Token);
        try
        {
            return await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ObserveLater(task);
            throw;
        }
    }

    // 被丢下的操作稍后失败时，读取它的异常，避免变成未观察的任务异常。
    private static void ObserveLater(Task task) =>
        _ = task.ContinueWith(static finished => _ = finished.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private abstract class QueuedRequest
    {
        public abstract bool IsQuery { get; }

        /// <summary>日志与指标里用的请求类别名，不含任何牌面或身份信息。</summary>
        public abstract string Kind { get; }

        public RequestOutcome Outcome { get; protected set; } = RequestOutcome.Faulted;

        public RejectionReason? RejectionReason { get; protected set; }

        public abstract ValueTask ExecuteAsync(RoomExecutor owner);
        public abstract void Reject(Exception reason);
    }

    private abstract class QueuedRequest<T> : QueuedRequest
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _completed;
        public Task<T> Result => _completion.Task;

        /// <summary>在结果交给调用方之前执行一次，无论成功、拒绝还是故障。用于释放每玩家名额。</summary>
        public Action? BeforeComplete { get; set; }

        protected abstract ValueTask<T> RunAsync(RoomExecutor owner);

        public override async ValueTask ExecuteAsync(RoomExecutor owner)
        {
            try
            {
                var value = await RunAsync(owner).ConfigureAwait(false);
                // 先重设计时并发布新快照，再完成结果：调用方拿到“已提交，版本 N”时，已发布的状态一定不早于 N。
                // 反过来的话，调用方（以及据此推送的连接）可能先读到旧快照。
                if (!IsQuery)
                {
                    owner.AfterCommand();
                }
                if (Finish())
                {
                    _completion.TrySetResult(value);
                }
                Outcome = RequestOutcome.Applied;
            }
            catch (RuleRejectedException error)
            {
                // 规则约定：抛出拒绝前没有改动状态，所以后续请求可以继续。拒绝不改变状态，发布是空操作；照样调用，保持一处入口。
                if (!IsQuery)
                {
                    owner.AfterCommand();
                }
                Outcome = RequestOutcome.Rejected;
                RejectionReason = error.Reason;
                Reject(error);
            }
            catch (Exception error)
            {
                // 其他异常无法确认状态是否改了一半，必须停止这个房间。
                Outcome = RequestOutcome.Faulted;
                Reject(error);
                throw;
            }
        }

        public override void Reject(Exception reason)
        {
            if (Finish())
            {
                _completion.TrySetException(reason);
            }
        }

        // 只有第一次结束生效：执行与关闭时的放弃可能先后到达。
        private bool Finish()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return false;
            }
            BeforeComplete?.Invoke();
            return true;
        }
    }

    private sealed class CommandRequest(RoomCommand command) : QueuedRequest<CommandReceipt>
    {
        public override bool IsQuery => false;
        public override string Kind => command.GetType().Name;
        protected override ValueTask<CommandReceipt> RunAsync(RoomExecutor owner) => owner.ProcessCommandAsync(command);
    }

    /// <summary>由房间生成命令再执行：生成要看当前状态（座位、筹码），所以必须在消费循环里做。</summary>
    private sealed class GeneratedCommandRequest(Func<Room, RoomCommand> create, string kind) : QueuedRequest<CommandReceipt>
    {
        public override bool IsQuery => false;
        public override string Kind => kind;
        protected override ValueTask<CommandReceipt> RunAsync(RoomExecutor owner) => owner.ProcessCommandAsync(create(owner._room));
    }

    /// <summary>计时器到期。没有调用方等待结果；处理时重新读当前状态，所以不带任何参数。</summary>
    private sealed class ClockRequest : QueuedRequest<bool>
    {
        public override bool IsQuery => false;
        public override string Kind => "Clock";
        protected override async ValueTask<bool> RunAsync(RoomExecutor owner)
        {
            await owner.HandleClockAsync().ConfigureAwait(false);
            return true;
        }
    }

    // 查询不改变状态，不需要去重、记录或持久化，用委托表达就够了。
    private sealed class QueryRequest<T>(Func<Room, T> query) : QueuedRequest<T>
    {
        public override bool IsQuery => true;
        public override string Kind => "Query";
        protected override ValueTask<T> RunAsync(RoomExecutor owner) => ValueTask.FromResult(query(owner._room));
    }
}
