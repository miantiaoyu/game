using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace DarkTide.Server;

/// <summary>
/// 就绪检查的一部分：数据库不可达时不接新流量。命令要写日志才能确认，数据库不通时房间无法确认任何操作。
/// 只用于就绪、不用于存活：数据库短暂不可用时重启进程无济于事。
/// </summary>
internal sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception error) when (error is NpgsqlException or OperationCanceledException or TimeoutException)
        {
            // 不把异常细节（可能含主机名）放进响应；详情只进日志。
            return HealthCheckResult.Unhealthy("数据库不可达。", error);
        }
    }
}
