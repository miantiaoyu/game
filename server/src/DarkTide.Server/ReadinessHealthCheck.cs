using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DarkTide.Server;

/// <summary>
/// 就绪检查：进程正在关闭时报告不健康，让负载均衡器停止派新流量。
/// 存活检查（/health/live）与它分开：关闭中的进程仍然“活着”，不能被编排器当成卡死强杀。
/// </summary>
internal sealed class ReadinessHealthCheck(RoomDirectory directory, IHostApplicationLifetime lifetime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (lifetime.ApplicationStopping.IsCancellationRequested || directory.IsStopping)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("进程正在关闭。"));
        }

        // 数据只在详细输出里可见；默认响应只有 Healthy／Unhealthy，不对外暴露房间数。
        var data = new Dictionary<string, object> { ["rooms"] = directory.Count };
        return Task.FromResult(HealthCheckResult.Healthy(data: data));
    }
}
