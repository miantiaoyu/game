using System.Diagnostics.Metrics;

namespace DarkTide.Room;

/// <summary>
/// 房间层的进程级指标。用 BCL 自带的 System.Diagnostics.Metrics，不引入额外依赖：
/// 任何 OpenTelemetry 导出器都能直接订阅这个 Meter。
///
/// 标签里不出现房间编号：房间数量随时间增长，把它当标签会造成高基数，压垮指标后端。
/// 房间维度的排查走日志，聚合统计走指标。
/// </summary>
internal static class RoomMetrics
{
    public static readonly Meter Meter = new("DarkTide.Room", "1.0.0");

    /// <summary>已处理的请求数，按结果和拒绝原因分类。</summary>
    public static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "darktide.room.requests",
        description: "已处理的房间请求数，outcome 为 applied／rejected／faulted");

    /// <summary>从出队到执行完成的耗时，用于观察消费循环是否被拖慢。</summary>
    public static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        "darktide.room.request.duration",
        unit: "ms",
        description: "房间请求的处理耗时");

    /// <summary>
    /// 未被接收就退回的提交。reason：busy（房间队列满）、player_pending（该玩家上一个命令还没结果）、
    /// rate_limited（该玩家超出速率）、stopped（已停止接收）。后两种按玩家的拒绝不写日志，只看这个计数。
    /// </summary>
    public static readonly Counter<long> RejectedSubmissions = Meter.CreateCounter<long>(
        "darktide.room.submissions.rejected",
        description: "未被接收的提交数");

    /// <summary>关闭时因超时或取消而放弃、没有执行的请求数，trigger 为 timeout／cancelled。</summary>
    public static readonly Counter<long> AbandonedRequests = Meter.CreateCounter<long>(
        "darktide.room.requests.abandoned",
        description: "关闭时被放弃、没有执行的排队请求数");

    /// <summary>消费循环因意外异常停止的次数。</summary>
    public static readonly Counter<long> Faults = Meter.CreateCounter<long>(
        "darktide.room.faults",
        description: "房间执行器故障停止的次数");
}
