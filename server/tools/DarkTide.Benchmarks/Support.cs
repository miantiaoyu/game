using DarkTide.Room;

namespace DarkTide.Benchmarks;

/// <summary>同一玩家交替入座、离座：每条命令都会成功并推进版本，可以无限生成合法命令。</summary>
internal sealed class SeatToggler(Guid playerId)
{
    private bool _seated;

    public RoomCommand Next()
    {
        RoomCommand command = _seated
            ? new LeaveSeatCommand(Guid.NewGuid(), playerId)
            : new TakeSeatCommand(Guid.NewGuid(), playerId, 0);
        _seated = !_seated;
        return command;
    }
}

/// <summary>统计执行器对日志的调用次数：每条命令应当恰好一次追加，新命令不应查询。</summary>
internal sealed class CountingJournal(IRoomJournal inner) : IRoomJournal
{
    private long _appends;
    private long _finds;

    public long Appends => Interlocked.Read(ref _appends);
    public long Finds => Interlocked.Read(ref _finds);

    public void Reset()
    {
        Interlocked.Exchange(ref _appends, 0);
        Interlocked.Exchange(ref _finds, 0);
    }

    public Task CreateRoomAsync(Guid roomId, int seatCount, Guid? createdBy, CancellationToken cancellationToken) =>
        inner.CreateRoomAsync(roomId, seatCount, createdBy, cancellationToken);

    public Task AppendAsync(Guid roomId, JournalEntry entry, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _appends);
        return inner.AppendAsync(roomId, entry, cancellationToken);
    }

    public Task<JournalEntry?> FindAsync(Guid roomId, Guid commandId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _finds);
        return inner.FindAsync(roomId, commandId, cancellationToken);
    }

    public Task<StoredRoom?> LoadAsync(Guid roomId, CancellationToken cancellationToken) => inner.LoadAsync(roomId, cancellationToken);
    public Task<IReadOnlyList<Guid>> ListOpenRoomsAsync(CancellationToken cancellationToken) => inner.ListOpenRoomsAsync(cancellationToken);
    public Task MarkClosedAsync(Guid roomId, CancellationToken cancellationToken) => inner.MarkClosedAsync(roomId, cancellationToken);
    public Task<IReadOnlyDictionary<int, int>> CountOpenRoomsByRulesVersionAsync(CancellationToken cancellationToken) =>
        inner.CountOpenRoomsByRulesVersionAsync(cancellationToken);
}

internal static class Report
{
    public static void Latency(string title, IEnumerable<IEnumerable<double>> perRoom, TimeSpan elapsed, CountingJournal journal)
    {
        var all = perRoom.SelectMany(values => values).Order().ToArray();
        if (all.Length == 0)
        {
            Console.WriteLine($"{title}: 没有样本");
            return;
        }
        Console.WriteLine(
            $"{title,-14} 命令 {all.Length,6}  实际 {all.Length / elapsed.TotalSeconds,7:F0}/s  " +
            $"p50 {Percentile(all, 0.50),6:F1}  p95 {Percentile(all, 0.95),6:F1}  p99 {Percentile(all, 0.99),6:F1}  max {all[^1],7:F1} ms  " +
            $"追加/条 {journal.Appends / (double)all.Length:F2}  查询/条 {journal.Finds / (double)all.Length:F2}");
    }

    private static double Percentile(double[] sorted, double quantile) =>
        sorted[Math.Max(0, (int)Math.Ceiling(quantile * sorted.Length) - 1)];
}

/// <summary>压测要测的是数据库，不是每玩家限流：每个房间只有一个玩家在连续操作，按默认速率会被限流挡住。</summary>
internal static class Unthrottled
{
    public static RoomOptions Options { get; } = new()
    {
        PlayerCommandsPerSecond = RoomOptions.MaxPerSecond,
        PlayerCommandBurst = RoomOptions.MaxBurst
    };
}
