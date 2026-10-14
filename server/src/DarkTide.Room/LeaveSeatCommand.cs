using DarkTide.Rules;

namespace DarkTide.Room;

public sealed record LeaveSeatCommand : RoomCommand
{
    public Guid PlayerId { get; }

    public LeaveSeatCommand(Guid commandId, Guid playerId, long? expectedVersion = null)
        : base(commandId, expectedVersion)
    {
        RequirePlayerId(playerId);
        this.PlayerId = playerId;
    }
}
