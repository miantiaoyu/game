using DarkTide.Rules;

namespace DarkTide.Room;

/// <summary>
/// 下注动作。玩家编号必须来自已认证身份；座位由房间按身份查出，客户端不能代别人下注。
/// 加注时 <see cref="RaiseTarget"/> 是本轮累计目标额；其他动作必须为 0，这样“同一操作”只有一种写法，去重才可靠。
/// 是否轮到本人、金额是否合法，由单局按当前状态判断。
/// </summary>
public sealed record BetCommand : RoomCommand
{
    // 只挡住明显错误的载荷；真实上限由底池限注规则决定。
    public const int MaxRaiseTarget = 1_000_000;

    public Guid PlayerId { get; }
    public BettingAction Action { get; }
    public int RaiseTarget { get; }

    public override Guid? ActorId => PlayerId;

    public BetCommand(Guid commandId, Guid playerId, BettingAction action, int raiseTarget = 0, long? expectedVersion = null)
        : base(commandId, expectedVersion)
    {
        RequirePlayerId(playerId);
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "未知的下注动作。");
        }
        if (action == BettingAction.Raise)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(raiseTarget, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(raiseTarget, MaxRaiseTarget);
        }
        else if (raiseTarget != 0)
        {
            throw new ArgumentException("只有加注需要目标额。", nameof(raiseTarget));
        }

        this.PlayerId = playerId;
        this.Action = action;
        this.RaiseTarget = raiseTarget;
    }
}
