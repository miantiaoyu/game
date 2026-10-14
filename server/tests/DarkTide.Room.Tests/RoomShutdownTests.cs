using DarkTide.Rules;
using Microsoft.Extensions.Logging;

namespace DarkTide.Room.Tests;

public class RoomShutdownTests
{
    private static TakeSeatCommand Seat(int seat) => new(Guid.NewGuid(), Guid.NewGuid(), seat);

    [Fact]
    public async Task CleanStopDrainsEverythingAndReportsIt()
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(), signal.Task);
        var pending = new[] { executor.ExecuteAsync(Seat(0)), executor.ExecuteAsync(Seat(1)) };

        var stopping = executor.StopAsync();
        signal.SetResult(true);
        var result = await stopping;

        Assert.True(result.Drained);
        Assert.True(result.ConsumerStopped);
        Assert.Equal(0, result.AbandonedRequests);
        await Task.WhenAll(pending);
    }

    [Fact]
    public async Task StuckRequestDoesNotBlockStopBeyondTimeout()
    {
        var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new RecordingLogger();
        var executor = new RoomExecutor(Guid.NewGuid(), 8, TestOptions.With(shutdownTimeout: TimeSpan.FromMilliseconds(200)), logger);
        try
        {
            // 第一条请求在消费循环里卡住，后面两条排队等待。
            var stuck = executor.RunInLoopForTestAsync(_ =>
            {
                entered.SetResult(true);
                release.Wait();
                return 0;
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queuedCommand = executor.ExecuteAsync(Seat(0));
            var queuedQuery = executor.FindSeatAsync(Guid.NewGuid());

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await executor.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            watch.Stop();

            // 关闭不能无限等待：时限一到就返回。
            Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(3));
            Assert.False(result.Drained);
            Assert.False(result.ConsumerStopped);
            Assert.Equal(2, result.AbandonedRequests);

            // 被放弃的请求没有执行，并立即得到明确结果，而不是永远挂起。
            await Assert.ThrowsAsync<ObjectDisposedException>(() => queuedCommand.WaitAsync(TimeSpan.FromSeconds(1)));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => queuedQuery.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(0, executor.WaitingCommandCount);
            Assert.Equal(0, executor.WaitingQueryCount);

            // 卡住的那一条照常做完；之后消费循环退出，房间不再执行任何东西。
            release.Set();
            Assert.Equal(0, await stuck.WaitAsync(TimeSpan.FromSeconds(5)));
            await executor.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
            var final = await executor.StopAsync();
            Assert.True(final.ConsumerStopped);
            Assert.Equal(2, final.AbandonedRequests);

            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("did not drain"));
            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("do not transfer ownership"));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task AbandonedCommandsAreNeverExecutedLater()
    {
        var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new RoomExecutor(Guid.NewGuid(), 8, TestOptions.With(shutdownTimeout: TimeSpan.FromMilliseconds(100)));
        var player = Guid.NewGuid();
        try
        {
            var stuck = executor.RunInLoopForTestAsync(_ =>
            {
                entered.SetResult(true);
                release.Wait();
                return 0;
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var abandoned = executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), player, 4));

            await executor.StopAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => abandoned);

            // 卡住的请求结束后，用它暴露房间状态：被放弃的入座命令确实没有生效。
            release.Set();
            await stuck;
            await executor.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(executor.RoomForTest.FindSeat(player));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task CancellationAbandonsQueueImmediately()
    {
        var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 时限设得很长，确认是取消而不是超时让它返回。
        var executor = new RoomExecutor(Guid.NewGuid(), 8, TestOptions.With(shutdownTimeout: TimeSpan.FromMinutes(1)));
        try
        {
            var stuck = executor.RunInLoopForTestAsync(_ =>
            {
                entered.SetResult(true);
                release.Wait();
                return 0;
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = executor.ExecuteAsync(Seat(1));

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var result = await executor.StopAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, result.AbandonedRequests);
            Assert.False(result.Drained);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
            release.Set();
            await stuck;
        }
        finally
        {
            release.Set();
            await executor.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ZeroTimeoutAbandonsQueuedRequestsWithoutWaiting()
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(shutdownTimeout: TimeSpan.Zero), signal.Task);
        var queued = new[] { executor.ExecuteAsync(Seat(0)), executor.ExecuteAsync(Seat(1)) };

        var result = await executor.StopAsync();

        Assert.Equal(2, result.AbandonedRequests);
        foreach (var request in queued)
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => request);
        }

        // 消费循环之后才启动：它必须看到放弃标志，不再执行任何请求。
        signal.SetResult(true);
        await executor.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await executor.StopAsync()).ConsumerStopped);
    }

    [Fact]
    public async Task RepeatedAndConcurrentStopsAreSafe()
    {
        var executor = new RoomExecutor(Guid.NewGuid(), 8);
        await executor.ExecuteAsync(Seat(0));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => executor.StopAsync()));
        await executor.DisposeAsync();

        Assert.All(results, result => Assert.True(result.Drained));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => executor.ExecuteAsync(Seat(1)));
    }

    [Fact]
    public async Task FaultedRoomReportsNotDrained()
    {
        var executor = new RoomExecutor(Guid.NewGuid(), 8);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.RunInLoopForTestAsync<int>(_ => throw new InvalidOperationException("模拟缺陷")));

        var result = await executor.StopAsync();

        Assert.False(result.Drained);
        Assert.True(result.ConsumerStopped);
        Assert.NotNull(executor.FaultReason);
    }
}
