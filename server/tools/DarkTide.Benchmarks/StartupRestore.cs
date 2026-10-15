using System.Diagnostics;
using DarkTide.Persistence;
using DarkTide.Room;
using Npgsql;
using NpgsqlTypes;

namespace DarkTide.Benchmarks;

/// <summary>
/// 模拟进程重启：库里有大量未关闭的房间，每个房间一整场的命令。测逐个恢复（当前服务器的做法）与限并发恢复的耗时，
/// 以及恢复后每个房间常驻的内存。数据用 COPY 直接写入，不经过执行器。
/// </summary>
internal static class StartupRestore
{
    public static async Task RunAsync(NpgsqlDataSource dataSource, int rooms, int commandsPerRoom)
    {
        Console.WriteLine($"== 启动恢复：{rooms} 个房间 × 每房间 {commandsPerRoom} 条命令 ==");
        var ids = Enumerable.Range(0, rooms).Select(_ => Guid.NewGuid()).ToArray();

        var watch = Stopwatch.StartNew();
        await using (var connection = await dataSource.OpenConnectionAsync())
        {
            await using (var writer = await connection.BeginBinaryImportAsync("COPY rooms (room_id, seat_count, rules_version) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var id in ids)
                {
                    await writer.StartRowAsync();
                    await writer.WriteAsync(id, NpgsqlDbType.Uuid);
                    await writer.WriteAsync((short)4, NpgsqlDbType.Smallint);
                    await writer.WriteAsync(RulesVersion.Current, NpgsqlDbType.Integer);
                }
                await writer.CompleteAsync();
            }
            await using (var writer = await connection.BeginBinaryImportAsync(
                "COPY room_commands (room_id, sequence, command_id, command_kind, payload, applied_version) FROM STDIN (FORMAT BINARY)"))
            {
                foreach (var id in ids)
                {
                    var toggler = new SeatToggler(Guid.NewGuid());
                    for (long sequence = 1; sequence <= commandsPerRoom; sequence++)
                    {
                        var command = toggler.Next();
                        var (kind, payload) = CommandCodec.Encode(command);
                        await writer.StartRowAsync();
                        await writer.WriteAsync(id, NpgsqlDbType.Uuid);
                        await writer.WriteAsync(sequence, NpgsqlDbType.Bigint);
                        await writer.WriteAsync(command.CommandId, NpgsqlDbType.Uuid);
                        await writer.WriteAsync(kind, NpgsqlDbType.Text);
                        await writer.WriteAsync(payload, NpgsqlDbType.Jsonb);
                        // 交替入座离座，每条都成功，版本号恰好等于序号。
                        await writer.WriteAsync(sequence, NpgsqlDbType.Bigint);
                    }
                }
                await writer.CompleteAsync();
            }
        }
        Console.WriteLine($"写入 {rooms * commandsPerRoom} 条记录用时 {watch.Elapsed.TotalSeconds:F1} s");

        var journal = new PostgresRoomJournal(dataSource);

        long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        watch.Restart();
        var restored = new List<RoomExecutor>(rooms);
        foreach (var id in ids)
        {
            restored.Add(await RoomExecutor.RestoreAsync(id, journal));
        }
        watch.Stop();
        long memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        await Verify(restored, commandsPerRoom);
        Console.WriteLine(
            $"逐个恢复    {watch.Elapsed.TotalSeconds,6:F1} s  {rooms / watch.Elapsed.TotalSeconds,7:F0} 房间/s  " +
            $"常驻内存约 {(memoryAfter - memoryBefore) / (double)rooms / 1024:F1} KiB/房间");
        await Task.WhenAll(restored.Select(executor => executor.StopAsync()));

        foreach (int parallelism in new[] { 4, 16 })
        {
            restored.Clear();
            watch.Restart();
            var bag = new System.Collections.Concurrent.ConcurrentBag<RoomExecutor>();
            await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, async (id, token) =>
                bag.Add(await RoomExecutor.RestoreAsync(id, journal, cancellationToken: token)));
            watch.Stop();
            restored.AddRange(bag);
            await Verify(restored, commandsPerRoom);
            Console.WriteLine($"并发 {parallelism,2} 恢复 {watch.Elapsed.TotalSeconds,6:F1} s  {rooms / watch.Elapsed.TotalSeconds,7:F0} 房间/s");
            await Task.WhenAll(restored.Select(executor => executor.StopAsync()));
        }
    }

    private static async Task Verify(IReadOnlyList<RoomExecutor> restored, long expectedVersion)
    {
        foreach (var executor in restored.Take(20))
        {
            var overview = await executor.CreateOverviewAsync();
            if (overview.StateVersion != expectedVersion)
            {
                throw new InvalidOperationException($"房间 {executor.RoomId} 恢复后版本为 {overview.StateVersion}，应为 {expectedVersion}。");
            }
        }
    }
}
