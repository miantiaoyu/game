using System.Threading.Channels;
using DarkTide.Rules;

namespace DarkTide.Room;

/// <summary>
/// 多个调用方可以提交请求，同一房间只有一个消费循环执行请求。
/// 当前仅提供进程内队列；身份认证和持久提交尚未接入。
/// 因此任务成功只代表内存状态已改变，不代表已经保存。
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
    private int _waitingCommands;
    private int _waitingQueries;
    private int _stopping;
    // 消费循环先写入故障，再置位停止接收；提交方看到停止后再读故障，就能读到写入的值。
    private Exception? _fault;

    /// <summary>创建空房间并开始处理。</summary>
    public RoomExecutor(Guid RoomId, int SeatCount, int commandQueueCapacity = MaxWaitingCommands)
        : this(new Room(RoomId, SeatCount), commandQueueCapacity, Task.CompletedTask)
    {
    }

    /// <summary>
    /// 接管一个已有房间，例如以后从持久状态重建的房间。
    /// 调用后由执行器独占该对象，调用方不得再直接调用它的方法，否则会绕过队列。
    /// </summary>
    public RoomExecutor(Room initialRoom, int commandQueueCapacity = MaxWaitingCommands)
        : this(initialRoom, commandQueueCapacity, Task.CompletedTask)
    {
    }

    // 测试可以暂缓消费，以确定性验证满队列和关闭时的行为。
    // 正常构造传入已完成任务，立即开始等待请求。
    internal RoomExecutor(Room initialRoom, int commandQueueCapacity, Task startSignal, int queryQueueCapacity = MaxWaitingQueries)
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
            request.Reject(CreateStopException());
        }
        // 先占名额再入队。Interlocked 让多个提交方同时占位时也不会超过上限；超了就退回名额。
        else if (Interlocked.Increment(ref waiting) > limit)
        {
            Interlocked.Decrement(ref waiting);
            request.Reject(Volatile.Read(ref _stopping) != 0 ? CreateStopException() : new RoomBusyException());
        }
        // 名额已保证队列有空位，TryWrite 只会因为队列已关闭而失败。
        else if (!_queue.Writer.TryWrite(request))
        {
            Interlocked.Decrement(ref waiting);
            request.Reject(CreateStopException());
        }
        return request.result;
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
                request.Execute(_room);
            }
        }
        catch (Exception error)
        {
            // 不再向外抛：故障通过 故障原因 公开。若让任务以异常结束而无人等待，
            // 异常只会在垃圾回收时以“未观察的任务异常”出现，目录模块无从得知。
            Volatile.Write(ref _fault, error);
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
        }
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
        public abstract void Execute(Room currentRoom);
        public abstract void Reject(Exception reason);
    }

    private abstract class QueuedRequest<T> : QueuedRequest
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<T> result => _completion.Task;

        protected abstract T Run(Room currentRoom);

        public override void Execute(Room currentRoom)
        {
            try
            {
                _completion.TrySetResult(Run(currentRoom));
            }
            catch (RuleRejectedException error)
            {
                // 规则约定：抛出拒绝前没有改动状态，所以后续请求可以继续。
                Reject(error);
            }
            catch (Exception error)
            {
                // 其他异常无法确认状态是否改了一半，必须停止这个房间。
                Reject(error);
                throw;
            }
        }

        public override void Reject(Exception reason) => _completion.TrySetException(reason);
    }

    private sealed class CommandRequest(RoomCommand command) : QueuedRequest<CommandReceipt>
    {
        public override bool IsQuery => false;
        protected override CommandReceipt Run(Room currentRoom) => currentRoom.Execute(command);
    }

    // 查询不改变状态，不需要去重、记录或持久化，用委托表达就够了。
    private sealed class QueryRequest<T>(Func<Room, T> query) : QueuedRequest<T>
    {
        public override bool IsQuery => true;
        protected override T Run(Room currentRoom) => query(currentRoom);
    }
}
