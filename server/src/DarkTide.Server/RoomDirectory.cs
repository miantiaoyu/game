using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using DarkTide.Room;
using Microsoft.Extensions.Options;

namespace DarkTide.Server;

/// <summary>
/// 本进程拥有的全部房间：创建、查找、房间停止后摘除，以及进程退出时有界地停止所有房间。
///
/// 目前没有持久化：房间故障或进程退出，内存状态即丢失。故障房间会被摘除并记 Error 日志，
/// 不会自动重建——没有可信的状态来源，重建只会凭空造出一个空房间。
/// </summary>
public sealed class RoomDirectory : IHostedService
{
    public const string MeterName = "DarkTide.Server";

    private readonly ConcurrentDictionary<Guid, RoomExecutor> _rooms = new();
    private readonly RoomOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RoomDirectory> _logger;
    // 创建与“开始停止”用同一把锁：否则一个刚通过检查的创建可能在停止拍完快照后才登记，
    // 留下一个没人停止的房间，进程退出时它的排队请求会无声丢失。
    private readonly Lock _gate = new();
    private bool _stopping;

    public RoomDirectory(IOptions<RoomOptions> options, ILoggerFactory loggerFactory, IMeterFactory meterFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RoomDirectory>();

        // 用 IMeterFactory 而不是静态 Meter：同一进程里多个宿主（例如集成测试）各自独立，互不串数。
        var meter = meterFactory.Create(MeterName);
        meter.CreateObservableGauge("darktide.rooms.active", () => _rooms.Count,
            description: "本进程当前拥有的房间数");
        meter.CreateObservableGauge("darktide.rooms.waiting_commands", SumWaitingCommands,
            description: "所有房间排队中的命令总数，持续偏高说明消费跟不上");
    }

    public int Count => _rooms.Count;

    public bool IsStopping
    {
        get
        {
            lock (_gate)
            {
                return _stopping;
            }
        }
    }

    /// <summary>创建房间并开始处理。房间编号已存在或进程正在关闭时抛出。</summary>
    public RoomExecutor CreateRoom(Guid roomId, int seatCount)
    {
        lock (_gate)
        {
            if (_stopping)
            {
                throw new InvalidOperationException("进程正在关闭，不再创建房间。");
            }
            if (_rooms.ContainsKey(roomId))
            {
                throw new InvalidOperationException($"房间 {roomId} 已存在。");
            }

            // 构造失败（座位数非法等）时什么都没有登记。
            var executor = new RoomExecutor(roomId, seatCount, _options, _loggerFactory.CreateLogger<RoomExecutor>());
            _rooms[roomId] = executor;
            _ = RemoveWhenStoppedAsync(executor);
            _logger.LogInformation("Room {RoomId} created with {SeatCount} seats.", roomId, seatCount);
            return executor;
        }
    }

    public bool TryGetRoom(Guid roomId, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out RoomExecutor executor) =>
        _rooms.TryGetValue(roomId, out executor);

    /// <summary>停止并摘除一个房间。房间不存在时返回 null。</summary>
    public async Task<RoomStopResult?> CloseRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        if (!_rooms.TryGetValue(roomId, out var executor))
        {
            return null;
        }
        // 先停止再摘除（摘除由 RemoveWhenStoppedAsync 完成）：停止期间仍能被查到，避免同编号被重新创建、两个房间并存。
        return await executor.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// 进程退出时由宿主调用。所有房间并行停止，各自受 <see cref="RoomOptions.ShutdownTimeout"/> 限制；
    /// <paramref name="cancellationToken"/> 在宿主总时限到达时取消，作为第二道上限。
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        RoomExecutor[] rooms;
        lock (_gate)
        {
            _stopping = true;
            rooms = _rooms.Values.ToArray();
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

    private async Task RemoveWhenStoppedAsync(RoomExecutor executor)
    {
        // Stopped 按约定不会以异常结束。
        await executor.Stopped.ConfigureAwait(false);
        // 只摘除同一个实例，不误删之后用同编号新建的房间。
        _rooms.TryRemove(new KeyValuePair<Guid, RoomExecutor>(executor.RoomId, executor));

        if (executor.FaultReason is not null)
        {
            _logger.LogError(
                "Room {RoomId} faulted and was removed; its in-memory state is lost because persistence is not implemented yet.",
                executor.RoomId);
        }
        else
        {
            _logger.LogInformation("Room {RoomId} stopped and was removed.", executor.RoomId);
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
