using System.Diagnostics;
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
/// 当前仅提供进程内队列；身份认证和持久提交尚未接入。
/// 因此任务成功只代表内存状态已改变，不代表已经保存。
///
/// 运维可见性：日志与指标都只输出标识符、枚举与数量。**绝不输出牌**
/// （牌序、底牌、公共牌），一副牌序泄漏就等于作弊；相关类型已覆盖 ToString 做脱敏。
/// </summary>
public sealed class RoomExecutor : IAsyncDisposable
{
    public const int MaxWaitingCommands = 56;
    // 查询只读、可以随时重发，额度小；与命令分开计数，频繁刷新视图挤不掉真正的操作。
    // 超时等内部事件接入时，再按同样方式增加第三类额度（设计为 8）。
    public const int MaxWaitingQueries = 8;

    private readonly Room _room;
    private readonly Channel<QueuedRequest> _queue;
    private readonly Task _processingTask;
    private readonly int _commandCapacity;
    private readonly int _queryCapacity;
    private readonly ILogger _logger;
    private int _waitingCommands;
    private int _waitingQueries;
    private int _stopping;
    // 消费循环先写入故障，再置位停止接收；提交方看到停止后再读故障，就能读到写入的值。
    private Exception? _fault;

    /// <summary>创建空房间并开始处理。</summary>
    public RoomExecutor(Guid RoomId, int SeatCount, int commandQueueCapacity = MaxWaitingCommands, ILogger? logger = null)
        : this(new Room(RoomId, SeatCount), commandQueueCapacity, Task.CompletedTask, MaxWaitingQueries, logger)
    {
    }

    /// <summary>
    /// 接管一个已有房间，例如以后从持久状态重建的房间。
    /// 调用后由执行器独占该对象，调用方不得再直接调用它的方法，否则会绕过队列。
    /// </summary>
    public RoomExecutor(Room initialRoom, int commandQueueCapacity = MaxWaitingCommands, ILogger? logger = null)
        : this(initialRoom, commandQueueCapacity, Task.CompletedTask, MaxWaitingQueries, logger)
    {
    }

    // 测试可以暂缓消费，以确定性验证满队列和关闭时的行为。
    // 正常构造传入已完成任务，立即开始等待请求。
    internal RoomExecutor(
        Room initialRoom,
        int commandQueueCapacity,
        Task startSignal,
        int queryQueueCapacity = MaxWaitingQueries,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(initialRoom);
        ArgumentNullException.ThrowIfNull(startSignal);
        if (commandQueueCapacity < 1 || commandQueueCapacity > MaxWaitingCommands)
        {
            throw new ArgumentOutOfRangeException(nameof(commandQueueCapacity), commandQueueCapacity, $"命令队列容量必须在 1～{MaxWaitingCommands} 之间。");
        }
        if (queryQueueCapacity < 1 || queryQueueCapacity > MaxWaitingQueries)
        {
            throw new ArgumentOutOfRangeException(nameof(queryQueueCapacity), queryQueueCapacity, $"查询队列容量必须在 1～{MaxWaitingQueries} 之间。");
        }

        _room = initialRoom;
        _commandCapacity = commandQueueCapacity;
        _queryCapacity = queryQueueCapacity;
        // 没有传入日志器时用 NullLogger：调用方不必为了构造一个房间而准备日志基础设施。
        _logger = logger ?? NullLogger.Instance;
        // 命令和查询共用一条队列，才能保证“先提交的修改先生效，后提交的查询能看到它”。
        // 名额由两个计数器分别控制；队列容量取两者之和，只作为最后一道保护。
        _queue = Channel.CreateBounded<QueuedRequest>(new BoundedChannelOptions(_commandCapacity + _queryCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _processingTask = ProcessQueueAsync(startSignal);
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
            await foreach (var request in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                // 离开队列即归还名额：额度限制的是“排队等待”的请求数。
                if (request.IsQuery)
                {
                    Interlocked.Decrement(ref _waitingQueries);
                }
                else
                {
                    Interlocked.Decrement(ref _waitingCommands);
                }

                var startedAt = Stopwatch.GetTimestamp();
                try
                {
                    request.Execute(_room);
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
            while (_queue.Reader.TryRead(out var request))
            {
                request.Reject(CreateStopException());
            }

            _logger.LogInformation(
                "Room {RoomId} stopped. Faulted: {Faulted}.", RoomId, Volatile.Read(ref _fault) is not null);
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

    /// <summary>停止接收，等待已接收的请求全部处理完成；可以重复调用，不会因房间故障而抛出。</summary>
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        _queue.Writer.TryComplete();
        return new ValueTask(_processingTask);
    }

    private static void RequirePlayerId(Guid playerId)
    {
        if (playerId == Guid.Empty)
        {
            throw new ArgumentException("玩家编号不能为空编号。", nameof(playerId));
        }
    }

    private abstract class QueuedRequest
    {
        public abstract bool IsQuery { get; }

        /// <summary>日志与指标里用的请求类别名，不含任何牌面或身份信息。</summary>
        public abstract string Kind { get; }

        public RequestOutcome Outcome { get; protected set; } = RequestOutcome.Faulted;

        public RejectionReason? RejectionReason { get; protected set; }

        public abstract void Execute(Room currentRoom);
        public abstract void Reject(Exception reason);
    }

    private abstract class QueuedRequest<T> : QueuedRequest
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<T> Result => _completion.Task;

        protected abstract T Run(Room currentRoom);

        public override void Execute(Room currentRoom)
        {
            try
            {
                _completion.TrySetResult(Run(currentRoom));
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
        protected override CommandReceipt Run(Room currentRoom) => currentRoom.Execute(command);
    }

    // 查询不改变状态，不需要去重、记录或持久化，用委托表达就够了。
    private sealed class QueryRequest<T>(Func<Room, T> query) : QueuedRequest<T>
    {
        public override bool IsQuery => true;
        public override string Kind => "Query";
        protected override T Run(Room currentRoom) => query(currentRoom);
    }
}
