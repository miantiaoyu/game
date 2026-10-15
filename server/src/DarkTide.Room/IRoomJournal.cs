namespace DarkTide.Room;

/// <summary>
/// 房间的持久命令日志。每条已接收命令的最终结果（执行或规则拒绝）按顺序写入一次；
/// 重启后按顺序重放即可重建房间，并继续识别重发的命令编号。
///
/// 实现必须保证：<see cref="AppendAsync"/> 返回即已持久提交；同一房间的序号与命令编号都唯一，
/// 重复写入要以异常失败而不是覆盖——两个进程同时写同一房间时，后写的一方因此失败并停止，而不是悄悄分叉。
/// </summary>
public interface IRoomJournal
{
    /// <summary>登记新房间，同时记下 <see cref="RulesVersion.Current"/>。房间编号已存在时抛 <see cref="RoomAlreadyExistsException"/>。</summary>
    Task CreateRoomAsync(Guid roomId, int seatCount, CancellationToken cancellationToken);

    /// <summary>追加一条命令结果。<see cref="JournalEntry.Sequence"/> 必须紧接上一条。</summary>
    Task AppendAsync(Guid roomId, JournalEntry entry, CancellationToken cancellationToken);

    /// <summary>按命令编号查找已记录的结果；没有时返回 null。用于缓存淘汰后的去重。</summary>
    Task<JournalEntry?> FindAsync(Guid roomId, Guid commandId, CancellationToken cancellationToken);

    /// <summary>读取房间的全部记录，按序号升序。房间不存在时返回 null。</summary>
    Task<StoredRoom?> LoadAsync(Guid roomId, CancellationToken cancellationToken);

    /// <summary>尚未关闭的房间编号，进程启动时据此恢复。</summary>
    Task<IReadOnlyList<Guid>> ListOpenRoomsAsync(CancellationToken cancellationToken);

    /// <summary>尚未关闭的房间按规则版本计数。启动时据此提前发现当前代码不能重放的房间。</summary>
    Task<IReadOnlyDictionary<int, int>> CountOpenRoomsByRulesVersionAsync(CancellationToken cancellationToken);

    /// <summary>标记房间已关闭，之后启动时不再恢复它。重复调用无害。</summary>
    Task MarkClosedAsync(Guid roomId, CancellationToken cancellationToken);
}

/// <summary>一条命令及其最终结果。序号从 1 开始，按处理顺序连续递增。</summary>
public sealed record JournalEntry(long Sequence, RoomCommand Command, CommandOutcome Outcome);

/// <summary>从日志读回的房间：创建参数、创建时的规则版本与全部记录。</summary>
public sealed record StoredRoom(Guid RoomId, int SeatCount, IReadOnlyList<JournalEntry> Entries)
{
    public int RulesVersion { get; init; } = DarkTide.Room.RulesVersion.Current;
}

/// <summary>
/// 命令的最终结果：执行成功（带提交后的状态版本）或被规则拒绝（带原因代码与说明）。
/// 只有这两种结果会写入日志；故障不是结果，故障命令不写入，重启后也不会被重放。
/// </summary>
public sealed record CommandOutcome
{
    private CommandOutcome(long? appliedVersion, RejectionReasonValue? rejection)
    {
        AppliedVersion = appliedVersion;
        Rejection = rejection;
    }

    public long? AppliedVersion { get; }
    public RejectionReasonValue? Rejection { get; }
    public bool IsApplied => AppliedVersion.HasValue;

    public static CommandOutcome Applied(long version)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        return new CommandOutcome(version, null);
    }

    public static CommandOutcome Rejected(DarkTide.Rules.RejectionReason reason, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new CommandOutcome(null, new RejectionReasonValue(reason, message));
    }

    /// <summary>重放：成功返回原回执，拒绝则抛出原拒绝。</summary>
    public CommandReceipt Replay(Guid commandId) => AppliedVersion is long version
        ? new CommandReceipt(commandId, version)
        : throw new DarkTide.Rules.RuleRejectedException(Rejection!.Reason, Rejection.Message);
}

public sealed record RejectionReasonValue(DarkTide.Rules.RejectionReason Reason, string Message);

/// <summary>日志里已经有这个房间编号。</summary>
public sealed class RoomAlreadyExistsException(Guid roomId)
    : Exception($"房间 {roomId} 已存在。")
{
    public Guid RoomId { get; } = roomId;
}

/// <summary>
/// 命令已在内存中执行，但结果没能确认写入日志。结果**未知**：可能已经提交，也可能没有。
/// 房间随之停止，不再在可疑状态上继续处理。房间从日志恢复后，用同一命令编号重试是安全的：
/// 若原提交已成功，会得到原结果；若没有，命令重新执行一次。
/// </summary>
public sealed class RoomPersistenceException(Guid commandId, Exception inner)
    : Exception($"命令 {commandId} 的结果未能确认写入，结果未知；房间已停止，请在恢复后用同一命令编号重试。", inner)
{
    public Guid CommandId { get; } = commandId;
}

/// <summary>重放日志得到的结果与记录不一致：规则代码与日志不匹配，拒绝加载，避免在分叉的状态上继续。</summary>
public sealed class JournalReplayMismatchException(Guid roomId, long sequence, string detail)
    : Exception($"房间 {roomId} 的第 {sequence} 条记录重放结果与日志不一致：{detail}。拒绝加载。")
{
    public Guid RoomId { get; } = roomId;
    public long Sequence { get; } = sequence;
}
