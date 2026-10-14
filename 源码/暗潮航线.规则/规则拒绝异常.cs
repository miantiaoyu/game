namespace 暗潮航线.规则;

/// <summary>
/// 请求在检查阶段被规则拒绝，抛出前没有修改任何状态。
/// 只有这一类异常允许房间继续处理后续请求；其他异常一律视为故障。
/// </summary>
public sealed class 规则拒绝异常 : Exception
{
    public 拒绝原因 原因 { get; }

    public 规则拒绝异常(拒绝原因 原因, string 说明) : base(说明)
    {
        this.原因 = 原因;
    }
}
