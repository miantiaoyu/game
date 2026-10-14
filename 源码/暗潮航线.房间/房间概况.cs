namespace 暗潮航线.房间;

/// <summary>
/// 房间的公开信息快照，任何人都可以看到，不含底牌。
/// 与底牌视图配合使用：座位有人但不在参与顺序里，就是“已入座、本局未参与”；座位没人就是空座。
/// 客户端提交命令时，把这里的状态版本作为预期版本带上。
/// </summary>
public sealed record 房间概况(
    Guid 房间编号,
    房间阶段 阶段,
    long 状态版本,
    IReadOnlyList<bool> 座位有人,
    IReadOnlyList<int> 本局参与座位顺序);
