using DarkTide.Rules;

namespace DarkTide.Room;

/// <summary>玩家编号必须来自已认证身份。座位号来自客户端，越界由房间以规则拒绝回应。</summary>
public sealed record TakeSeatCommand : RoomCommand
{
    public Guid PlayerId { get; }
    public int Seat { get; }

    public TakeSeatCommand(Guid commandId, Guid playerId, int seat, long? expectedVersion = null)
        : base(commandId, expectedVersion)
    {
        RequirePlayerId(playerId);
        this.PlayerId = playerId;
        this.Seat = seat;
    }
}
