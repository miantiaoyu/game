using System.Collections.Concurrent;

namespace DarkTide.Room;

/// <summary>
/// 进程内日志，供测试与本地开发使用：进程退出即丢失，不提供真正的持久性。
/// 存的是编码后的命令，而不是命令对象本身：这样测试也会经过 <see cref="CommandCodec"/>，
/// 读回来的命令与写进去的对象没有共享引用。
/// </summary>
public sealed class InMemoryRoomJournal : IRoomJournal
{
    private sealed class RoomLog(int seatCount)
    {
        public int SeatCount { get; } = seatCount;
        public List<(long Sequence, Guid CommandId, string Kind, string Payload, CommandOutcome Outcome)> Entries { get; } = [];
        public bool Closed { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, RoomLog> _rooms = new();

    /// <summary>测试用：让下一次 <see cref="AppendAsync"/> 抛出这个异常，模拟数据库写入失败。</summary>
    public Exception? FailNextAppend { get; set; }

    public int AppendCount { get; private set; }

    public Task CreateRoomAsync(Guid roomId, int seatCount, CancellationToken cancellationToken)
    {
        if (!_rooms.TryAdd(roomId, new RoomLog(seatCount)))
        {
            throw new RoomAlreadyExistsException(roomId);
        }
        return Task.CompletedTask;
    }

    public Task AppendAsync(Guid roomId, JournalEntry entry, CancellationToken cancellationToken)
    {
        if (FailNextAppend is { } failure)
        {
            FailNextAppend = null;
            throw failure;
        }

        var log = _rooms[roomId];
        lock (log)
        {
            long expected = log.Entries.Count + 1;
            if (entry.Sequence != expected)
            {
                throw new InvalidOperationException($"序号冲突：应为 {expected}，收到 {entry.Sequence}。");
            }
            if (log.Entries.Any(existing => existing.CommandId == entry.Command.CommandId))
            {
                throw new InvalidOperationException($"命令编号 {entry.Command.CommandId} 已经记录过。");
            }
            var (kind, payload) = CommandCodec.Encode(entry.Command);
            log.Entries.Add((entry.Sequence, entry.Command.CommandId, kind, payload, entry.Outcome));
            AppendCount++;
        }
        return Task.CompletedTask;
    }

    public Task<JournalEntry?> FindAsync(Guid roomId, Guid commandId, CancellationToken cancellationToken)
    {
        var log = _rooms[roomId];
        lock (log)
        {
            foreach (var row in log.Entries)
            {
                if (row.CommandId == commandId)
                {
                    return Task.FromResult<JournalEntry?>(ToEntry(row));
                }
            }
        }
        return Task.FromResult<JournalEntry?>(null);
    }

    public Task<StoredRoom?> LoadAsync(Guid roomId, CancellationToken cancellationToken)
    {
        if (!_rooms.TryGetValue(roomId, out var log))
        {
            return Task.FromResult<StoredRoom?>(null);
        }
        lock (log)
        {
            return Task.FromResult<StoredRoom?>(new StoredRoom(roomId, log.SeatCount, log.Entries.Select(ToEntry).ToArray()));
        }
    }

    public Task<IReadOnlyList<Guid>> ListOpenRoomsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(_rooms.Where(pair => !pair.Value.Closed).Select(pair => pair.Key).ToArray());

    public Task MarkClosedAsync(Guid roomId, CancellationToken cancellationToken)
    {
        if (_rooms.TryGetValue(roomId, out var log))
        {
            log.Closed = true;
        }
        return Task.CompletedTask;
    }

    private static JournalEntry ToEntry((long Sequence, Guid CommandId, string Kind, string Payload, CommandOutcome Outcome) row) =>
        new(row.Sequence, CommandCodec.Decode(row.Kind, row.Payload), row.Outcome);
}
