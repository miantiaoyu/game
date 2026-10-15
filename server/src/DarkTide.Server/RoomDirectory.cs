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
/// - 持久化故障（数据库闪断、超时）的房间按 <see cref="RoomRecoveryPolicy"/> 在进程内带退避地自动重建；
///   重建期间房间编号仍被占用，查找返回“没有这个房间”，调用方稍后重试；
/// - 只有显式 <see cref="CloseRoomAsync"/> 才把房间标记为已关闭。
/// 没有日志时只在内存运行，进程退出即丢失，启动时记一条警告。
/// </summary>
public sealed class RoomDirectory : IHostedService
{
    public const string MeterName = "DarkTide.Server";

    /// <summary>
    /// 启动时同时恢复的房间数。实测（1000 房间 × 360 条命令，本机 PostgreSQL）：逐个恢复 3.6 s，并发 4 为 1.1 s，
    /// 并发 16 为 0.8 s，再往上收益很小。取 8：多数时间花在等数据库，又不至于在启动时占满连接池。
    /// </summary>
    public const int RestoreParallelism = 8;

    private readonly ConcurrentDictionary<Guid, RoomExecutor> _rooms = new();
    private readonly RoomOptions _options;
    private readonly IRoomJournal? _journal;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RoomDirectory> _logger;
    private readonly Counter<long> _restoreFailures;
    private readonly Counter<long> _recoveries;
    private readonly RoomRecoveryPolicy _recoveryPolicy;
    private readonly TimeProvider _time;
    // 正在等待自动重建的房间：编号仍被占用，防止重建期间被同编号新建，两个房间并存。
    private readonly ConcurrentDictionary<Guid, Task> _recovering = new();
    // 每个房间最近的故障时间，用于判断是否“短时间内反复故障”。只在重建循环里按房间串行访问。
    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _faultHistory = new();
    // 停止时取消所有等待中的重建。
    private readonly CancellationTokenSource _shutdown = new();
    // 创建与“开始停止”串行化：否则一个刚通过检查的创建可能在停止拍完快照后才登记，
    // 留下一个没人停止的房间。创建需要等待日志写入，所以用信号量而不是 lock。
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _stopping;

    public RoomDirectory(
        IOptions<RoomOptions> options,
        ILoggerFactory loggerFactory,
        IMeterFactory meterFactory,
        IRoomJournal? journal = null,
        RoomRecoveryPolicy? recoveryPolicy = null,
        TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _journal = journal;
        _recoveryPolicy = recoveryPolicy ?? RoomRecoveryPolicy.Default;
        _time = timeProvider ?? TimeProvider.System;
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
        _recoveries = meter.CreateCounter<long>("darktide.rooms.recoveries",
            description: "故障房间的自动重建，outcome 为 succeeded／retrying／gave_up／not_eligible");
        meter.CreateObservableGauge("darktide.rooms.recovering", () => _recovering.Count,
            description: "正在等待自动重建的房间数；持续非零说明数据库一直不可用");
    }

    public int Count => _rooms.Count;

    public bool IsStopping => _stopping;

    /// <summary>是否配置了持久日志。</summary>
    public bool IsPersistent => _journal is not null;

    /// <summary>正在等待自动重建的房间数。</summary>
    public int RecoveringCount => _recovering.Count;

    /// <summary>房间是否正在自动重建。重建期间 <see cref="TryGetRoom"/> 找不到它，但编号不能被新建占用。</summary>
    public bool IsRecovering(Guid roomId) => _recovering.ContainsKey(roomId);

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
            if (_rooms.ContainsKey(roomId) || _recovering.ContainsKey(roomId))
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

        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        // 先按版本统计：部署了不兼容的版本、还有旧房间没结束，一条日志就能看出来，而不是逐个房间重放失败。
        var byVersion = await _journal.CountOpenRoomsByRulesVersionAsync(cancellationToken).ConfigureAwait(false);
        int incompatible = byVersion.Where(pair => !RulesVersion.CanReplay(pair.Key)).Sum(pair => pair.Value);
        if (incompatible > 0)
        {
            _logger.LogCritical(
                "{Incompatible} open rooms were created by rules versions this build cannot replay ({Versions}; this build is {Current}). They are skipped and stay open; finish them with a compatible build.",
                incompatible,
                string.Join(", ", byVersion.Where(pair => !RulesVersion.CanReplay(pair.Key)).Select(pair => $"v{pair.Key}: {pair.Value}")),
                RulesVersion.Current);
        }

        var roomIds = await _journal.ListOpenRoomsAsync(cancellationToken).ConfigureAwait(false);
        int restored = 0;
        // 恢复完成之前 Web 服务器还没开始监听，存活检查也访问不到：房间多时要并行，部署时还应配启动探针。
        await Parallel.ForEachAsync(roomIds, new ParallelOptions
        {
            MaxDegreeOfParallelism = RestoreParallelism,
            CancellationToken = cancellationToken
        }, async (roomId, token) =>
        {
            try
            {
                var executor = await RoomExecutor.RestoreAsync(roomId, _journal, _options, _loggerFactory.CreateLogger<RoomExecutor>(), token).ConfigureAwait(false);
                Register(executor);
                Interlocked.Increment(ref restored);
            }
            catch (RulesVersionMismatchException)
            {
                // 上面已经汇总报告过，不再逐个房间写错误日志。
                _restoreFailures.Add(1, new KeyValuePair<string, object?>("reason", "rules_version"));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // 一个房间的日志与当前代码不一致（或读不出来），不应让整个服务起不来；
                // 房间在日志里保持未关闭，修复后重启即可恢复。
                _restoreFailures.Add(1, new KeyValuePair<string, object?>("reason", "replay"));
                _logger.LogError(error, "Room {RoomId} could not be restored from the journal and was skipped; it stays open for investigation.", roomId);
            }
        }).ConfigureAwait(false);

