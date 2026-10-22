using System.Diagnostics;
using DarkTide.Persistence;
using DarkTide.Room;
using DarkTide.Rules;
using Npgsql;

namespace DarkTide.Benchmarks;

/// <summary>
/// 真实对局负载：N 个 5 人房间同时开打，每名玩家按“思考时间”行动（下注、改航都有），计时器与自动发牌开着，
/// 所有命令写入真实 PostgreSQL。测：玩家动作的确认延迟、每秒命令数、内存，以及有没有房间卡住。
///
/// 和 SteadyLoad 的区别：那里只有入座／离座，这里走真实规则路径（发牌、下注、改航、结算、超时），
/// 且命令由“轮到谁谁行动”驱动，不是均匀发送。思考时间决定负载：真人约 3～10 秒，这里用较短的值把负载压高。
/// </summary>
internal static class GameplayLoad
{
    public static async Task RunAsync(NpgsqlDataSource dataSource, int rooms, TimeSpan think, TimeSpan duration)
    {
        Console.WriteLine($"== 对局负载：{rooms} 个 5 人房间，思考时间约 {think.TotalMilliseconds:F0} ms，持续 {duration.TotalSeconds:F0} s ==");
        var journal = new CountingJournal(new PostgresRoomJournal(dataSource));
        var options = new RoomOptions
        {
            PlayerCommandsPerSecond = RoomOptions.DefaultPlayerCommandsPerSecond,
            PlayerCommandBurst = RoomOptions.DefaultPlayerCommandBurst,
            // 超时比思考时间长得多：正常情况下不该触发；触发了说明房间或客户端跟不上。
            TurnTimeout = TimeSpan.FromSeconds(10),
            NextHandDelay = TimeSpan.FromMilliseconds(200),
        };

        long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        var executors = new RoomExecutor[rooms];
        var players = new Guid[rooms][];
        // 建房并行：每次写入都要等一次落盘（本机约 33 ms），逐个建 2000 个房间要好几分钟。
        var setup = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, rooms), new ParallelOptions { MaxDegreeOfParallelism = 64 }, async (i, _) =>
        {
            executors[i] = await RoomExecutor.CreatePersistentAsync(Guid.NewGuid(), 5, journal, options);
            players[i] = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
            for (int seat = 0; seat < 5; seat++)
            {
                await executors[i].ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), players[i][seat], seat));
            }
            await executors[i].StartMatchAsync();
        });
        Console.WriteLine($"  建房与开局用时 {setup.Elapsed.TotalSeconds:F1} s");

        journal.Reset();
        var stats = Enumerable.Range(0, rooms).Select(_ => new RoomStats()).ToArray();
        using var stop = new CancellationTokenSource(duration);
        var watch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, rooms).Select(i => Task.Run(() => DriveRoomAsync(
            executors[i], players[i], new Random(i), think, stats[i], stop.Token))));
        watch.Stop();

        Report.Latency("玩家动作", stats.Select(s => s.Latencies), watch.Elapsed, journal);
        Console.WriteLine($"  写日志 {journal.Appends / watch.Elapsed.TotalSeconds:F0} 条/秒（含超时与自动发牌）；"
            + $"繁忙拒绝 {stats.Sum(s => s.Busy)} 次；被超时抢先 {stats.Sum(s => s.Preempted)} 次；打完的整场 {stats.Count(s => s.Finished)} 场");
        long memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        Console.WriteLine($"  常驻内存约 {(memoryAfter - memoryBefore) / 1024.0 / rooms:F0} KiB/房间");

        foreach (var executor in executors)
        {
            await executor.StopAsync();
        }
    }

    private sealed class RoomStats
    {
        public List<double> Latencies { get; } = [];
        public int Busy;
        public int Preempted;
        public bool Finished;
    }

    private static async Task DriveRoomAsync(RoomExecutor executor, Guid[] players, Random random, TimeSpan think, RoomStats stats, CancellationToken stop)
    {
        long lastVersion = -1;
        var stuckSince = Stopwatch.GetTimestamp();
        while (!stop.IsCancellationRequested)
        {
            var overview = await executor.CreateOverviewAsync();
            if (overview.Stage == RoomStage.MatchFinished)
            {
                stats.Finished = true;
                return;
            }
            if (overview.StateVersion != lastVersion)
            {
                lastVersion = overview.StateVersion;
                stuckSince = Stopwatch.GetTimestamp();
            }
            else if (Stopwatch.GetElapsedTime(stuckSince) > TimeSpan.FromSeconds(30))
            {
                throw new InvalidOperationException($"房间 {executor.RoomId} 30 秒没有任何进展，卡在 {overview.Stage}。");
            }

            var command = overview.Stage == RoomStage.CardsDealt ? ChooseMove(overview, players, random) : null;
            if (command is null)
            {
                // 局间：等自动发牌。
                await Task.Delay(50, stop).ContinueWith(_ => { });
                continue;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(think.TotalMilliseconds * (0.5 + random.NextDouble())), stop).ContinueWith(_ => { });
            if (stop.IsCancellationRequested)
            {
                return;
            }
            long started = Stopwatch.GetTimestamp();
            try
            {
                await executor.ExecuteAsync(command);
                stats.Latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (RuleRejectedException error) when (error.Reason is RejectionReason.VersionMismatch or RejectionReason.NotYourTurn)
            {
                // 思考期间超时已经替他行动了。
                stats.Preempted++;
            }
            catch (RoomBusyException)
            {
                stats.Busy++;
            }
        }
    }

    private static RoomCommand ChooseMove(RoomOverview overview, Guid[] players, Random random)
    {
        var hand = overview.Hand!;
        if (hand.RerouteActingSeat >= 0)
        {
            int seat = hand.RerouteActingSeat;
            int fee = hand.RerouteQuotes.Single(quote => quote.Seat == seat).Fee;
            return new RerouteCommand(Guid.NewGuid(), players[seat], hand.Stacks[seat] >= fee && random.Next(3) == 0, overview.StateVersion);
        }
        var legal = hand.LegalActions!.Value;
        var player = players[hand.ActingSeat];
        int roll = random.Next(10);
        if (legal.CanRaise && roll == 0)
        {
            return new BetCommand(Guid.NewGuid(), player, BettingAction.Raise, legal.MinRaiseTarget, overview.StateVersion);
        }
        if (legal.CanFold && roll <= 2)
        {
            return new BetCommand(Guid.NewGuid(), player, BettingAction.Fold, 0, overview.StateVersion);
        }
        if (legal.CanCheck)
        {
            return new BetCommand(Guid.NewGuid(), player, BettingAction.Check, 0, overview.StateVersion);
        }
        return new BetCommand(Guid.NewGuid(), player, legal.CanCall ? BettingAction.Call : BettingAction.AllIn, 0, overview.StateVersion);
    }
}
