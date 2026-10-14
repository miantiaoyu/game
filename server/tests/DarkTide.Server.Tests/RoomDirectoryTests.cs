using System.Diagnostics.Metrics;
using DarkTide.Room;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DarkTide.Server.Tests;

public class RoomDirectoryTests
{
    private static (RoomDirectory Directory, ServiceProvider Services) CreateDirectory(RoomOptions? options = null)
    {
        var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var directory = new RoomDirectory(
            Options.Create(options ?? new RoomOptions()),
            NullLoggerFactory.Instance,
            services.GetRequiredService<IMeterFactory>());
        return (directory, services);
    }

    [Fact]
    public async Task CreatedRoomCanBeFoundAndUsed()
    {
        var (directory, services) = CreateDirectory();
        await using var _ = services;
        var roomId = Guid.NewGuid();

        var created = directory.CreateRoom(roomId, 5);

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
        directory.CreateRoom(roomId, 5);

        Assert.Throws<InvalidOperationException>(() => directory.CreateRoom(roomId, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => directory.CreateRoom(Guid.NewGuid(), 99));
        Assert.Equal(1, directory.Count);
        await directory.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ClosedRoomIsRemoved()
    {
        var (directory, services) = CreateDirectory();
        await using var _ = services;
        var roomId = Guid.NewGuid();
        var executor = directory.CreateRoom(roomId, 5);

        var result = await directory.CloseRoomAsync(roomId);

        Assert.NotNull(result);
        Assert.True(result.Drained);
        await WaitUntilAsync(() => directory.Count == 0);
        Assert.False(directory.TryGetRoom(roomId, out var __));
        Assert.Null(await directory.CloseRoomAsync(roomId));

        // 摘除后可以用同一编号重新创建（例如运维手工恢复）。
        directory.CreateRoom(roomId, 5);
        Assert.Equal(1, directory.Count);
        Assert.NotSame(executor, directory.TryGetRoom(roomId, out var again) ? again : null);
        await directory.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FaultedRoomIsRemovedAutomatically()
    {
        var (directory, services) = CreateDirectory();
        await using var _ = services;
        var executor = directory.CreateRoom(Guid.NewGuid(), 5);

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
        var stuckRoom = directory.CreateRoom(Guid.NewGuid(), 5);
        var healthyRoom = directory.CreateRoom(Guid.NewGuid(), 5);
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
                        created.Add(directory.CreateRoom(Guid.NewGuid(), 4));
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
