using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using DarkTide.Room;
using Microsoft.Extensions.Options;

namespace DarkTide.Server;

/// <summary>
/// 本进程拥有的全部房间：创建、查找、关闭，进程启动时从日志恢复，退出时有界地停止所有房间。
///
/// 有日志（配置了数据库）时：
/// - 创建先在日志登记，成功才开始处理；
/// - 进程退出或房间故障时，房间在日志里保持“未关闭”，下次启动自动恢复；
/// - 只有显式 <see cref="CloseRoomAsync"/> 才把房间标记为已关闭。
/// 没有日志时只在内存运行，进程退出即丢失，启动时记一条警告。
/// </summary>
public sealed class RoomDirectory : IHostedService
{
    public const string MeterName = "DarkTide.Server";

    private readonly ConcurrentDictionary<Guid, RoomExecutor> _rooms = new();
    private readonly RoomOptions _options;
    private readonly IRoomJournal? _journal;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RoomDirectory> _logger;
    private readonly Counter<long> _restoreFailures;
    // 创建与“开始停止”串行化：否则一个刚通过检查的创建可能在停止拍完快照后才登记，
    // 留下一个没人停止的房间。创建需要等待日志写入，所以用信号量而不是 lock。
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _stopping;

    public RoomDirectory(IOptions<RoomOptions> options, ILoggerFactory loggerFactory, IMeterFactory meterFactory, IRoomJournal? journal = null)
    {
        _options = options.Value;
        _journal = journal;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RoomDirectory>();

        // 用 IMeterFactory 而不是静态 Meter：同一进程里多个宿主（例如集成测试）各自独立，互不串数。
        var meter = meterFactory.Create(MeterName);
        meter.CreateObservableGauge("darktide.rooms.active", () => _rooms.Count,
            description: "本进程当前拥有的房间数");
        meter.CreateObservableGauge("darktide.rooms.waiting_commands", SumWaitingCommands,
            description: "所有房间排队中的命令总数，持续偏高说明消费跟不上");
        _restoreFailures = meter.CreateCounter<long>("darktide.rooms.restore_failures",
            description: "启动时未能从日志恢复的房间数；非零需要人工核查");
    }

    public int Count => _rooms.Count;

    public bool IsStopping => _stopping;

    /// <summary>是否配置了持久日志。</summary>
    public bool IsPersistent => _journal is not null;

    /// <summary>创建房间并开始处理。房间编号已存在或进程正在关闭时抛 <see cref="InvalidOperationException"/>。</summary>
    public async Task<RoomExecutor> CreateRoomAsync(Guid roomId, int seatCount, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_stopping)
            {
                throw new InvalidOperationException("进程正在关闭，不再创建房间。");
            }
            if (_rooms.ContainsKey(roomId))
            {
                throw new InvalidOperationException($"房间 {roomId} 已存在。");
            }

            var logger = _loggerFactory.CreateLogger<RoomExecutor>();
            RoomExecutor executor;
            if (_journal is null)
            {
                executor = new RoomExecutor(roomId, seatCount, _options, logger);
            }
            else
            {
                try
                {
                    executor = await RoomExecutor.CreatePersistentAsync(roomId, seatCount, _journal, _options, logger, cancellationToken).ConfigureAwait(false);
                }
                catch (RoomAlreadyExistsException)
                {
                    // 日志里已有、内存里没有：多半是已关闭的旧房间。编号不复用（设计文档第 5 节）。
                    throw new InvalidOperationException($"房间 {roomId} 已存在。");
                }
            }

            Register(executor);
            _logger.LogInformation("Room {RoomId} created with {SeatCount} seats. Persistent: {Persistent}.", roomId, seatCount, IsPersistent);
            return executor;
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TryGetRoom(Guid roomId, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out RoomExecutor executor) =>
        _rooms.TryGetValue(roomId, out executor);

