using DarkTide.Room;
using DarkTide.Rules;
using Npgsql;

namespace DarkTide.Persistence.Tests;

public class PostgresRoomJournalTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static readonly Guid Alice = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Bob = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private PostgresRoomJournal NewJournal() => new(database.DataSource);

    [Fact]
    public async Task RoomSurvivesARestartWithIdenticalCardsAndReceipts()
    {
        var roomId = Guid.NewGuid();
        var seat = new TakeSeatCommand(Guid.NewGuid(), Alice, 1);
        var rejected = new TakeSeatCommand(Guid.NewGuid(), Bob, 1);
        var deal = new DealCommand(Guid.NewGuid(), Shuffler.ShuffleStandard(), new[] { 1, 3 });
        CommandReceipt dealReceipt;
        IReadOnlyList<Card> aliceCards;
        await using (var original = await RoomExecutor.CreatePersistentAsync(roomId, 4, NewJournal()))
        {
            await original.ExecuteAsync(seat);
            await Assert.ThrowsAsync<RuleRejectedException>(() => original.ExecuteAsync(rejected));
            await original.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Bob, 3));
            dealReceipt = await original.ExecuteAsync(deal);
            aliceCards = (await original.CreatePlayerViewAsync(Alice)).HoleCards;
        }

        // 新的日志对象、新的执行器：只能从数据库读回状态。
        await using var restored = await RoomExecutor.RestoreAsync(roomId, NewJournal());

        Assert.Equal(aliceCards, (await restored.CreatePlayerViewAsync(Alice)).HoleCards);
        Assert.Equal(dealReceipt, await restored.ExecuteAsync(deal));
        Assert.Equal(RejectionReason.SeatTaken,
            (await Assert.ThrowsAsync<RuleRejectedException>(() => restored.ExecuteAsync(rejected))).Reason);
        Assert.Contains(roomId, await NewJournal().ListOpenRoomsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DatabaseRejectsDuplicateSequenceAndDuplicateCommandId()
    {
        var journal = NewJournal();
        var roomId = Guid.NewGuid();
        await journal.CreateRoomAsync(roomId, 4, CancellationToken.None);
        var first = new TakeSeatCommand(Guid.NewGuid(), Alice, 0);
        await journal.AppendAsync(roomId, new JournalEntry(1, first, CommandOutcome.Applied(1)), CancellationToken.None);

        // 另一个写者用同一序号写入：数据库拒绝，日志不会分叉。
        var sameSequence = await Assert.ThrowsAsync<PostgresException>(() => journal.AppendAsync(
            roomId, new JournalEntry(1, new TakeSeatCommand(Guid.NewGuid(), Bob, 1), CommandOutcome.Applied(1)), CancellationToken.None));
        Assert.Equal("23505", sameSequence.SqlState);

        // 同一命令编号写第二次：同样拒绝，幂等由数据库保证，不依赖内存缓存。
        var sameCommand = await Assert.ThrowsAsync<PostgresException>(() => journal.AppendAsync(
            roomId, new JournalEntry(2, first, CommandOutcome.Applied(2)), CancellationToken.None));
        Assert.Equal("23505", sameCommand.SqlState);

        var stored = await journal.LoadAsync(roomId, CancellationToken.None);
        Assert.Single(stored!.Entries);
    }

    [Fact]
    public async Task DuplicateRoomAndMissingRoomAreReported()
    {
        var journal = NewJournal();
        var roomId = Guid.NewGuid();
        await journal.CreateRoomAsync(roomId, 4, CancellationToken.None);

        await Assert.ThrowsAsync<RoomAlreadyExistsException>(() => journal.CreateRoomAsync(roomId, 4, CancellationToken.None));
        Assert.Null(await journal.LoadAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Null(await journal.FindAsync(roomId, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task RejectionsRoundTripWithReasonAndMessage()
    {
        var journal = NewJournal();
        var roomId = Guid.NewGuid();
        await journal.CreateRoomAsync(roomId, 4, CancellationToken.None);
        var command = new LeaveSeatCommand(Guid.NewGuid(), Alice, expectedVersion: 3);
        await journal.AppendAsync(roomId, new JournalEntry(1, command, CommandOutcome.Rejected(RejectionReason.NotSeated, "该玩家未在本房间入座。")), CancellationToken.None);

        var found = await journal.FindAsync(roomId, command.CommandId, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(command, found.Command);
        Assert.Equal(RejectionReason.NotSeated, found.Outcome.Rejection!.Reason);
        Assert.Equal("该玩家未在本房间入座。", found.Outcome.Rejection.Message);
    }

    [Fact]
    public async Task ClosedRoomsAreNotListedForRecovery()
    {
        var journal = NewJournal();
        var roomId = Guid.NewGuid();
        await journal.CreateRoomAsync(roomId, 4, CancellationToken.None);

        await journal.MarkClosedAsync(roomId, CancellationToken.None);
        await journal.MarkClosedAsync(roomId, CancellationToken.None);

        Assert.DoesNotContain(roomId, await journal.ListOpenRoomsAsync(CancellationToken.None));
        // 关闭的房间仍可读出：历史与回执不因关闭而消失。
        Assert.NotNull(await journal.LoadAsync(roomId, CancellationToken.None));
    }

    [Fact]
    public async Task MigrationIsIdempotentAndSafeToRunConcurrently()
    {
        // 多个实例同时启动：咨询锁串行化，脚本只执行一次，不会因 CREATE TABLE 竞争而失败。
        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => SchemaMigrator.MigrateAsync(database.DataSource)));

        Assert.All(runs, applied => Assert.Empty(applied));
    }

    [Fact]
    public async Task CheckConstraintRejectsAmbiguousOutcome()
    {
        var roomId = Guid.NewGuid();
        await NewJournal().CreateRoomAsync(roomId, 4, CancellationToken.None);
        await using var command = database.DataSource.CreateCommand(
            """
            INSERT INTO room_commands (room_id, sequence, command_id, command_kind, payload, applied_version, rejection_reason, rejection_text)
            VALUES ($1, 1, $2, 'take_seat', '{}', 1, 2, 'x')
            """);
        command.Parameters.AddWithValue(roomId);
        command.Parameters.AddWithValue(Guid.NewGuid());

        // 同时写了“成功”和“拒绝”：数据库层面拒绝，不让含糊的记录进入日志。
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("23514", error.SqlState);
    }
}