        _logger.LogInformation("Restored {Restored} of {Open} open rooms from the journal in {ElapsedMs:F0} ms.",
            restored, roomIds.Count, System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
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

        // 取消等待中的重建并等它们退出：之后不会再有房间被登记进来。
        // 这些房间在日志里仍是未关闭，下次启动时恢复。
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(_recovering.Values).ConfigureAwait(false);

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
            StartRecovery(executor.RoomId, executor.FaultReason);
        }
        else
        {
            _logger.LogError(
                "Room {RoomId} faulted and was removed; its in-memory state is lost because no database is configured.",
                executor.RoomId);
        }
    }

    /// <summary>
    /// 决定故障房间是否自动重建，并在需要时启动重建循环。
    /// 在目录的停止锁之外调用：重建循环自己会在登记前检查是否正在停止。
    /// </summary>
    private void StartRecovery(Guid roomId, Exception fault)
    {
        if (fault is not RoomPersistenceException)
        {
            _recoveries.Add(1, new KeyValuePair<string, object?>("outcome", "not_eligible"));
            _logger.LogError(fault,
                "Room {RoomId} faulted on an unexpected error and will not be rebuilt automatically; it stays open in the journal for investigation and is restored on the next start.",
                roomId);
            return;
        }
        if (_stopping)
        {
            return;
        }

        var history = _faultHistory.GetOrAdd(roomId, _ => new Queue<DateTimeOffset>());
        var now = _time.GetUtcNow();
        lock (history)
        {
            history.Enqueue(now);
            while (history.Count > 0 && now - history.Peek() > _recoveryPolicy.FaultWindow)
            {
                history.Dequeue();
            }
            if (history.Count > _recoveryPolicy.MaxFaultsInWindow)
            {
                _recoveries.Add(1, new KeyValuePair<string, object?>("outcome", "gave_up"));
                _logger.LogCritical(
                    "Room {RoomId} faulted {FaultCount} times within {FaultWindow}; automatic rebuild stopped. It stays open in the journal and needs manual attention.",
                    roomId, history.Count, _recoveryPolicy.FaultWindow);
                return;
            }
        }

        // 先占住编号再启动循环：TryAdd 失败说明已有重建在进行（不应发生，作为防御）。
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_recovering.TryAdd(roomId, started.Task))
        {
            return;
        }
        _logger.LogWarning(fault,
            "Room {RoomId} stopped after a persistence failure; rebuilding it from the journal with backoff.", roomId);
        _ = RecoverAsync(roomId, started);
    }

    private async Task RecoverAsync(Guid roomId, TaskCompletionSource done)
    {
        // 只移除本次重建登记的那一项：重建成功的房间可能马上再次故障并登记新的重建，不能误删它。
        var ownEntry = new KeyValuePair<Guid, Task>(roomId, done.Task);
        var delay = _recoveryPolicy.InitialDelay;
        int attempt = 0;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(delay, _time, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                attempt++;
                RoomExecutor executor;
                try
                {
                    // 一次重建尝试的时限：数据库停顿时加载会和写入一样挂住，不能让重建循环卡在一次尝试里。
                    // 加载一整场的记录比单条写入慢，给 4 倍日志时限。
                    var attemptTimeout = _options.JournalTimeout * 4;
                    using var attemptToken = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    attemptToken.CancelAfter(attemptTimeout);
                    executor = await RoomExecutor.RestoreAsync(roomId, _journal!, _options,
                            _loggerFactory.CreateLogger<RoomExecutor>(), attemptToken.Token)
                        .WaitAsync(attemptTimeout, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error) when (error is JournalReplayMismatchException or RulesVersionMismatchException)
                {
                    // 日志与代码不一致不是暂时问题，重试不会好转。
                    _recoveries.Add(1, new KeyValuePair<string, object?>("outcome", "gave_up"));
                    _logger.LogCritical(error, "Room {RoomId} cannot be rebuilt: its journal does not replay; automatic rebuild stopped.", roomId);
                    return;
                }
                catch (Exception error)
                {
                    _recoveries.Add(1, new KeyValuePair<string, object?>("outcome", "retrying"));
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _recoveryPolicy.MaxDelay.Ticks));
                    _logger.LogWarning(error, "Rebuilding room {RoomId} failed (attempt {Attempt}); retrying in {Delay}.", roomId, attempt, delay);
                    continue;
                }

                // 与 StopAsync 竞争：在锁内确认未停止才登记，否则这个新执行器无人停止。
                // 故障时可能还有一条写入在数据库里没结束（超时取消不等于没提交）。若它在加载之后才提交，
                // 新执行器下一次追加会因序号冲突失败，再次触发重建并读到它：最终收敛到数据库里的真实结果。
                await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (_stopping)
                    {
                        await executor.StopAsync(CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                    _recovering.TryRemove(ownEntry);
                    Register(executor);
                }
                finally
                {
                    _gate.Release();
                }

                _recoveries.Add(1, new KeyValuePair<string, object?>("outcome", "succeeded"));
                _logger.LogInformation("Room {RoomId} was rebuilt from the journal after {Attempt} attempt(s).", roomId, attempt);
                return;
            }
        }
        finally
        {
            _recovering.TryRemove(ownEntry);
            done.TrySetResult();
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
