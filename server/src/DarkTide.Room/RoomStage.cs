namespace DarkTide.Room;

/// <summary>
/// 房间当前所处阶段。数值将进入协议与存储：只追加，不改名、不复用编号。
/// </summary>
public enum RoomStage
{
    WaitingForSeats = 1,
    /// <summary>一局正在进行（已发底牌、尚未结算）。名称沿用最初的“已发底牌”，数值已进入协议，不改名。</summary>
    CardsDealt = 2,
    /// <summary>上一局已结算，等待可信流程发下一局。</summary>
    HandSettled = 3,
    /// <summary>整场结束：打满局数，或入座玩家中只剩一人有筹码。</summary>
    MatchFinished = 4
}
