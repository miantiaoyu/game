using DarkTide.Rules;

namespace DarkTide.Room.Tests;

/// <summary>执行器与持久日志的配合：先写日志再确认、重启后重建、缓存淘汰后仍能去重、写入失败时结果未知。</summary>
public class RoomJournalTests
{
    private static readonly Guid Alice = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Bob = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    [Fact]
    public async Task EveryFinalOutcomeIsJournaledInOrder()
    {
        var journal = new InMemoryRoomJournal();
        var roomId = Guid.NewGuid();
        await using (var executor = await RoomExecutor.CreatePersistentAsync(roomId, 5, journal))
        {
            await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 0));
            await Assert.ThrowsAsync<RuleRejectedException>(() => executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Bob, 0)));
            await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Bob, 1));
        }

        var stored = await journal.LoadAsync(roomId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(new long[] { 1, 2, 3 }, stored.Entries.Select(entry => entry.Sequence));
        Assert.True(stored.Entries[0].Outcome.IsApplied);
        // 规则拒绝也是最终结果，同样写入：重启后重发它，得到的仍是同一个拒绝。
        Assert.Equal(RejectionReason.SeatTaken, stored.Entries[1].Outcome.Rejection!.Reason);
        Assert.Equal(2, stored.Entries[2].Outcome.AppliedVersion);
    }

    [Fact]
    public async Task QueriesAndCachedResendsAreNotJournaled()
    {
        var journal = new InMemoryRoomJournal();
        await using var executor = await RoomExecutor.CreatePersistentAsync(Guid.NewGuid(), 5, journal);
        var command = new TakeSeatCommand(Guid.NewGuid(), Alice, 0);

        await executor.ExecuteAsync(command);
        await executor.ExecuteAsync(command);
        await executor.FindSeatAsync(Alice);
        await executor.CreateOverviewAsync();

        Assert.Equal(1, journal.AppendCount);
    }

    [Fact]
    public async Task RestoredRoomHasTheSameStateAndRecognizesOldCommands()
    {
        var journal = new InMemoryRoomJournal();
        var roomId = Guid.NewGuid();
        var seatAlice = new TakeSeatCommand(Guid.NewGuid(), Alice, 2);
        var rejected = new TakeSeatCommand(Guid.NewGuid(), Bob, 2);
        var deck = Shuffler.ShuffleStandard();
        var deal = new DealCommand(Guid.NewGuid(), deck, new[] { 2, 4 });
        CommandReceipt dealReceipt;
        PlayerHandView aliceView;
        await using (var original = await RoomExecutor.CreatePersistentAsync(roomId, 5, journal))
        {
            await original.ExecuteAsync(seatAlice);
            await Assert.ThrowsAsync<RuleRejectedException>(() => original.ExecuteAsync(rejected));
            await original.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Bob, 4));
            dealReceipt = await original.ExecuteAsync(deal);
            aliceView = await original.CreatePlayerViewAsync(Alice);
        }

        // 模拟进程重启：内存里什么都没有，只有日志。
        await using var restored = await RoomExecutor.RestoreAsync(roomId, journal);

        var overview = await restored.CreateOverviewAsync();
        Assert.Equal(RoomStage.CardsDealt, overview.Stage);
        Assert.Equal(dealReceipt.CommittedVersion, overview.StateVersion);
        Assert.Equal(new[] { 2, 4 }, overview.ParticipantOrder);
        // 发出去的牌完全相同：重启不会重新洗牌。
        Assert.Equal(aliceView.HoleCards, (await restored.CreatePlayerViewAsync(Alice)).HoleCards);

        // 重启前处理过的命令再发一次：成功的返回原回执，拒绝的仍是原拒绝，都不再执行、不再写日志。
        int before = journal.AppendCount;
        Assert.Equal(dealReceipt, await restored.ExecuteAsync(deal));
        var again = await Assert.ThrowsAsync<RuleRejectedException>(() => restored.ExecuteAsync(rejected));
        Assert.Equal(RejectionReason.SeatTaken, again.Reason);
        Assert.Equal(before, journal.AppendCount);

        // 新命令从下一个序号继续。
        await Assert.ThrowsAsync<RuleRejectedException>(() => restored.ExecuteAsync(new LeaveSeatCommand(Guid.NewGuid(), Alice)));
        var stored = await journal.LoadAsync(roomId, CancellationToken.None);
        Assert.Equal(before + 1, stored!.Entries[^1].Sequence);
    }

    [Fact]
    public async Task EvictedCommandIsStillDeduplicatedThroughTheJournal()
    {
        var journal = new InMemoryRoomJournal();
        var options = TestOptions.With(receipts: 1);
        await using var executor = await RoomExecutor.CreatePersistentAsync(Guid.NewGuid(), 5, journal, options);
        var first = new TakeSeatCommand(Guid.NewGuid(), Alice, 0);
        var firstReceipt = await executor.ExecuteAsync(first);
        await executor.ExecuteAsync(new LeaveSeatCommand(Guid.NewGuid(), Alice));

        // 内存缓存只有 1 条，first 已被淘汰。没有日志时它会被再执行一次（见 RoomOptionsTests）；
        // 有日志时以日志为准，返回原回执，Alice 不会又坐回 0 号座。
        Assert.Equal(firstReceipt, await executor.ExecuteAsync(first));
        Assert.Null(await executor.FindSeatAsync(Alice));
        Assert.Equal(2, journal.AppendCount);

        // 同一编号不同内容：即使只能在日志里查到，也要拒绝。
        var conflict = await Assert.ThrowsAsync<RuleRejectedException>(() =>
            executor.ExecuteAsync(new TakeSeatCommand(first.CommandId, Bob, 3)));
        Assert.Equal(RejectionReason.CommandIdConflict, conflict.Reason);
    }

    [Fact]
    public async Task NewCommandsNeverQueryTheJournalEvenAfterEviction()
    {
        // 回归：以前缓存淘汰过一次之后，每条新命令都要先查一次库（一场后半段的数据库往返翻倍）。
        var journal = new InMemoryRoomJournal();
        await using var executor = await RoomExecutor.CreatePersistentAsync(Guid.NewGuid(), 5, journal, TestOptions.With(receipts: 2));

        for (int i = 0; i < 20; i++)
        {
            await executor.ExecuteAsync(i % 2 == 0 ? new TakeSeatCommand(Guid.NewGuid(), Alice, 0) : new LeaveSeatCommand(Guid.NewGuid(), Alice));
        }

        Assert.Equal(20, journal.AppendCount);
        Assert.Equal(0, journal.FindCount);
    }

    [Fact]
    public async Task RestoredRoomDeduplicatesOldCommandsThatAreNoLongerCached()
    {
        var journal = new InMemoryRoomJournal();
        var roomId = Guid.NewGuid();
        var options = TestOptions.With(receipts: 1);
        var first = new TakeSeatCommand(Guid.NewGuid(), Alice, 0);
        CommandReceipt firstReceipt;
        await using (var original = await RoomExecutor.CreatePersistentAsync(roomId, 5, journal, options))
        {
            firstReceipt = await original.ExecuteAsync(first);
            await original.ExecuteAsync(new LeaveSeatCommand(Guid.NewGuid(), Alice));
        }

        // 重放后缓存只剩最后一条；第一条要靠“已写入日志的编号”识别，到日志取原结果。
        await using var restored = await RoomExecutor.RestoreAsync(roomId, journal, options);

        Assert.Equal(firstReceipt, await restored.ExecuteAsync(first));
        Assert.Null(await restored.FindSeatAsync(Alice));
        Assert.Equal(1, journal.FindCount);
        Assert.Equal(2, journal.AppendCount);
    }

    [Fact]
    public async Task StalledJournalFailsWithinTheConfiguredTimeoutAndStopsTheRoom()
    {
        var journal = new InMemoryRoomJournal();
        var never = new TaskCompletionSource();
        var executor = await RoomExecutor.CreatePersistentAsync(Guid.NewGuid(), 5, journal,
            TestOptions.With(journalTimeout: TimeSpan.FromMilliseconds(200)));
        journal.HoldAppends = never.Task;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<RoomPersistenceException>(
            () => executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 0)).WaitAsync(TimeSpan.FromSeconds(10)));
        watch.Stop();

        // 以前没有超时，只受数据库驱动默认的 30 秒约束；现在按配置的时限失败，房间不会被长时间占住。
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(3));
        // 取消令牌与执行器自己的等待时限同时到期，先到的那个决定异常类型。
        Assert.True(error.InnerException is OperationCanceledException or TimeoutException, error.InnerException?.GetType().Name);
        await executor.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(executor.FaultReason);
    }

    [Fact]
    public async Task JournalFailureMakesTheOutcomeUnknownAndStopsTheRoom()
    {
        var journal = new InMemoryRoomJournal();
        var roomId = Guid.NewGuid();
        var executor = await RoomExecutor.CreatePersistentAsync(roomId, 5, journal);
        await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 0));
        var lost = new TakeSeatCommand(Guid.NewGuid(), Bob, 1);

        journal.FailNextAppend = new IOException("模拟数据库断开");
        var error = await Assert.ThrowsAsync<RoomPersistenceException>(() => executor.ExecuteAsync(lost));

        Assert.Equal(lost.CommandId, error.CommandId);
        await executor.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<RoomPersistenceException>(executor.FaultReason);
        await Assert.ThrowsAsync<RoomFaultedException>(() => executor.FindSeatAsync(Bob));

        // 恢复后，没写进日志的命令不存在；用同一编号重试是安全的，会正常执行一次。
        await using var restored = await RoomExecutor.RestoreAsync(roomId, journal);
        Assert.Null(await restored.FindSeatAsync(Bob));
        await restored.ExecuteAsync(lost);
        Assert.Equal<int?>(1, await restored.FindSeatAsync(Bob));
    }

    [Fact]
    public async Task DuplicateRoomIdIsRejectedByTheJournal()
    {
        var journal = new InMemoryRoomJournal();
        var roomId = Guid.NewGuid();
        await using var _ = await RoomExecutor.CreatePersistentAsync(roomId, 5, journal);

        await Assert.ThrowsAsync<RoomAlreadyExistsException>(() => RoomExecutor.CreatePersistentAsync(roomId, 5, journal));
        // 参数非法时根本不登记。
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => RoomExecutor.CreatePersistentAsync(Guid.NewGuid(), 99, journal));
        Assert.Single(await journal.ListOpenRoomsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReplayMismatchRefusesToLoad()
    {
        // 日志说“入座成功、版本 1”，但规则其实会拒绝（座位越界）：说明日志与代码不一致，不能加载。
        var journal = new TamperedJournal(new JournalEntry(1, new TakeSeatCommand(Guid.NewGuid(), Alice, 7), CommandOutcome.Applied(1)));

        var error = await Assert.ThrowsAsync<JournalReplayMismatchException>(() => RoomExecutor.RestoreAsync(journal.RoomId, journal));

        Assert.Equal(1, error.Sequence);
    }

    [Fact]
    public async Task MissingRoomCannotBeRestored()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => RoomExecutor.RestoreAsync(Guid.NewGuid(), new InMemoryRoomJournal()));
    }

    [Fact]
    public void CommandCodecRoundTripsEveryCommandKind()
    {
        RoomCommand[] commands =
        [
            new TakeSeatCommand(Guid.NewGuid(), Alice, 3, expectedVersion: 7),
            new LeaveSeatCommand(Guid.NewGuid(), Bob),
            new DealCommand(Guid.NewGuid(), Shuffler.ShuffleStandard(), new[] { 4, 1, 0 }, expectedVersion: 2)
        ];

        foreach (var command in commands)
        {
            var (kind, payload) = CommandCodec.Encode(command);
            Assert.Equal(command, CommandCodec.Decode(kind, payload));
        }

        Assert.Throws<System.Text.Json.JsonException>(() => CommandCodec.Decode("unknown", """{"v":1,"commandId":"00000000-0000-0000-0000-000000000001"}"""));
        Assert.Throws<System.Text.Json.JsonException>(() => CommandCodec.Decode("take_seat", """{"v":99}"""));
    }

    private sealed class TamperedJournal(params JournalEntry[] entries) : IRoomJournal
    {
        public Guid RoomId { get; } = Guid.NewGuid();
        public Task CreateRoomAsync(Guid roomId, int seatCount, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AppendAsync(Guid roomId, JournalEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<JournalEntry?> FindAsync(Guid roomId, Guid commandId, CancellationToken cancellationToken) => Task.FromResult<JournalEntry?>(null);
        public Task<StoredRoom?> LoadAsync(Guid roomId, CancellationToken cancellationToken) => Task.FromResult<StoredRoom?>(new StoredRoom(RoomId, 5, entries));
        public Task<IReadOnlyList<Guid>> ListOpenRoomsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([RoomId]);
        public Task MarkClosedAsync(Guid roomId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
