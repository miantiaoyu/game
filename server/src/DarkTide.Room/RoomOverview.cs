namespace DarkTide.Room;

/// <summary>
/// 房间的公开信息快照，任何人都可以看到，不含底牌。
/// 与底牌视图配合使用：座位有人但不在参与顺序里，就是“已入座、本局未参与”；座位没人就是空座。
/// 客户端提交命令时，把这里的状态版本作为预期版本带上。
/// </summary>
public sealed record RoomOverview(
    Guid RoomId,
    RoomStage Stage,
    long StateVersion,
    IReadOnlyList<bool> SeatOccupied,
    IReadOnlyList<int> ParticipantOrder);
