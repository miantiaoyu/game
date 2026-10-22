using System.Diagnostics.Metrics;
using DarkTide.Room;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DarkTide.Server.Tests;

public class RoomDirectoryTests
{
    private static (RoomDirectory Directory, ServiceProvider Services) CreateDirectory(RoomOptions? options = null, IRoomJournal? journal = null)
    {
        var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var directory = new RoomDirectory(
            Options.Create(options ?? new RoomOptions()),
            NullLoggerFactory.Instance,
            services.GetRequiredService<IMeterFactory>(),
            journal);
        return (directory, services);
    }

    [Fact]
    public async Task FinishedMatchIsClosedAfterRetentionAndNotRestored()
    {
        // 整场结束的房间要在保留期后关闭：否则内存与每次启动的重放量随历史房间数无限增长。
        var journal = new InMemoryRoomJournal();
        var options = new RoomOptions
        {
            TurnTimeout = TimeSpan.FromMilliseconds(1), NextHandDelay = TimeSpan.FromMilliseconds(1),
            FinishedRoomRetention = TimeSpan.FromMilliseconds(50),
        };
        var (directory, services) = CreateDirectory(options, journal);
        await using var _ = services;
        await directory.StartAsync(CancellationToken.None);

        var room = await directory.CreateRoomAsync(Guid.NewGuid(), 2);
        await room.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Guid.NewGuid(), 0));
        await room.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Guid.NewGuid(), 1));
        await room.StartMatchAsync();

        // 每个行动 1 毫秒超时：无人操作的整场很快打完，然后 50 毫秒后关闭。
        await room.MatchFinished.WaitAsync(TimeSpan.FromSeconds(30));
        await room.Stopped.WaitAsync(TimeSpan.FromSeconds(10));
        // 停止后先从目录摘除，随后才在日志里标记关闭；两步之间崩溃无害（下次启动恢复后会再关一次）。所以这里等日志。
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while ((await journal.ListOpenRoomsAsync(CancellationToken.None)).Contains(room.RoomId) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.DoesNotContain(room.RoomId, await journal.ListOpenRoomsAsync(CancellationToken.None));
        Assert.False(directory.TryGetRoom(room.RoomId, out var _));
        await directory.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OpenRoomsAreRestoredOnStartAndClosedRoomsAreNot()
    {
        var journal = new InMemoryRoomJournal();
        var player = Guid.NewGuid();
        Guid keptRoom, closedRoom;
        {
            var (first, services) = CreateDirectory(journal: journal);
            await using var _ = services;
            await first.StartAsync(CancellationToken.None);
            var kept = await first.CreateRoomAsync(Guid.NewGuid(), 5);
            await kept.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), player, 3));
            keptRoom = kept.RoomId;
            closedRoom = (await first.CreateRoomAsync(Guid.NewGuid(), 5)).RoomId;
            await first.CloseRoomAsync(closedRoom);
            // 进程退出：停止所有房间，但不标记关闭。
            await first.StopAsync(CancellationToken.None);
        }

        var (second, services2) = CreateDirectory(journal: journal);
        await using var __ = services2;
        await second.StartAsync(CancellationToken.None);

        Assert.True(second.TryGetRoom(keptRoom, out var restored));
        Assert.Equal<int?>(3, await restored.FindSeatAsync(player));
        Assert.False(second.TryGetRoom(closedRoom, out var ___));
        // 已关闭房间的编号不复用。
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.CreateRoomAsync(closedRoom, 5));
        await second.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RoomsFromAnIncompatibleRulesVersionAreSkippedAndStayOpen()
    {
        var journal = new InMemoryRoomJournal();
        var current = Guid.NewGuid();
        var future = Guid.NewGuid();
        await journal.CreateRoomAsync(current, 5, null, CancellationToken.None);
        journal.CreateRoomWithVersionForTest(future, 5, RulesVersion.Current + 1);

        var (directory, services) = CreateDirectory(journal: journal);
        await using var _ = services;
        await directory.StartAsync(CancellationToken.None);

        Assert.True(directory.TryGetRoom(current, out var __));
        Assert.False(directory.TryGetRoom(future, out var ___));
        // 不关闭：用兼容的版本启动时还能接着打完。
        Assert.Contains(future, await journal.ListOpenRoomsAsync(CancellationToken.None));
        await directory.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnrestorableRoomIsSkippedWithoutBlockingStartup()
    {
        var journal = new InMemoryRoomJournal();
        var good = Guid.NewGuid();
        var bad = Guid.NewGuid();
        await journal.CreateRoomAsync(good, 5, null, CancellationToken.None);
        await journal.CreateRoomAsync(bad, 5, null, CancellationToken.None);
        // 日志声称越界入座成功：与规则不一致，重放会拒绝加载。
        await journal.AppendAsync(bad, new JournalEntry(1, new TakeSeatCommand(Guid.NewGuid(), Guid.NewGuid(), 9), CommandOutcome.Applied(1)), CancellationToken.None);

        var (directory, services) = CreateDirectory(journal: journal);
        await using var _ = services;
        await directory.StartAsync(CancellationToken.None);

        Assert.True(directory.TryGetRoom(good, out var __));
        Assert.False(directory.TryGetRoom(bad, out var ___));
        // 坏房间保持未关闭，修复后重启还能恢复。
        Assert.Contains(bad, await journal.ListOpenRoomsAsync(CancellationToken.None));
        await directory.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CreatedRoomCanBeFoundAndUsed()
    {
        var (directory, services) = CreateDirectory();
        await using var _ = services;
        var roomId = Guid.NewGuid();

        var created = await directory.CreateRoomAsync(roomId, 5);

        Assert.True(directory.TryGetRoom(roomId, out var found));
        Assert.Same(created, found);
        await found.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Guid.NewGuid(), 2));
        await directory.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DuplicateRoomIdIsRejectedAndInvalidRoomIsNotRegistered()
    {
        var (directory, services) = CreateDirectory();
        await using var _ = services;
        var roomId = Guid.NewGuid();
        await directory.CreateRoomAsync(roomId, 5);

        await Assert.ThrowsAsync<InvalidOperationException>(() => directory.CreateRoomAsync(roomId, 5));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => directory.CreateRoomAsync(Guid.NewGuid(), 99));
        Assert.Equal(1, directory.Count);
        await directory.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ClosedRoomIsRemoved()
    {
        var (directory, services) = CreateDirectory();
        await using var _ = services;
        var roomId = Guid.NewGuid();
        var executor = await directory.CreateRoomAsync(roomId, 5);

        var result = await directory.CloseRoomAsync(roomId);

        Assert.NotNull(result);
        Assert.True(result.Drained);
        await WaitUntilAsync(() => directory.Count == 0);
        Assert.False(directory.TryGetRoom(roomId, out var __));
        Assert.Null(await directory.CloseRoomAsync(roomId));

        // 摘除后可以用同一编号重新创建（例如运维手工恢复）。
        await directory.CreateRoomAsync(roomId, 5);
        Assert.Equal(1, directory.Count);
        Assert.NotSame(executor, directory.TryGetRoom(roomId, out var again) ? again : null);
        await directory.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FaultedRoomIsRemovedAutomatically()
    {
        var (directory, services) = CreateDirectory();
        await using var _ = services;
        var executor = await directory.CreateRoomAsync(Guid.NewGuid(), 5);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.RunInLoopForTestAsync<int>(_ => throw new InvalidOperationException("模拟缺陷")));

        await WaitUntilAsync(() => directory.Count == 0);
        Assert.NotNull(executor.FaultReason);
    }

    [Fact]
    public async Task StopIsBoundedEvenWhenARoomIsStuck()
    {
        var (directory, services) = CreateDirectory(new RoomOptions { ShutdownTimeout = TimeSpan.FromMilliseconds(200) });
        await using var _ = services;
        var stuckRoom = await directory.CreateRoomAsync(Guid.NewGuid(), 5);
        var healthyRoom = await directory.CreateRoomAsync(Guid.NewGuid(), 5);
        var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var stuck = stuckRoom.RunInLoopForTestAsync(_ =>
            {
                entered.SetResult(true);
                release.Wait();
                return 0;
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // 所有房间并行停止：一个卡住的房间不会让整体等待时间叠加。
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await directory.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            watch.Stop();

            Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
            await healthyRoom.Stopped.WaitAsync(TimeSpan.FromSeconds(1));
            release.Set();
            await stuck;
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task ConcurrentCreatesAndStopNeverLeaveAnUnstoppedRoom()
    {
        for (int round = 0; round < 20; round++)
        {
            var (directory, services) = CreateDirectory();
            await using var _ = services;
            var created = new System.Collections.Concurrent.ConcurrentBag<RoomExecutor>();
            using var go = new ManualResetEventSlim(false);

            var creators = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                go.Wait();
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        created.Add(directory.CreateRoomAsync(Guid.NewGuid(), 4).GetAwaiter().GetResult());
                    }
                    catch (InvalidOperationException)
                    {
                        // 关闭之后的创建被拒绝，这是预期的。
                    }
                }
            })).ToArray();
            var stopper = Task.Run(async () =>
            {
                go.Wait();
                await directory.StopAsync(CancellationToken.None);
            });

            go.Set();
            await Task.WhenAll(creators.Append(stopper)).WaitAsync(TimeSpan.FromSeconds(10));

            // 每个成功创建的房间都必须被停止，否则进程退出时它的请求会无声丢失。
            foreach (var room in created)
            {
                await room.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("条件在 5 秒内没有满足。");
            }
            await Task.Delay(10);
        }
    }
}
