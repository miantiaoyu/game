namespace DarkTide.Room;

/// <summary>
/// 房间行为的版本：规则（同一命令序列得到什么状态与结果）与命令持久格式。建房时写入日志，恢复时核对。
///
/// 持久化靠按顺序重放命令重建房间，所以**任何改变重放结果的修改都必须把 <see cref="Current"/> 加一**：
/// 改变规则判定、拒绝原因、版本号的推进方式、命令编解码，都算。纯重构、日志文字、性能优化不算。
/// 不加一的后果是：旧房间重放时才发现结果对不上，被拒绝加载；加了一，启动时就能按版本识别出来，
/// 提前报告“需要排空或保留旧版本处理”，而不是在玩家重连时才失败。
///
/// 只追加，不复用编号。版本号记在 rooms.rules_version，与 CommandCodec.FormatVersion 是两件事：
/// 后者只描述单条命令的 JSON 形状，前者覆盖整个重放语义。
/// </summary>
public static class RulesVersion
{
    /// <summary>
    /// 1：本列加入前创建的房间（迁移时回填）。只有入座、离座、发一次牌。
    /// 2：发牌后立即收底注并开始一注；下注、改航命令；本局结束自动结算；可连续发多局直到整场结束；
    ///    首局起空座筹码为 0。版本 1 的房间发牌后版本号推进方式不同，不能用本版本重放。
    /// 3：首局名单必须按顺时针排列，之后每局名单必须严格按起始标记顺移（版本 2 只检查成员）；新增超时命令。
    /// </summary>
    public const int Current = 3;

    /// <summary>本版本代码能正确重放的版本。目前只能重放自己；将来若新版本仍兼容某个旧版本，把它加进来。</summary>
    public static bool CanReplay(int version) => version == Current;
}

/// <summary>日志里的房间由另一个规则版本创建，当前代码不能保证重放结果一致，拒绝加载。</summary>
public sealed class RulesVersionMismatchException(Guid roomId, int storedVersion)
    : Exception($"房间 {roomId} 由规则版本 {storedVersion} 创建，当前代码为版本 {RulesVersion.Current}，不能重放。需要用对应版本的服务处理完，或做版本迁移。")
{
    public Guid RoomId { get; } = roomId;
    public int StoredVersion { get; } = storedVersion;
}
