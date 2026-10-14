namespace DarkTide.Room;

/// <summary>
/// 房间当前所处阶段。数值将进入协议与存储：只追加，不改名、不复用编号。
/// 整场、单局结算等阶段在实现对应规则时再加入。
/// </summary>
public enum RoomStage
{
    WaitingForSeats = 1,
    CardsDealt = 2
}