    /// <summary>
    /// 停止房间并在日志中标记为已关闭，之后启动时不再恢复。房间不存在时返回 null。
    /// 没能干净停止（有请求被放弃或仍在执行）时不标记关闭：房间仍可恢复，留给运维核查。
    /// </summary>
    public async Task<RoomStopResult?> CloseRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        if (!_rooms.TryGetValue(roomId, out var executor))
        {
            return null;
        }
        // 先停止再摘除（摘除由 RemoveWhenStoppedAsync 完成）：停止期间仍能被查到，避免同编号被重新创建、两个房间并存。
        var result = await executor.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_journal is not null && result.Drained)
        {
            await _journal.MarkClosedAsync(roomId, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>进程启动时由宿主调用：从日志恢复所有未关闭的房间。单个房间恢复失败只记错误，不阻止进程启动。</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_journal is null)
        {
            _logger.LogWarning("No database is configured; rooms live only in memory and are lost when the process exits.");
            return;
        }

        var roomIds = await _journal.ListOpenRoomsAsync(cancellationToken).ConfigureAwait(false);
        int restored = 0;
        foreach (var roomId in roomIds)
        {
            try
            {
                var executor = await RoomExecutor.RestoreAsync(roomId, _journal, _options, _loggerFactory.CreateLogger<RoomExecutor>(), cancellationToken).ConfigureAwait(false);
                Register(executor);
                restored++;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // 一个房间的日志与当前代码不一致（或读不出来），不应让整个服务起不来；
                // 房间在日志里保持未关闭，修复后重启即可恢复。
                _restoreFailures.Add(1);
                _logger.LogError(error, "Room {RoomId} could not be restored from the journal and was skipped; it stays open for investigation.", roomId);
            }
        }

        _logger.LogInformation("Restored {Restored} of {Open} open rooms from the journal.", restored, roomIds.Count);
    }

    /// <summary>
    /// 进程退出时由宿主调用。所有房间并行停止，各自受 <see cref="RoomOptions.ShutdownTimeout"/> 限制；
    /// <paramref name="cancellationToken"/> 在宿主总时限到达时取消，作为第二道上限。
    /// 不在日志中标记关闭：这些房间下次启动时恢复。
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        RoomExecutor[] rooms;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _stopping = true;
            rooms = _rooms.Values.ToArray();
        }
        finally
        {
            _gate.Release();
        }

        if (rooms.Length == 0)
        {
            _logger.LogInformation("Room directory stopped; no rooms were running.");
            return;
        }

        _logger.LogInformation("Stopping {RoomCount} rooms.", rooms.Length);
        var results = await Task.WhenAll(rooms.Select(room => room.StopAsync(cancellationToken))).ConfigureAwait(false);

        int notDrained = results.Count(result => !result.Drained);
        int abandoned = results.Sum(result => result.AbandonedRequests);
        int stillRunning = results.Count(result => !result.ConsumerStopped);
        _logger.Log(
            notDrained == 0 ? LogLevel.Information : LogLevel.Warning,
            "Room directory stopped {RoomCount} rooms: {NotDrained} did not drain cleanly, {AbandonedRequests} requests abandoned, {StillRunning} still executing.",
            rooms.Length, notDrained, abandoned, stillRunning);
    }

    private void Register(RoomExecutor executor)
    {
        _rooms[executor.RoomId] = executor;
        _ = RemoveWhenStoppedAsync(executor);
    }

    private async Task RemoveWhenStoppedAsync(RoomExecutor executor)
    {
        // Stopped 按约定不会以异常结束。
        await executor.Stopped.ConfigureAwait(false);
        // 只摘除同一个实例，不误删之后用同编号新建的房间。
        _rooms.TryRemove(new KeyValuePair<Guid, RoomExecutor>(executor.RoomId, executor));

        if (executor.FaultReason is null)
        {
            _logger.LogInformation("Room {RoomId} stopped and was removed.", executor.RoomId);
        }
        else if (_journal is not null)
        {
            _logger.LogError(
                "Room {RoomId} faulted and was removed; it stays open in the journal and will be restored on the next start.",
                executor.RoomId);
        }
        else
        {
            _logger.LogError(
                "Room {RoomId} faulted and was removed; its in-memory state is lost because no database is configured.",
                executor.RoomId);
        }
    }

    private int SumWaitingCommands()
    {
        int total = 0;
        foreach (var executor in _rooms.Values)
        {
            total += executor.WaitingCommandCount;
        }
        return total;
    }
}
