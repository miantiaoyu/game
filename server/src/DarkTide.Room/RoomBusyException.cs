namespace DarkTide.Room;

/// <summary>请求为什么没有被接收。数值将进入协议：只追加，不改名、不复用编号。</summary>
public enum BusyReason
{
    /// <summary>房间等待队列已满。</summary>
    QueueFull = 1,
    /// <summary>该玩家在本房间已有一个尚未得到结果的命令；等它完成后再提交新的。</summary>
    PlayerCommandPending = 2,
    /// <summary>该玩家提交过快，超出速率限制。</summary>
    RateLimited = 3
}

/// <summary>
/// 请求未进入房间队列，也没有执行。调用方可以退避后重新尝试；重试同一意图要保留原命令编号。
/// 注意：这只说明“这一次没有被接收”，不能证明同编号的更早一次请求没有提交。
/// </summary>
public sealed class RoomBusyException : Exception
{
    public RoomBusyException(BusyReason reason, TimeSpan? retryAfter = null) : base(Describe(reason))
    {
        Reason = reason;
        RetryAfter = retryAfter;
    }

    public BusyReason Reason { get; }

    /// <summary>建议的最短退避时间；只有速率限制时才有。</summary>
    public TimeSpan? RetryAfter { get; }

    private static string Describe(BusyReason reason) => reason switch
    {
        BusyReason.QueueFull => "房间等待队列已满，请稍后重试。",
        BusyReason.PlayerCommandPending => "上一个操作还没有结果，请等待它完成后再提交。",
        BusyReason.RateLimited => "操作过于频繁，请稍后重试。",
        _ => "房间暂时无法接收请求，请稍后重试。"
    };
}
