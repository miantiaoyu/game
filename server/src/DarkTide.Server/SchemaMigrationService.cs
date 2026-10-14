using DarkTide.Persistence;
using Npgsql;

namespace DarkTide.Server;

/// <summary>
/// 启动时执行结构迁移。注册在 <see cref="RoomDirectory"/> 之前：宿主按注册顺序启动托管服务，
/// 恢复房间时表一定已经存在。迁移失败时进程不启动。
/// </summary>
internal sealed class SchemaMigrationService(NpgsqlDataSource dataSource, ILogger<SchemaMigrationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var applied = await SchemaMigrator.MigrateAsync(dataSource, cancellationToken);
        if (applied.Count > 0)
        {
            logger.LogInformation("Applied database migrations: {Migrations}.", string.Join(", ", applied));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
