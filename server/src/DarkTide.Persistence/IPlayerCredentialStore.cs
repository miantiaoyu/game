namespace DarkTide.Persistence;

/// <summary>重连凭据换新的结果。</summary>
public enum RotationStatus
{
    /// <summary>已换成新凭据（后继）。</summary>
    Rotated = 1,
    /// <summary>
    /// 旧凭据刚在宽限期内换过，后继还没被用过：多半是客户端没收到上次的响应而重发。
    /// 后继由旧凭据确定性地导出（见 SessionService），所以调用方重新算出的就是上次那一张，原样返回即可。
    /// </summary>
    AlreadyRotated = 2,
    /// <summary>已换过的旧凭据在宽限期外、或后继已被使用之后再次出现：凭据可能被盗。该玩家的全部凭据已吊销。</summary>
    ReuseDetected = 3,
    /// <summary>不存在、已过期、已吊销，或玩家已被封禁。</summary>
    Invalid = 4
}

/// <param name="SuccessorExpiresAt">Rotated／AlreadyRotated 时：后继的过期时间。</param>
/// <param name="SuccessorIndex">Rotated 时为 0；AlreadyRotated 时是候选后继里与上次一致的那一个的下标。</param>
public readonly record struct RotationResult(RotationStatus Status, Guid PlayerId, DateTimeOffset SuccessorExpiresAt, int SuccessorIndex = 0)
{
    public static RotationResult Invalid => new(RotationStatus.Invalid, Guid.Empty, default);
}

/// <summary>
/// 玩家身份与重连凭据。只接触凭据的 SHA-256 摘要，从不接触原文：存储泄露时摘要不能直接冒充玩家。
/// 所有方法都可以并发调用；同一张凭据的并发换新由存储串行化。
/// </summary>
public interface IPlayerCredentialStore
{
    /// <summary>创建游客玩家及其第一张凭据。</summary>
    Task CreateGuestAsync(Guid playerId, byte[] credentialHash, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>
    /// 用旧凭据换后继：同一事务里标记旧的已替换并写入后继。后继必须由旧凭据确定性导出，
    /// 这样重发得到同一个后继，存储只需比较摘要是否一致，不必保存后继原文。
    /// <paramref name="successorHashes"/> 是按每把已配置密钥导出的候选，第一个用于本次换新；
    /// 重发时与其中任何一个一致都算同一个后继——换签名密钥前后的重发不会被误判为盗用。
    /// </summary>
    Task<RotationResult> RotateAsync(byte[] oldHash, IReadOnlyList<byte[]> successorHashes, DateTimeOffset now, DateTimeOffset successorExpiresAt,
        TimeSpan grace, CancellationToken cancellationToken);

    /// <summary>删除最多 <paramref name="batchSize"/> 张已过期的凭据，返回删除数。分批是为了不在一条语句里长时间持锁。</summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken);
}

/// <summary>换新的判定顺序，两种存储共用，保证行为一致。</summary>
public static class CredentialRules
{
    public sealed record Snapshot(
        DateTimeOffset ExpiresAt, DateTimeOffset? ReplacedAt, byte[]? ReplacedBy, DateTimeOffset? RevokedAt, DateTimeOffset? DisabledAt);

    /// <param name="successorUsed">后继是否已被替换或吊销；只在旧凭据已被替换时有意义。</param>
    public static RotationStatus Decide(Snapshot old, bool successorUsed, IReadOnlyList<byte[]> successorHashes, DateTimeOffset now, TimeSpan grace) =>
        Decide(old, successorUsed, successorHashes, now, grace, out _);

    public static RotationStatus Decide(Snapshot old, bool successorUsed, IReadOnlyList<byte[]> successorHashes, DateTimeOffset now, TimeSpan grace,
        out int matchedIndex)
    {
        matchedIndex = 0;
        // 先看吊销与封禁：发现重复使用后整个家族都已吊销，宽限期内的重发也不再放行。
        if (old.RevokedAt is not null || old.DisabledAt is not null || old.ExpiresAt <= now)
        {
            return RotationStatus.Invalid;
        }
        if (old.ReplacedAt is not DateTimeOffset replaced)
        {
            return RotationStatus.Rotated;
        }
        // 重发：在宽限期内、后继一致（某一把已配置密钥导出的）、且后继还没被用过。三条缺一条都按盗用处理。
        matchedIndex = -1;
        for (int i = 0; old.ReplacedBy is not null && i < successorHashes.Count; i++)
        {
            if (old.ReplacedBy.AsSpan().SequenceEqual(successorHashes[i]))
            {
                matchedIndex = i;
                break;
            }
        }
        return now - replaced <= grace && matchedIndex >= 0 && !successorUsed
            ? RotationStatus.AlreadyRotated
            : RotationStatus.ReuseDetected;
    }
}
