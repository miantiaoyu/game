namespace DarkTide.Room;

/// <summary>
/// 改航窗口里轮到本人时的选择：<see cref="Reroute"/> 为 true 表示付冻结报价并换掉第二张公共牌，false 表示放弃。
/// 不带位置也不带金额：只能换第二张，费用取窗口开始时冻结的报价，客户端不能自行指定。
/// </summary>
public sealed record RerouteCommand : RoomCommand
{
    public Guid PlayerId { get; }
    public bool Reroute { get; }

    public override Guid? ActorId => PlayerId;

    public RerouteCommand(Guid commandId, Guid playerId, bool reroute, long? expectedVersion = null)
        : base(commandId, expectedVersion)
    {
        RequirePlayerId(playerId);
        this.PlayerId = playerId;
        this.Reroute = reroute;
    }
}
