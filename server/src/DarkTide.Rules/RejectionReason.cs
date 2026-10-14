namespace DarkTide.Rules;

/// <summary>
/// 业务拒绝的稳定代码。网络层按代码组装回执，不解析提示文字。
/// 新增可以追加；已发布的成员不改名、不复用编号。
/// </summary>
public enum RejectionReason
{
    SeatOutOfRange = 1,
    SeatTaken = 2,
    AlreadySeated = 3,
    NotSeated = 4,
    StageNotAllowed = 5,
    EmptySeatInLineup = 6,
    /// <summary>提交时看到的状态已过时，需刷新后由玩家重新决定。</summary>
    VersionMismatch = 7,
    /// <summary>同一命令编号对应了不同内容；原命令的结果不变。</summary>
    CommandIdConflict = 8
}
