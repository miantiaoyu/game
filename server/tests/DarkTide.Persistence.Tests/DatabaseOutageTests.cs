using System.Diagnostics.Metrics;
using DarkTide.Room;
using DarkTide.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DarkTide.Persistence.Tests;

/// <summary>
/// 真实的数据库中断：暂停 PostgreSQL 容器，房间写入超时；恢复容器后，房间应自动从日志重建，
/// 不需要重启进程。用独立容器，不影响其他测试共享的数据库。
/// </summary>
public class DatabaseOutageTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        await SchemaMigrator.MigrateAsync(_dataSource);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task RoomComesBackOnItsOwnAfterTheDatabaseReturns()
    {
        await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var directory = new RoomDirectory(
            Options.Create(new RoomOptions { JournalTimeout = TimeSpan.FromSeconds(1) }),
            NullLoggerFactory.Instance,
            services.GetRequiredService<IMeterFactory>(),
            new PostgresRoomJournal(_dataSource),
            new RoomRecoveryPolicy(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), MaxFaultsInWindow: 5, FaultWindow: TimeSpan.FromMinutes(1)));
        var roomId = Guid.NewGuid();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var room = await directory.CreateRoomAsync(roomId, 4);
        await room.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), alice, 1));

        // 数据库整体停顿（进程暂停，连接不断开但不响应）：写入在 1 秒内超时，结果未知，房间停止。
        await _container.PauseAsync();
        var lost = new TakeSeatCommand(Guid.NewGuid(), bob, 2);
        await Assert.ThrowsAsync<RoomPersistenceException>(() => room.ExecuteAsync(lost).WaitAsync(TimeSpan.FromSeconds(10)));
        await room.Stopped.WaitAsync(TimeSpan.FromSeconds(5));

        // 数据库仍不可用期间，重建不断失败、按退避重试，编号保持占用。
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.True(directory.IsRecovering(roomId));
        Assert.False(directory.TryGetRoom(roomId, out _));

        await _container.UnpauseAsync();

        RoomExecutor? rebuilt = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!(directory.TryGetRoom(roomId, out rebuilt) && !ReferenceEquals(rebuilt, room)))
        {
            Assert.True(DateTime.UtcNow < deadline, "数据库恢复后 30 秒内房间没有自动重建。");
            await Task.Delay(50);
        }

        Assert.Equal<int?>(1, await rebuilt!.FindSeatAsync(alice));
        // 停顿时那条写入可能已经提交，也可能没有（结果未知）。无论哪种，用同一编号重试都得到同一个结果：
        // 已提交则命中日志、返回原回执；未提交则正常执行一次。Bob 最终恰好坐在 2 号座。
        await rebuilt.ExecuteAsync(lost);
        Assert.Equal<int?>(2, await rebuilt.FindSeatAsync(bob));
        await directory.StopAsync(CancellationToken.None);
    }
}
