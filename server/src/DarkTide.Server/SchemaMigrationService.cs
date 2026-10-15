using Npgsql;

namespace DarkTide.Server;

/// <summary>
/// 启动时的数据库准备。注册在 <see cref="RoomDirectory"/> 之前：宿主按注册顺序启动托管服务，恢复房间前表一定已经存在。
/// 配置了迁移账号时先迁移并授权（开发与单机部署方便）；没配置时只检查结构是否最新，缺迁移就拒绝启动。
/// </summary>
internal sealed class SchemaMigrationService(
    NpgsqlDataSource runtime,
    IConfiguration configuration,
    ILogger<SchemaMigrationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var migrationConnectionString = configuration.GetConnectionString(ServerDefaults.MigrationConnectionStringName);
        if (!string.IsNullOrWhiteSpace(migrationConnectionString))
        {
            var runtimeConnectionString = configuration.GetConnectionString(ServerDefaults.ConnectionStringName)!;
            await DatabaseSetup.MigrateAndGrantAsync(migrationConnectionString, runtimeConnectionString, logger, cancellationToken);
        }

        await DatabaseSetup.VerifyRuntimeAsync(runtime, logger, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
