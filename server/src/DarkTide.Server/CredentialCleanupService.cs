using DarkTide.Persistence;

namespace DarkTide.Server;

/// <summary>
/// 定期删除已过期的重连凭据：每次使用都轮换出新的一张，不清理的话表随时间线性增长。
/// 分批删除，每批之间让出：不在一条语句里长时间持锁，也不和对局写入抢落盘。
/// </summary>
internal sealed class CredentialCleanupService(IPlayerCredentialStore store, TimeProvider time, ILogger<CredentialCleanupService> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    public const int BatchSize = 1000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, time, stoppingToken).ConfigureAwait(false);
                int total = 0;
                int deleted;
                do
                {
                    deleted = await store.DeleteExpiredAsync(time.GetUtcNow(), BatchSize, stoppingToken).ConfigureAwait(false);
                    total += deleted;
                    await Task.Delay(TimeSpan.FromMilliseconds(50), time, stoppingToken).ConfigureAwait(false);
                }
                while (deleted == BatchSize);
                if (total > 0)
                {
                    logger.LogInformation("Deleted {Count} expired refresh credentials.", total);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                // 清理失败不影响服务：下一轮再试。
                logger.LogWarning(error, "Deleting expired refresh credentials failed; will retry next cycle.");
            }
        }
    }
}
