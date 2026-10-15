using System.Diagnostics;
using DarkTide.Persistence;
using DarkTide.Room;
using Npgsql;

namespace DarkTide.Benchmarks;

/// <summary>
/// 模拟数据库卡住：另一个事务对命令表加排他锁，房间的写入因此阻塞。测从提交命令到房间报告失败要多久。
/// 这段时间里该房间的消费循环被占住，后面的请求全部排队。
/// </summary>
internal static class StalledDatabase
{
    public static async Task RunAsync(NpgsqlDataSource dataSource)
    {
        Console.WriteLine("== 数据库卡住：命令表被另一个事务锁住 ==");
        var executor = await RoomExecutor.CreatePersistentAsync(Guid.NewGuid(), 4, new PostgresRoomJournal(dataSource));

        await using var locker = await dataSource.OpenConnectionAsync();
        await using var transaction = await locker.BeginTransactionAsync();
        await using (var lockTable = new NpgsqlCommand("LOCK TABLE room_commands IN ACCESS EXCLUSIVE MODE", locker, transaction))
        {
            await lockTable.ExecuteNonQueryAsync();
        }

        var watch = Stopwatch.StartNew();
        try
        {
            await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Guid.NewGuid(), 0));
            Console.WriteLine("意外：命令在表被锁住时成功了。");
        }
        catch (RoomPersistenceException error)
        {
            Console.WriteLine($"{watch.Elapsed.TotalSeconds:F1} s 后报告结果未知（{error.InnerException?.GetType().Name}: {error.InnerException?.Message}）");
        }

        await transaction.RollbackAsync();
        await executor.StopAsync();
    }
}
