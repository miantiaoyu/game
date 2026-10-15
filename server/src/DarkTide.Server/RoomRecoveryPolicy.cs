namespace DarkTide.Server;

/// <summary>
/// 故障房间的自动重建策略。只对持久化故障（<see cref="DarkTide.Room.RoomPersistenceException"/>）生效：
/// 数据库闪断、超时这类问题是暂时的，日志是可信的状态来源，从日志重建即可。
/// 规则代码抛出的意外异常不自动重建：同一命令重发会再次触发同一缺陷，自动重建只会让全桌反复掉线。
/// </summary>
/// <param name="InitialDelay">第一次重建前等待多久；之后每次失败翻倍。</param>
/// <param name="MaxDelay">两次重建尝试之间最长等待多久。数据库长时间不可用时按这个间隔持续重试。</param>
/// <param name="MaxFaultsInWindow">同一房间在 <paramref name="FaultWindow"/> 内最多故障几次；超过即停止自动重建，留给人工处理。</param>
/// <param name="FaultWindow">统计故障次数的时间窗口。</param>
public sealed record RoomRecoveryPolicy(TimeSpan InitialDelay, TimeSpan MaxDelay, int MaxFaultsInWindow, TimeSpan FaultWindow)
{
    // 首次等 1 秒：给数据库和连接池一点恢复时间，同时让可能仍在途的旧写入先有结果。
    // 10 分钟内 5 次：数据库偶发闪断远低于这个频率；超过说明问题不是暂时的。
    public static RoomRecoveryPolicy Default { get; } = new(
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), MaxFaultsInWindow: 5, FaultWindow: TimeSpan.FromMinutes(10));
}
