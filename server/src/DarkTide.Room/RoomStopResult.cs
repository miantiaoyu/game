namespace DarkTide.Room;

/// <summary>
/// 一次停止的结果，供目录模块和运维日志判断关闭是否干净。
/// </summary>
/// <param name="Drained">所有已接收的请求都在时限内正常处理完，且没有发生故障。</param>
/// <param name="AbandonedRequests">
/// 被放弃的排队请求数（超时、取消或故障导致）。它们都没有执行，调用方收到异常，可以安全重试。
/// </param>
/// <param name="ConsumerStopped">
/// 消费循环是否已经结束。为 false 时可能仍有一条请求在执行，
/// 在 <see cref="RoomExecutor.Stopped"/> 完成之前不得把房间交给其他所有者。
/// </param>
public sealed record RoomStopResult(bool Drained, int AbandonedRequests, bool ConsumerStopped);
