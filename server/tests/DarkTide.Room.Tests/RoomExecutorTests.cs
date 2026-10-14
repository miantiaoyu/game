using DarkTide.Rules;

namespace DarkTide.Room.Tests;

public class RoomExecutorTests
{
    private static readonly Guid alice = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid bob = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task ConcurrentSeatGrabHasSingleWinnerAndQueueSurvives()
    {
        await using var executor = new RoomExecutor(Guid.NewGuid(), 8);
        var startSignal = NewSignal();
        var players = Enumerable.Range(0, 16).Select(_ => Guid.NewGuid()).ToArray();
        var requests = players.Select(async player =>
        {
            await startSignal.Task;
            try
            {
                await SubmitTakeSeat(executor, player, 6);
                return true;
            }
            catch (RuleRejectedException error) when (error.Reason == RejectionReason.SeatTaken)
            {
                return false;
            }
        }).ToArray();

        startSignal.SetResult(true);
        var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, results.Count(succeeded => succeeded));
        for (int index = 0; index < players.Length; index++)
        {
            Assert.Equal<int?>(results[index] ? 6 : null, await executor.FindSeatAsync(players[index]));
        }
        await SubmitTakeSeat(executor, alice, 0);
        Assert.Equal<int?>(0, await executor.FindSeatAsync(alice));
    }

    [Fact]
    public async Task QueuedRequestsRunInOrderAndQueriesDoNotOvertakeWrites()
    {
        var signal = NewSignal();
        await using var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(commands: 4), signal.Task);
        try
        {
            var TakeSeat = SubmitTakeSeat(executor, alice, 3);
            var LeaveSeat = SubmitLeaveSeat(executor, alice);
            var reseat = SubmitTakeSeat(executor, bob, 3);
            var query = executor.FindSeatAsync(bob);
            Assert.False(query.IsCompleted);
            signal.SetResult(true);

            await Task.WhenAll(TakeSeat, LeaveSeat, reseat);
            Assert.Equal<int?>(3, await query);
            Assert.Null(await executor.FindSeatAsync(alice));
        }
        finally
        {
            signal.TrySetResult(true);
        }
    }

    [Fact]
    public async Task FullQueueRejectsImmediatelyAndRejectedRequestNeverRuns()
    {
        var signal = NewSignal();
        await using var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(commands: 1), signal.Task);
        try
        {
            var accepted = SubmitTakeSeat(executor, alice, 0);
            var overCapacity = SubmitTakeSeat(executor, bob, 1);

            Assert.True(overCapacity.IsCompleted);
            await Assert.ThrowsAsync<RoomBusyException>(() => overCapacity);
            Assert.False(accepted.IsCompleted);
            signal.SetResult(true);
            await accepted;

            Assert.Null(await executor.FindSeatAsync(bob));
            await SubmitTakeSeat(executor, bob, 1);
            Assert.Equal<int?>(1, await executor.FindSeatAsync(bob));
        }
        finally
        {
            signal.TrySetResult(true);
        }
    }

    [Fact]
    public async Task MutatingSourceArraysAfterSubmitDoesNotAffectQueuedDeal()
    {
        var signal = NewSignal();
        await using var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(commands: 4), signal.Task);
        try
        {
            var aliceSeats = SubmitTakeSeat(executor, alice, 6);
            var bobSeats = SubmitTakeSeat(executor, bob, 0);
            var drawOrder = Deck.CreateStandard().ToArray();
            int[] order = { 6, 0 };
            var deal = SubmitDeal(executor, drawOrder, order);
            var lookup = executor.CreatePlayerViewAsync(alice);

            Array.Reverse(drawOrder);
            Array.Reverse(order);
            signal.SetResult(true);
            await Task.WhenAll(aliceSeats, bobSeats, deal);
            var view = await lookup;

            Assert.Equal(6, view.Seat);
            Assert.Equal(new[] { new Card(Suit.Red, 1), new Card(Suit.Red, 3) }, view.HoleCards);
        }
        finally
        {
            signal.TrySetResult(true);
        }
    }

    [Fact]
    public async Task InvalidRequestsAreRejectedAndRoomKeepsWorking()
    {
        await using var executor = new RoomExecutor(Guid.NewGuid(), 8);
        await Assert.ThrowsAsync<RuleRejectedException>(() => SubmitTakeSeat(executor, alice, 8));
        await SubmitTakeSeat(executor, alice, 6);
        await SubmitTakeSeat(executor, bob, 0);
        await Assert.ThrowsAsync<RuleRejectedException>(() => SubmitDeal(executor, Deck.CreateStandard(), new[] { 6, 7 }));
        await SubmitDeal(executor, Deck.CreateStandard(), new[] { 6, 0 });
        await Assert.ThrowsAsync<RuleRejectedException>(() => executor.CreatePlayerViewAsync(Guid.NewGuid()));
        Assert.Equal(2, (await executor.CreatePlayerViewAsync(alice)).HoleCards.Count);
    }

    [Fact]
    public async Task ShutdownRejectsNewRequestsAndDrainsAcceptedOnes()
    {
        var signal = NewSignal();
        await using var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(commands: 2), signal.Task);
        try
        {
            var TakeSeat = SubmitTakeSeat(executor, alice, 6);
            var query = executor.FindSeatAsync(alice);
            var shutdown = executor.DisposeAsync().AsTask();

            Assert.False(shutdown.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => SubmitTakeSeat(executor, bob, 0));
            signal.SetResult(true);
            await shutdown;
            await TakeSeat;
            Assert.Equal<int?>(6, await query);
            await executor.DisposeAsync();
            Assert.Null(executor.FaultReason);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => executor.FindSeatAsync(alice));
        }
        finally
        {
            signal.TrySetResult(true);
        }
    }

    [Fact]
    public async Task ConcurrentSubmittersAndShutdownAllGetDefiniteOutcomes()
    {
        var executor = new RoomExecutor(Guid.NewGuid(), 8);
        var signal = NewSignal();
        var requests = Enumerable.Range(0, 16).Select(async _ =>
        {
            await signal.Task;
            try
            {
                Assert.Null(await executor.FindSeatAsync(alice));
            }
            catch (ObjectDisposedException)
            {
                // 与停止接收竞争时，也允许明确拒绝，但不能永久等待。
            }
        }).ToArray();
        var shutdownTask = Task.Run(async () =>
        {
            await signal.Task;
            await executor.DisposeAsync();
        });
        try
        {
            signal.SetResult(true);
            await Task.WhenAll(requests.Append(shutdownTask)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await executor.DisposeAsync();
        }
    }

    [Fact]
    public async Task PausedRoomDoesNotBlockAnotherRoom()
    {
        var signal = NewSignal();
        await using var firstRoom = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(commands: 1), signal.Task);
        await using var secondRoom = new RoomExecutor(Guid.NewGuid(), 8);
        try
        {
            var waitingRequest = SubmitTakeSeat(firstRoom, alice, 6);
            await SubmitTakeSeat(secondRoom, bob, 0).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal<int?>(0, await secondRoom.FindSeatAsync(bob));
            Assert.False(waitingRequest.IsCompleted);
            signal.SetResult(true);
            await waitingRequest;
            Assert.Null(await firstRoom.FindSeatAsync(bob));
        }
        finally
        {
            signal.TrySetResult(true);
        }
    }

    [Fact]
    public async Task ConsumerFaultFailsQueuedRequestsAndStopsAccepting()
    {
        var signal = NewSignal();
        var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(commands: 2), signal.Task);
        var firstRequest = SubmitTakeSeat(executor, alice, 0);
        var secondRequest = executor.FindSeatAsync(alice);
        signal.SetException(new ApplicationException("测试模拟消费循环故障。"));

        await executor.Stopped.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<ApplicationException>(executor.FaultReason);
        await Assert.ThrowsAsync<RoomFaultedException>(() => firstRequest);
        await Assert.ThrowsAsync<RoomFaultedException>(() => secondRequest);
        var laterRejection = await Assert.ThrowsAsync<RoomFaultedException>(() => SubmitTakeSeat(executor, bob, 1));
        Assert.Same(executor.FaultReason, laterRejection.InnerException);
        // 故障已通过属性公开，关闭时不再重复抛出。
        await executor.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentResendOfSameCommandAppliesOnce()
    {
        await using var executor = new RoomExecutor(Guid.NewGuid(), 8);
        var startSignal = NewSignal();
        var command = new TakeSeatCommand(Guid.NewGuid(), alice, 2);
        // 模拟多条连接同时重发；队列把它们排成先后，第一条执行，其余命中记录。
        var retries = Enumerable.Range(0, 8).Select(async _ =>
        {
            await startSignal.Task;
            return await executor.ExecuteAsync(command);
        }).ToArray();

        startSignal.SetResult(true);
        var receipts = await Task.WhenAll(retries).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(receipts.Distinct());
        var overview = await executor.CreateOverviewAsync();
        Assert.Equal(1, overview.StateVersion);
        Assert.True(overview.SeatOccupied[2]);
    }

    [Fact]
    public async Task FullQueryQuotaStillAllowsCommands()
    {
        var signal = NewSignal();
        await using var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(commands: 2, queries: 1), signal.Task);
        try
        {
            var firstQuery = executor.FindSeatAsync(alice);
            await Assert.ThrowsAsync<RoomBusyException>(() => executor.CreateOverviewAsync());

            // 刷新视图挤满了查询额度，但入座命令有自己的额度。
            var command = SubmitTakeSeat(executor, alice, 0);
            Assert.False(command.IsCompleted);

            signal.SetResult(true);
            Assert.Null(await firstQuery);
            await command;
            Assert.Equal<int?>(0, await executor.FindSeatAsync(alice));
        }
        finally
        {
            signal.TrySetResult(true);
        }
    }

    [Fact]
    public async Task FullCommandQuotaStillAllowsQueriesAndReleasesQuota()
    {
        var signal = NewSignal();
        await using var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(commands: 1), signal.Task);
        try
        {
            var firstCommand = SubmitTakeSeat(executor, alice, 0);
            await Assert.ThrowsAsync<RoomBusyException>(() => SubmitTakeSeat(executor, bob, 1));

            var query = executor.FindSeatAsync(alice);
            Assert.False(query.IsCompleted);

            signal.SetResult(true);
            await firstCommand;
            // 查询排在命令之后，所以能看到它的结果。
            Assert.Equal<int?>(0, await query);
            // 额度已归还，后续命令可以再次进入。
            await SubmitTakeSeat(executor, bob, 1);
        }
        finally
        {
            signal.TrySetResult(true);
        }
    }

    [Fact]
    public async Task OverviewDistinguishesEmptySeatFromSeatedNonParticipant()
    {
        await using var executor = new RoomExecutor(Guid.NewGuid(), 4);
        await SubmitTakeSeat(executor, alice, 0);
        await SubmitTakeSeat(executor, bob, 1);
        await SubmitTakeSeat(executor, Guid.NewGuid(), 2);
        await SubmitDeal(executor, Deck.CreateStandard(), new[] { 1, 0 });

        var overview = await executor.CreateOverviewAsync();

        Assert.Equal(RoomStage.CardsDealt, overview.Stage);
        Assert.Equal(4, overview.StateVersion);
        Assert.Equal(new[] { true, true, true, false }, overview.SeatOccupied);
        Assert.Equal(new[] { 1, 0 }, overview.ParticipantOrder);
    }

    [Fact]
    public async Task NullCommandIsRejected()
    {
        await using var executor = new RoomExecutor(Guid.NewGuid(), 8);
        Assert.Throws<ArgumentNullException>(() => { _ = executor.ExecuteAsync(null!); });
    }

    [Fact]
    public async Task EmptyPlayerIdIsRejectedBeforeQueueing()
    {
        var signal = NewSignal();
        await using var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(commands: 1, queries: 1), signal.Task);

        // 用 Action 形式断言：调用本身同步抛出，根本没有返回任务。
        // 命令在构造时就被拒绝，查询在入队前被拒绝。
        Assert.Throws<ArgumentException>(() => { _ = SubmitTakeSeat(executor, Guid.Empty, 0); });
        Assert.Throws<ArgumentException>(() => { _ = SubmitLeaveSeat(executor, Guid.Empty); });
        Assert.Throws<ArgumentException>(() => { _ = executor.FindSeatAsync(Guid.Empty); });
        Assert.Throws<ArgumentException>(() => { _ = executor.CreatePlayerViewAsync(Guid.Empty); });

        // 两类额度都只有 1，仍然空着，正常请求可以进入，不会收到繁忙。
        var normalCommand = SubmitTakeSeat(executor, alice, 0);
        var normalQuery = executor.FindSeatAsync(alice);
        Assert.False(normalCommand.IsCompleted);
        Assert.False(normalQuery.IsCompleted);
        signal.SetResult(true);
        await normalCommand;
        Assert.Equal<int?>(0, await normalQuery);
    }

    [Fact]
    public async Task NonRuleExceptionIsTreatedAsFaultAndStopsRoom()
    {
        var executor = new RoomExecutor(Guid.NewGuid(), 8);
        await SubmitTakeSeat(executor, alice, 0);
        await SubmitTakeSeat(executor, bob, 1);
        var duplicateDeck = Deck.CreateStandard().ToArray();
        duplicateDeck[26] = duplicateDeck[0];

        // 张数正确，能通过入队前的载荷检查；重复牌是可信流程的缺陷，不是玩家操作被拒。
        await Assert.ThrowsAsync<ArgumentException>(() => SubmitDeal(executor, duplicateDeck, new[] { 0, 1 }));

        await executor.Stopped.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<ArgumentException>(executor.FaultReason);
        await Assert.ThrowsAsync<RoomFaultedException>(() => executor.FindSeatAsync(alice));
        await executor.DisposeAsync();
    }

    [Fact]
    public async Task ExistingRoomCanBeTakenOverWithItsState()
    {
        var existingRoom = new Room(Guid.NewGuid(), 8);
        existingRoom.TakeSeat(alice, 3);

        await using var executor = new RoomExecutor(existingRoom);

        Assert.Equal(existingRoom.RoomId, executor.RoomId);
        Assert.Equal<int?>(3, await executor.FindSeatAsync(alice));
        await Assert.ThrowsAsync<RuleRejectedException>(() => SubmitTakeSeat(executor, bob, 3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(RoomOptions.MaxQueueCapacity + 1)]
    public void UnsupportedCommandQueueCapacityIsRejected(int capacity)
    {
        Assert.Throws<ArgumentException>(() =>
            new RoomExecutor(Guid.NewGuid(), 8, TestOptions.With(commands: capacity)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(RoomOptions.MaxQueueCapacity + 1)]
    public void UnsupportedQueryQueueCapacityIsRejected(int capacity)
    {
        Assert.Throws<ArgumentException>(() =>
            new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(queries: capacity), Task.CompletedTask));
    }

    // 每次调用生成新命令编号，相当于玩家各点了一次；测试重发时显式复用同一个命令对象。
    private static Task<CommandReceipt> SubmitTakeSeat(RoomExecutor executor, Guid playerId, int seat) =>
        executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), playerId, seat));

    private static Task<CommandReceipt> SubmitLeaveSeat(RoomExecutor executor, Guid playerId) =>
        executor.ExecuteAsync(new LeaveSeatCommand(Guid.NewGuid(), playerId));

    private static Task<CommandReceipt> SubmitDeal(RoomExecutor executor, IReadOnlyList<Card> drawOrder, IReadOnlyList<int> ParticipantOrder) =>
        executor.ExecuteAsync(new DealCommand(Guid.NewGuid(), drawOrder, ParticipantOrder));

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
