namespace 暗潮航线.房间;

/// <summary>请求未进入房间队列，也没有执行。调用方可以稍后重新尝试。</summary>
public sealed class 房间繁忙异常 : Exception
{
    public 房间繁忙异常() : base("房间等待队列已满，请稍后重试。")
    {
    }
}
