namespace DarkTide.Room;

/// <summary>
/// 房间执行器已因意外异常停止，本次请求没有执行。
/// 与繁忙不同：稍后重试同一个执行器也不会成功，需要上层恢复或重建房间。
/// </summary>
public sealed class RoomFaultedException : Exception
{
    public RoomFaultedException(Exception FaultReason)
        : base("房间执行器已因故障停止，请求未执行。", FaultReason)
    {
    }
}
