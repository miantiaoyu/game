namespace DarkTide.Room;

/// <summary>
/// 当前行动者超时：能过牌就过牌，否则弃牌；改航窗口里则放弃改航。由房间执行器的计时器在期限到达时发出。
///
/// 超时的判断（看墙上时钟）只发生在执行器里；写进日志的是这条确定的命令，重放时不再看时钟，结果完全由命令序列决定。
/// 必须带预期版本：它只对“发出时看到的那一个行动机会”有效。玩家抢在前面行动了，版本就变了，这条命令被拒绝而不是误伤下一个人。
/// </summary>
public sealed record TimeoutCommand : RoomCommand
{
    public TimeoutCommand(Guid commandId, long expectedVersion)
        : base(commandId, expectedVersion)
    {
    }
}
