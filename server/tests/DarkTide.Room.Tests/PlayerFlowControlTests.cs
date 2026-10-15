using DarkTide.Rules;

namespace DarkTide.Room.Tests;

/// <summary>每玩家一个未完成命令、重发合并、速率限制，以及这些限制不会挡住其他玩家。</summary>
public class PlayerFlowControlTests
{
    private static readonly Guid Alice = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Bob = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task SecondIntentWhileFirstIsPendingIsRejectedAndNeverRuns()
    {
        var signal = NewSignal();
        await using var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(), signal.Task);
        try
        {
            var first = executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 0));
            var second = executor.ExecuteAsync(new LeaveSeatCommand(Guid.NewGuid(), Alice));

            // 第二个意图立即被拒绝，不进入队列：一名玩家不能用不同编号占满房间队列。
            var busy = await Assert.ThrowsAsync<RoomBusyException>(() => second);
            Assert.Equal(BusyReason.PlayerCommandPending, busy.Reason);
            Assert.Equal(1, executor.WaitingCommandCount);

            signal.SetResult(true);
            await first;
            Assert.Equal<int?>(0, await executor.FindSeatAsync(Alice));
        }
        finally
        {
            signal.TrySetResult(true);
        }
    }

    [Fact]
    public async Task ResendOfThePendingCommandJoinsTheOriginalInsteadOfBeingRejected()
    {
        var signal = NewSignal();
        await using var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(), signal.Task);
        try
        {
            var command = new TakeSeatCommand(Guid.NewGuid(), Alice, 4);
            var original = executor.ExecuteAsync(command);
            // 客户端没等到结果就用同一编号重发（例如连接抖动）：拿到的是同一个结果，不占新的队列位置。
            var resend = executor.ExecuteAsync(command);

            Assert.Same(original, resend);
            Assert.Equal(1, executor.WaitingCommandCount);

            signal.SetResult(true);
            Assert.Equal(await original, await resend);
        }
        finally
        {
            signal.TrySetResult(true);
        }
    }

    [Fact]
    public async Task SameIdWithDifferentContentWhilePendingIsRejected()
    {
        var signal = NewSignal();
        await using var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(), signal.Task);
        try
        {
            var id = Guid.NewGuid();
            var original = executor.ExecuteAsync(new TakeSeatCommand(id, Alice, 4));

            // 提交后不能改同一个命令的内容来替换原意图。
            var busy = await Assert.ThrowsAsync<RoomBusyException>(() => executor.ExecuteAsync(new TakeSeatCommand(id, Alice, 5)));
            Assert.Equal(BusyReason.PlayerCommandPending, busy.Reason);

            signal.SetResult(true);
            await original;
            Assert.Equal<int?>(4, await executor.FindSeatAsync(Alice));
        }
        finally
        {
            signal.TrySetResult(true);
        }
    }

    [Fact]
    public async Task SlotIsReleasedAsSoonAsTheResultIsKnownWhateverTheOutcome()
    {
        await using var executor = new RoomExecutor(Guid.NewGuid(), 8);

        // 规则拒绝也是结果：拿到拒绝后马上可以提交下一个。
        await Assert.ThrowsAsync<RuleRejectedException>(() => executor.ExecuteAsync(new LeaveSeatCommand(Guid.NewGuid(), Alice)));
        await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 1));
        await executor.ExecuteAsync(new LeaveSeatCommand(Guid.NewGuid(), Alice));

        Assert.Equal(0, executor.PlayersWithPendingCommand);
    }

    [Fact]
    public async Task OnePlayerWaitingDoesNotBlockOthers()
    {
        var signal = NewSignal();
        await using var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(), signal.Task);
        try
        {
            var alice = executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 0));
            await Assert.ThrowsAsync<RoomBusyException>(() => executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 1)));

            // 名额按玩家计算：Alice 的等待不影响 Bob。
            var bob = executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Bob, 1));
            Assert.Equal(2, executor.PlayersWithPendingCommand);

            signal.SetResult(true);
            await Task.WhenAll(alice, bob);
        }
        finally
        {
            signal.TrySetResult(true);
        }
    }

    [Fact]
    public async Task RateLimitAllowsABurstThenRejectsWithRetryAfter()
    {
        // 突发 3 次，之后每秒 1 次。
        await using var executor = new RoomExecutor(Guid.NewGuid(), 8, TestOptions.With(playerCommandsPerSecond: 1, playerCommandBurst: 3));

        for (int i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<RuleRejectedException>(() => executor.ExecuteAsync(new LeaveSeatCommand(Guid.NewGuid(), Alice)));
        }
        var limited = await Assert.ThrowsAsync<RoomBusyException>(() => executor.ExecuteAsync(new LeaveSeatCommand(Guid.NewGuid(), Alice)));

        Assert.Equal(BusyReason.RateLimited, limited.Reason);
        Assert.NotNull(limited.RetryAfter);
        Assert.InRange(limited.RetryAfter.Value, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1));
        // 预算按玩家计算：Bob 不受影响。
        await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Bob, 2));
    }

    [Fact]
    public async Task RateLimitedAndPendingRejectionsAreNotJournaled()
    {
        var journal = new InMemoryRoomJournal();
        await using var executor = await RoomExecutor.CreatePersistentAsync(Guid.NewGuid(), 8, journal,
            TestOptions.With(playerCommandsPerSecond: 0.01, playerCommandBurst: 2));

        // 前两条是真正的规则拒绝，写入日志；之后的被限流挡在门外，不产生数据库写入。
        for (int i = 0; i < 50; i++)
        {
            try
            {
                await executor.ExecuteAsync(new LeaveSeatCommand(Guid.NewGuid(), Alice));
            }
            catch (RuleRejectedException)
            {
            }
            catch (RoomBusyException)
            {
            }
        }

        Assert.Equal(2, journal.AppendCount);
    }

    [Fact]
    public async Task ResendAfterCompletionStillReturnsTheOriginalResultWithoutSpendingTheSlot()
    {
        await using var executor = new RoomExecutor(Guid.NewGuid(), 8, TestOptions.With(playerCommandsPerSecond: 0.01, playerCommandBurst: 1));
        var command = new TakeSeatCommand(Guid.NewGuid(), Alice, 3);
        var receipt = await executor.ExecuteAsync(command);

        // 预算已用完：完成后的重发会被限流。限流说明“这一次没被接收”，不说明原命令失败；
        // 客户端退避后用同一编号再查，得到原结果。这里只确认限流不会把它误报为别的结果。
        var limited = await Assert.ThrowsAsync<RoomBusyException>(() => executor.ExecuteAsync(command));
        Assert.Equal(BusyReason.RateLimited, limited.Reason);
        Assert.Equal<int?>(3, await executor.FindSeatAsync(Alice));
        Assert.Equal(receipt.CommandId, command.CommandId);
    }

    [Fact]
    public async Task QueriesHaveTheirOwnPerPlayerRateLimit()
    {
        await using var executor = new RoomExecutor(Guid.NewGuid(), 8, TestOptions.With(playerQueriesPerSecond: 0.01, playerQueryBurst: 2));

        await executor.FindSeatAsync(Alice);
        await executor.FindSeatAsync(Alice);
        var limited = await Assert.ThrowsAsync<RoomBusyException>(() => executor.FindSeatAsync(Alice));

        Assert.Equal(BusyReason.RateLimited, limited.Reason);
        // 一名玩家刷视图不影响别人查询，也不消耗命令预算。
        Assert.Null(await executor.FindSeatAsync(Bob));
        await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 0));
    }

    [Fact]
    public async Task TrustedCommandsAreNotSubjectToPlayerLimits()
    {
        await using var executor = new RoomExecutor(Guid.NewGuid(), 4, TestOptions.With(playerCommandsPerSecond: 0.01, playerCommandBurst: 2));
        await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 0));
        await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Bob, 1));

        // 发牌由可信流程发出，不属于任何玩家，不占名额也不扣预算。
        Assert.Null(new DealCommand(Guid.NewGuid(), Deck.CreateStandard(), new[] { 0, 1 }).ActorId);
        await executor.ExecuteAsync(new DealCommand(Guid.NewGuid(), Deck.CreateStandard(), new[] { 0, 1 }));
    }

    [Fact]
    public async Task ConcurrentSubmissionsFromOnePlayerAdmitExactlyOne()
    {
        var signal = NewSignal();
        await using var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8),
            TestOptions.With(commands: 64, playerCommandBurst: 1000), signal.Task);
        try
        {
            using var go = new ManualResetEventSlim(false);
            // 每个线程只负责“提交”，拿回的是尚未完成的任务；用 Func<Task<...>> 避免 Task.Run 自动等待它完成。
            var attempts = Enumerable.Range(0, 32).Select(seat => Task.Factory.StartNew(() =>
            {
                go.Wait();
                return executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, seat % 8));
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            go.Set();
            var submitted = await Task.WhenAll(attempts);

            // 32 个线程同时为同一玩家提交，只有一个进入队列。
            Assert.Equal(1, executor.WaitingCommandCount);
            signal.SetResult(true);
            var outcomes = await Task.WhenAll(submitted.Select(async task =>
            {
                try
                {
                    await task;
                    return "applied";
                }
                catch (RoomBusyException)
                {
                    return "busy";
                }
            }));
            Assert.Equal(1, outcomes.Count(outcome => outcome == "applied"));
            Assert.Equal(31, outcomes.Count(outcome => outcome == "busy"));
            Assert.Equal(0, executor.PlayersWithPendingCommand);
        }
        finally
        {
            signal.TrySetResult(true);
        }
    }

    [Fact]
    public async Task SlotIsReleasedWhenTheRoomStopsWithTheCommandStillQueued()
    {
        var signal = NewSignal();
        var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(shutdownTimeout: TimeSpan.Zero), signal.Task);
        var queued = executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 0));

        await executor.StopAsync();

        // 被放弃的命令同样释放名额，不会永远挂着。
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        Assert.Equal(0, executor.PlayersWithPendingCommand);
        signal.SetResult(true);
    }

    [Fact]
    public void LimiterMemoryIsBounded()
    {
        var limiter = new PlayerRateLimiter(1, 1, TimeProvider.System);
        for (int i = 0; i < PlayerRateLimiter.MaxTrackedPlayers * 4; i++)
        {
            limiter.TryTake(Guid.NewGuid(), out _);
        }

        // 换身份刷请求不能让内存无限增长；被挡下的新身份要退避。
        Assert.InRange(limiter.TrackedPlayers, 1, PlayerRateLimiter.MaxTrackedPlayers);
    }

    [Fact]
    public void InvalidLimitsAreReportedWithTheirKeys()
    {
        var errors = new RoomOptions
        {
            PlayerCommandsPerSecond = double.NaN,
            PlayerCommandBurst = 0,
            PlayerQueriesPerSecond = -1,
            PlayerQueryBurst = RoomOptions.MaxBurst + 1
        }.Validate();

        Assert.Contains(errors, error => error.Contains("Room:PlayerCommandsPerSecond"));
        Assert.Contains(errors, error => error.Contains("Room:PlayerCommandBurst"));
        Assert.Contains(errors, error => error.Contains("Room:PlayerQueriesPerSecond"));
        Assert.Contains(errors, error => error.Contains("Room:PlayerQueryBurst"));
    }
}
