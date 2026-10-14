namespace DarkTide.Rules;

/// <summary>
/// 请求在检查阶段被规则拒绝，抛出前没有修改任何状态。
/// 只有这一类异常允许房间继续处理后续请求；其他异常一律视为故障。
/// </summary>
public sealed class RuleRejectedException : Exception
{
    public RejectionReason Reason { get; }

    public RuleRejectedException(RejectionReason reason, string message) : base(message)
    {
        this.Reason = reason;
    }
}
