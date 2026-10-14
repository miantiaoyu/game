namespace DarkTide.Room;

/// <summary>请求未进入房间队列，也没有执行。调用方可以稍后重新尝试。</summary>
public sealed class RoomBusyException : Exception
{
    public RoomBusyException() : base("房间等待队列已满，请稍后重试。")
    {
    }
}
