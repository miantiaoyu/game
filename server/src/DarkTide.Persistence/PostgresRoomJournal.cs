using DarkTide.Room;
using DarkTide.Rules;
using Npgsql;
using NpgsqlTypes;

namespace DarkTide.Persistence;

/// <summary>
/// 基于 PostgreSQL 的房间日志。每次追加是一条独立的自动提交语句：返回即已提交。
/// 单进程拥有房间时，序号主键与命令编号唯一约束足以防止重复与分叉；
/// 多节点所有权（租约、owner_epoch 围栏）尚未实现，见 docs/technical/protocol-and-concurrency.md。
/// </summary>
public sealed class PostgresRoomJournal(NpgsqlDataSource dataSource) : IRoomJournal
{
    private const string UniqueViolation = "23505";

    public async Task CreateRoomAsync(Guid roomId, int seatCount, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO rooms (room_id, seat_count) VALUES ($1, $2)");
        command.Parameters.AddWithValue(roomId);
        command.Parameters.AddWithValue((short)seatCount);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException error) when (error.SqlState == UniqueViolation)
        {
            throw new RoomAlreadyExistsException(roomId);
        }
    }

    public async Task AppendAsync(Guid roomId, JournalEntry entry, CancellationToken cancellationToken)
    {
        var (kind, payload) = CommandCodec.Encode(entry.Command);
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO room_commands
                (room_id, sequence, command_id, command_kind, payload, applied_version, rejection_reason, rejection_text)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            """);
        command.Parameters.AddWithValue(roomId);
        command.Parameters.AddWithValue(entry.Sequence);
        command.Parameters.AddWithValue(entry.Command.CommandId);
        command.Parameters.AddWithValue(kind);
        command.Parameters.Add(new NpgsqlParameter { Value = payload, NpgsqlDbType = NpgsqlDbType.Jsonb });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)entry.Outcome.AppliedVersion ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)(int?)entry.Outcome.Rejection?.Reason ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)entry.Outcome.Rejection?.Message ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        // 唯一约束冲突不吞掉：它说明有另一个写者或重复写入，执行器会把它当作结果未知并停止房间。
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JournalEntry?> FindAsync(Guid roomId, Guid commandId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {Columns} FROM room_commands WHERE room_id = $1 AND command_id = $2");
        command.Parameters.AddWithValue(roomId);
        command.Parameters.AddWithValue(commandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEntry(reader) : null;
    }

    public async Task<StoredRoom?> LoadAsync(Guid roomId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // 同一个快照里读房间与全部命令：加载期间别处追加的记录不会读到一半。
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        int seatCount;
        await using (var room = new NpgsqlCommand("SELECT seat_count FROM rooms WHERE room_id = $1", connection, transaction))
        {
            room.Parameters.AddWithValue(roomId);
            var result = await room.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                return null;
            }
            seatCount = (short)result;
        }

        var entries = new List<JournalEntry>();
        await using (var commands = new NpgsqlCommand(
            $"SELECT {Columns} FROM room_commands WHERE room_id = $1 ORDER BY sequence", connection, transaction))
        {
            commands.Parameters.AddWithValue(roomId);
            await using var reader = await commands.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(ReadEntry(reader));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoredRoom(roomId, seatCount, entries);
    }

    public async Task<IReadOnlyList<Guid>> ListOpenRoomsAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT room_id FROM rooms WHERE closed_at IS NULL ORDER BY created_at");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rooms = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rooms.Add(reader.GetGuid(0));
        }
        return rooms;
    }

    public async Task MarkClosedAsync(Guid roomId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE rooms SET closed_at = now() WHERE room_id = $1 AND closed_at IS NULL");
        command.Parameters.AddWithValue(roomId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string Columns = "sequence, command_kind, payload::text, applied_version, rejection_reason, rejection_text";

    private static JournalEntry ReadEntry(NpgsqlDataReader reader)
    {
        var command = CommandCodec.Decode(reader.GetString(1), reader.GetString(2));
        var outcome = reader.IsDBNull(3)
            ? CommandOutcome.Rejected((RejectionReason)reader.GetInt32(4), reader.GetString(5))
            : CommandOutcome.Applied(reader.GetInt64(3));
        return new JournalEntry(reader.GetInt64(0), command, outcome);
    }
}
