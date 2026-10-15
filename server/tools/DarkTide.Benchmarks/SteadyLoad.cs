using System.Diagnostics;
using DarkTide.Persistence;
using DarkTide.Room;
using Npgsql;

namespace DarkTide.Benchmarks;

/// <summary>
/// 先让每个房间执行超过回执缓存容量（128）的命令，进入“一场后半段”的状态；
/// 再按固定总速率均匀发命令，测确认延迟与每条命令的数据库调用次数。
/// 延迟从“计划发出时间”算起：前一条拖延导致的排队也计入，避免只量到顺利的那部分（协调遗漏）。
/// </summary>
internal static class SteadyLoad
{
    public static async Task RunAsync(NpgsqlDataSource dataSource, int rooms, int warmCommandsPerRoom, int[] rates, TimeSpan duration)
    {
        Console.WriteLine($"== 稳态负载：{rooms} 个房间，每房间先预热 {warmCommandsPerRoom} 条 ==");
        var journal = new CountingJournal(new PostgresRoomJournal(dataSource));
        var executors = new RoomExecutor[rooms];
        var togglers = new SeatToggler[rooms];
        for (int i = 0; i < rooms; i++)
        {
            executors[i] = await RoomExecutor.CreatePersistentAsync(Guid.NewGuid(), 4, journal, Unthrottled.Options);
            togglers[i] = new SeatToggler(Guid.NewGuid());
        }

        // 预热同时是饱和测试：每个房间尽快连续执行，房间之间并行。
        journal.Reset();
        var warmWatch = Stopwatch.StartNew();
        var warm = await Task.WhenAll(Enumerable.Range(0, rooms).Select(async i =>
        {
            var latencies = new List<double>(warmCommandsPerRoom);
            for (int k = 0; k < warmCommandsPerRoom; k++)
            {
                long started = Stopwatch.GetTimestamp();
                await executors[i].ExecuteAsync(togglers[i].Next());
                latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            return latencies;
        }));
        warmWatch.Stop();
        Report.Latency("饱和", warm, warmWatch.Elapsed, journal);

        foreach (int rate in rates)
        {
            journal.Reset();
            double interval = rooms / (double)rate;
            long start = Stopwatch.GetTimestamp();
            var paced = await Task.WhenAll(Enumerable.Range(0, rooms).Select(async i =>
            {
                var latencies = new List<double>();
                double offset = interval * i / rooms;
                for (int k = 0; ; k++)
                {
                    var due = TimeSpan.FromSeconds(offset + k * interval);
                    if (due >= duration)
                    {
                        break;
                    }
                    var wait = due - Stopwatch.GetElapsedTime(start);
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait);
                    }
                    await executors[i].ExecuteAsync(togglers[i].Next());
                    latencies.Add((Stopwatch.GetElapsedTime(start) - due).TotalMilliseconds);
                }
                return latencies;
            }));
            Report.Latency($"匀速 {rate}/s", paced, duration, journal);
        }

        await Task.WhenAll(executors.Select(executor => executor.StopAsync()));
    }
}
