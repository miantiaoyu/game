namespace DarkTide.Persistence;

/// <summary>没有数据库时（开发、测试）用的凭据存储。进程退出即丢失，与内存房间一致。一把锁串行化全部操作。</summary>
public sealed class InMemoryPlayerCredentialStore : IPlayerCredentialStore
{
    private sealed class Credential(Guid playerId, DateTimeOffset expiresAt)
    {
        public Guid PlayerId { get; } = playerId;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public DateTimeOffset? ReplacedAt;
        public byte[]? ReplacedBy;
        public DateTimeOffset? RevokedAt;
    }

    private readonly Lock _lock = new();
    // 键是摘要的十六进制：byte[] 作字典键按引用比较。
    private readonly Dictionary<string, Credential> _credentials = [];
    private readonly Dictionary<Guid, DateTimeOffset?> _disabledAt = [];

    public int CredentialCount
    {
        get { lock (_lock) { return _credentials.Count; } }
    }

    /// <summary>仅供测试：封禁玩家。生产环境由运维账号在数据库里设置。</summary>
    public void DisableForTest(Guid playerId, DateTimeOffset at)
    {
        lock (_lock) { _disabledAt[playerId] = at; }
    }

    public Task CreateGuestAsync(Guid playerId, byte[] credentialHash, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_disabledAt.TryAdd(playerId, null) || !_credentials.TryAdd(Convert.ToHexString(credentialHash), new Credential(playerId, expiresAt)))
            {
                throw new InvalidOperationException("玩家编号或凭据重复。");
            }
        }
        return Task.CompletedTask;
    }

    public Task<RotationResult> RotateAsync(byte[] oldHash, IReadOnlyList<byte[]> successorHashes, DateTimeOffset now, DateTimeOffset successorExpiresAt,
        TimeSpan grace, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(successorHashes.Count);
        byte[] successorHash = successorHashes[0];
        lock (_lock)
        {
            if (!_credentials.TryGetValue(Convert.ToHexString(oldHash), out var old))
            {
                return Task.FromResult(RotationResult.Invalid);
            }
            Credential? successor = old.ReplacedBy is null ? null : _credentials.GetValueOrDefault(Convert.ToHexString(old.ReplacedBy));
            bool successorUsed = old.ReplacedBy is not null && (successor is null || successor.ReplacedAt is not null || successor.RevokedAt is not null);
            var snapshot = new CredentialRules.Snapshot(old.ExpiresAt, old.ReplacedAt, old.ReplacedBy, old.RevokedAt, _disabledAt[old.PlayerId]);

            var status = CredentialRules.Decide(snapshot, successorUsed, successorHashes, now, grace, out int matched);
            switch (status)
            {
                case RotationStatus.Invalid:
                    return Task.FromResult(RotationResult.Invalid);
                case RotationStatus.AlreadyRotated:
                    return Task.FromResult(new RotationResult(status, old.PlayerId, successor!.ExpiresAt, matched));
                case RotationStatus.ReuseDetected:
                    foreach (var credential in _credentials.Values.Where(c => c.PlayerId == old.PlayerId && c.RevokedAt is null))
                    {
                        credential.RevokedAt = now;
                    }
                    return Task.FromResult(new RotationResult(status, old.PlayerId, default));
            }

            old.ReplacedAt = now;
            old.ReplacedBy = successorHash;
            _credentials.Add(Convert.ToHexString(successorHash), new Credential(old.PlayerId, successorExpiresAt));
            return Task.FromResult(new RotationResult(RotationStatus.Rotated, old.PlayerId, successorExpiresAt));
        }
    }

    public Task<int> DeleteExpiredAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var expired = _credentials.Where(pair => pair.Value.ExpiresAt < now).Take(batchSize).Select(pair => pair.Key).ToArray();
            foreach (var key in expired)
            {
                _credentials.Remove(key);
            }
            return Task.FromResult(expired.Length);
        }
    }
}
