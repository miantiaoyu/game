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
/// </summary>
public sealed class RoomExecutor : IAsyncDisposable
{
    // 查询只读、可以随时重发，额度小；与命令分开计数，频繁刷新视图挤不掉真正的操作。
    // 超时等内部事件接入时，再按同样方式增加第三类额度（设计为 8）。

    private readonly Room _room;
    private readonly Channel<QueuedRequest> _queue;
    private readonly Task _processingTask;
    private readonly int _commandCapacity;
    private readonly int _queryCapacity;
    private readonly TimeSpan _shutdownTimeout;
    private readonly ILogger _logger;
    private readonly IRoomJournal? _journal;
    // 下一条日志记录的序号；只在消费循环里读写。
    private long _nextSequence;
    private int _waitingCommands;
    private int _waitingQueries;
    private int _stopping;
    // 超时放弃后置位：消费循环看到它就不再取新请求。
    private int _abandoned;
    // 谁先抢到，谁负责把队列里剩下的请求全部拒绝；保证每个请求要么被执行、要么恰好被拒绝一次。
    private int _drainClaimed;
    private int _droppedRequests;
    // 消费循环先写入故障，再置位停止接收；提交方看到停止后再读故障，就能读到写入的值。
    private Exception? _fault;

    /// <summary>创建空房间并开始处理。</summary>
    public RoomExecutor(Guid roomId, int seatCount, RoomOptions? options = null, ILogger? logger = null)
        : this(CreateRoom(roomId, seatCount, options), options, Task.CompletedTask, logger)
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
        long nextSequence = 1)
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
        ArgumentOutOfRangeException.ThrowIfLessThan(nextSequence, 1);
        _nextSequence = nextSequence;
        // 没有传入日志器时用 NullLogger：调用方不必为了构造一个房间而准备日志基础设施。
        _logger = logger ?? NullLogger.Instance;
        // 命令和查询共用一条队列，才能保证“先提交的修改先生效，后提交的查询能看到它”。
        // 名额由两个计数器分别控制；队列容量取两者之和，只作为最后一道保护。
        // SingleReader = false：超时放弃时，StopAsync 要在消费循环之外取走剩余请求。
        // 消费循环卡住时，这是让排队请求不永远挂起的唯一办法。
        _queue = Channel.CreateBounded<QueuedRequest>(new BoundedChannelOptions(_commandCapacity + _queryCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        // 先在内存里构造：参数非法时不在日志里留下无法加载的房间。
        var room = CreateRoom(roomId, seatCount, options);
        await journal.CreateRoomAsync(roomId, seatCount, cancellationToken).ConfigureAwait(false);
        return new RoomExecutor(room, options, Task.CompletedTask, logger, journal, nextSequence: 1);
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        var stored = await journal.LoadAsync(roomId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"日志中没有房间 {roomId}。");
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
        return new RoomExecutor(room, options, Task.CompletedTask, logger, journal, expected);
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

    /// <summary>命令在构造时已检查格式并复制输入，所以这里可以直接入队。</summary>
    public Task<CommandReceipt> ExecuteAsync(RoomCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Submit(new CommandRequest(command), ref _waitingCommands, _commandCapacity);
    }

    // 查询的参数契约不依赖房间状态，在调用方线程检查：不占名额，也不会在队列里被当成故障。
    public Task<int?> FindSeatAsync(Guid playerId)
    {
        RequirePlayerId(playerId);
        return Submit(new QueryRequest<int?>(currentRoom => currentRoom.FindSeat(playerId)), ref _waitingQueries, _queryCapacity);
    }

    public Task<PlayerHandView> CreatePlayerViewAsync(Guid playerId)
    {
        RequirePlayerId(playerId);
        return Submit(new QueryRequest<PlayerHandView>(currentRoom => currentRoom.CreatePlayerView(playerId)), ref _waitingQueries, _queryCapacity);
    }

    public Task<RoomOverview> CreateOverviewAsync() =>
        Submit(new QueryRequest<RoomOverview>(currentRoom => currentRoom.CreateOverview()), ref _waitingQueries, _queryCapacity);

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
                request.Reject(new RoomBusyException());
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
    /// 处理一条命令。没有日志时直接在内存执行；有日志时，结果写入日志之后才返回。
    /// 顺序：内存缓存命中 → 回放原结果（不写日志）；缓存淘汰过且日志里有 → 按日志回放；
    /// 否则执行规则，把结果（成功或规则拒绝）写入日志，写入成功才把结果交给调用方。
    /// </summary>
    private async ValueTask<CommandReceipt> ProcessCommandAsync(RoomCommand command)
    {
        if (_journal is null || _room.HasCachedResult(command.CommandId))
        {
            return _room.Execute(command);
        }

        if (_room.HasEvictedReceipts)
        {
            // 内存缓存只保留最近的记录；淘汰过之后，缓存未命中不代表没处理过，要以日志为准。
            JournalEntry? stored;
            try
            {
                stored = await _journal.FindAsync(RoomId, command.CommandId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // 查不到就无法判断是不是重发，不能冒险再执行一次。
                throw new RoomPersistenceException(command.CommandId, error);
            }
            if (stored is not null)
            {
                return stored.Command == command
                    ? stored.Outcome.Replay(command.CommandId)
                    : throw new RuleRejectedException(RejectionReason.CommandIdConflict, "该命令编号已用于另一项操作。");
            }
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
            await _journal.AppendAsync(RoomId, new JournalEntry(_nextSequence, command, outcome), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // 内存已经改变，但不知道日志是否提交：不能确认成功，也不能宣称失败。房间随之故障停止。
            throw new RoomPersistenceException(command.CommandId, error);
        }
        _nextSequence++;

        rejection?.Throw();
        return receipt!;
    }

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
        public Task<T> Result => _completion.Task;

        protected abstract ValueTask<T> RunAsync(RoomExecutor owner);

        public override async ValueTask ExecuteAsync(RoomExecutor owner)
        {
            try
            {
                _completion.TrySetResult(await RunAsync(owner).ConfigureAwait(false));
                Outcome = RequestOutcome.Applied;
            }
            catch (RuleRejectedException error)
            {
                // 规则约定：抛出拒绝前没有改动状态，所以后续请求可以继续。
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

        public override void Reject(Exception reason) => _completion.TrySetException(reason);
    }

    private sealed class CommandRequest(RoomCommand command) : QueuedRequest<CommandReceipt>
    {
        public override bool IsQuery => false;
        public override string Kind => command.GetType().Name;
        protected override ValueTask<CommandReceipt> RunAsync(RoomExecutor owner) => owner.ProcessCommandAsync(command);
    }

    // 查询不改变状态，不需要去重、记录或持久化，用委托表达就够了。
    private sealed class QueryRequest<T>(Func<Room, T> query) : QueuedRequest<T>
    {
        public override bool IsQuery => true;
        public override string Kind => "Query";
        protected override ValueTask<T> RunAsync(RoomExecutor owner) => ValueTask.FromResult(query(owner._room));
    }
}
