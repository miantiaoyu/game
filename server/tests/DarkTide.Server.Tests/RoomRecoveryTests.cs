using System.Diagnostics.Metrics;
using DarkTide.Room;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DarkTide.Server.Tests;

/// <summary>持久化故障后的自动重建：重建成功、带退避重试、不该重建的不重建、反复故障时放弃、与关闭的竞争。</summary>
public class RoomRecoveryTests
{
    private static readonly Guid Alice = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Bob = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    // 测试用的短间隔：真实默认值是 1 秒起步、最长 60 秒。
    private static readonly RoomRecoveryPolicy Fast = new(
        TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(80), MaxFaultsInWindow: 3, FaultWindow: TimeSpan.FromMinutes(1));

    private static (RoomDirectory Directory, ServiceProvider Services) Create(InMemoryRoomJournal journal, RoomRecoveryPolicy? policy = null)
    {
        var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var directory = new RoomDirectory(
            Options.Create(new RoomOptions()),
            NullLoggerFactory.Instance,
            services.GetRequiredService<IMeterFactory>(),
            journal,
            policy ?? Fast);
        return (directory, services);
    }

    [Fact]
    public async Task RoomIsRebuiltAfterAJournalFailureWithItsStateIntact()
    {
        var journal = new InMemoryRoomJournal();
        var (directory, services) = Create(journal);
        await using var _ = services;
        var roomId = Guid.NewGuid();
        var room = await directory.CreateRoomAsync(roomId, 5);
        await room.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 2));
        var lost = new TakeSeatCommand(Guid.NewGuid(), Bob, 3);

        journal.FailNextAppend = new IOException("模拟数据库闪断");
        await Assert.ThrowsAsync<RoomPersistenceException>(() => room.ExecuteAsync(lost));

        // 不需要重启进程：房间自动从日志重建，已确认的状态都在。
        var rebuilt = await WaitForRoomAsync(directory, roomId, notSameAs: room);
        Assert.Equal<int?>(2, await rebuilt.FindSeatAsync(Alice));
        // 没写进日志的那条不存在；用同一编号重试是安全的。
        Assert.Null(await rebuilt.FindSeatAsync(Bob));
        await rebuilt.ExecuteAsync(lost);
        Assert.Equal<int?>(3, await rebuilt.FindSeatAsync(Bob));
        Assert.Equal(0, directory.RecoveringCount);
        await directory.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RebuildRetriesWithBackoffWhileTheDatabaseIsStillDown()
    {
        var journal = new InMemoryRoomJournal();
        var (directory, services) = Create(journal);
        await using var _ = services;
        var roomId = Guid.NewGuid();
        var room = await directory.CreateRoomAsync(roomId, 5);

        // 数据库闪断后还要过一会儿才恢复：前三次加载都失败。
        journal.FailNextLoads = 3;
        journal.FailNextAppend = new IOException("模拟数据库断开");
        await Assert.ThrowsAsync<RoomPersistenceException>(() => room.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 0)));

        await WaitForRoomAsync(directory, roomId, notSameAs: room);
        Assert.Equal(4, journal.LoadCount);
        await directory.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RuleCodeFaultsAreNotRebuiltAutomatically()
    {
        var journal = new InMemoryRoomJournal();
        var (directory, services) = Create(journal);
        await using var _ = services;
        var roomId = Guid.NewGuid();
        var room = await directory.CreateRoomAsync(roomId, 5);

        // 规则代码的意外异常：同一命令重发会再次触发同一缺陷，自动重建只会让全桌反复掉线。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => room.RunInLoopForTestAsync<int>(_ => throw new InvalidOperationException("模拟规则缺陷")));
        await room.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        Assert.False(directory.TryGetRoom(roomId, out var __));
        Assert.False(directory.IsRecovering(roomId));
        Assert.Equal(0, journal.LoadCount);
        // 日志里仍未关闭：修复后重启进程即可恢复。
        Assert.Contains(roomId, await journal.ListOpenRoomsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RepeatedFaultsStopAutomaticRebuild()
    {
        var journal = new InMemoryRoomJournal();
        var (directory, services) = Create(journal);
        await using var _ = services;
        var roomId = Guid.NewGuid();
        var room = await directory.CreateRoomAsync(roomId, 5);

        // 策略允许 1 分钟内 3 次；第 4 次故障后不再重建。
        for (int fault = 1; fault <= 4; fault++)
        {
            journal.FailNextAppend = new IOException($"第 {fault} 次故障");
            await Assert.ThrowsAsync<RoomPersistenceException>(() => room.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Guid.NewGuid(), 0)));
            if (fault < 4)
            {
                room = await WaitForRoomAsync(directory, roomId, notSameAs: room);
            }
        }

        await room.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        Assert.False(directory.TryGetRoom(roomId, out var __));
        Assert.False(directory.IsRecovering(roomId));
        Assert.Contains(roomId, await journal.ListOpenRoomsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RoomIdStaysReservedWhileRecovering()
    {
        var journal = new InMemoryRoomJournal();
        var slow = Fast with { InitialDelay = TimeSpan.FromSeconds(30) };
        var (directory, services) = Create(journal, slow);
        await using var _ = services;
        var roomId = Guid.NewGuid();
        var room = await directory.CreateRoomAsync(roomId, 5);

        journal.FailNextAppend = new IOException("模拟数据库断开");
        await Assert.ThrowsAsync<RoomPersistenceException>(() => room.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 0)));
        await WaitUntilAsync(() => directory.IsRecovering(roomId));

        // 重建期间查不到房间，但编号不能被同编号新建占走，否则重建完成后两个房间并存。
        Assert.False(directory.TryGetRoom(roomId, out var __));
        await Assert.ThrowsAsync<InvalidOperationException>(() => directory.CreateRoomAsync(roomId, 5));
        await directory.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ShutdownCancelsPendingRebuildsPromptly()
    {
        var journal = new InMemoryRoomJournal();
        var slow = Fast with { InitialDelay = TimeSpan.FromSeconds(30) };
        var (directory, services) = Create(journal, slow);
        await using var _ = services;
        var roomId = Guid.NewGuid();
        var room = await directory.CreateRoomAsync(roomId, 5);
        journal.FailNextAppend = new IOException("模拟数据库断开");
        await Assert.ThrowsAsync<RoomPersistenceException>(() => room.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 0)));
        await WaitUntilAsync(() => directory.IsRecovering(roomId));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await directory.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // 不等 30 秒的退避：关闭立即取消重建，之后也不会有房间被登记进来。
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        Assert.Equal(0, directory.RecoveringCount);
        Assert.Equal(0, directory.Count);
        Assert.Contains(roomId, await journal.ListOpenRoomsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task JournalThatNoLongerReplaysIsNotRetriedForever()
    {
        var journal = new InMemoryRoomJournal();
        var policy = Fast with { InitialDelay = TimeSpan.FromMilliseconds(300) };
        var (directory, services) = Create(journal, policy);
        await using var _ = services;
        var roomId = Guid.NewGuid();
        var room = await directory.CreateRoomAsync(roomId, 5);
        journal.FailNextAppend = new IOException("模拟数据库断开");
        await Assert.ThrowsAsync<RoomPersistenceException>(() => room.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 0)));

        // 等待重建期间，日志里多了一条与规则不符的记录（越界入座却记为成功）。
        await journal.AppendAsync(roomId, new JournalEntry(1, new TakeSeatCommand(Guid.NewGuid(), Bob, 9), CommandOutcome.Applied(1)), CancellationToken.None);

        await WaitUntilAsync(() => !directory.IsRecovering(roomId), TimeSpan.FromSeconds(5));
        int loads = journal.LoadCount;
        await Task.Delay(300);
        // 重放不一致不是暂时问题：放弃后不再反复加载。
        Assert.Equal(loads, journal.LoadCount);
        Assert.False(directory.TryGetRoom(roomId, out var __));
    }

    private static async Task<RoomExecutor> WaitForRoomAsync(RoomDirectory directory, Guid roomId, RoomExecutor notSameAs)
    {
        RoomExecutor? found = null;
        await WaitUntilAsync(() => directory.TryGetRoom(roomId, out found) && !ReferenceEquals(found, notSameAs));
        return found!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("条件没有在时限内满足。");
            }
            await Task.Delay(10);
        }
    }
}
