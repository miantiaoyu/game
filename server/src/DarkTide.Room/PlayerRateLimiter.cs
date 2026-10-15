using System.Collections.Concurrent;

namespace DarkTide.Room;

/// <summary>
/// 按玩家的令牌桶：允许 <c>burst</c> 次的短时突发，持续速率不超过每秒 <c>perSecond</c> 次。
/// 可以被多个线程同时调用。只管一个房间内的预算；跨房间、跨节点的身份预算属于网络入口，尚未实现。
///
/// 内存有上限：跟踪的玩家数超过 <see cref="MaxTrackedPlayers"/> 时，先清掉已经回满（即一段时间没用）的桶；
/// 清完仍超过，就拒绝新玩家而不是无限增长。正常房间最多 8 个座位，只有被大量身份刷请求时才会碰到这个上限，
/// 那时真正的防线应当在入口按会话限流。
/// </summary>
internal sealed class PlayerRateLimiter
{
    public const int MaxTrackedPlayers = 256;

    private sealed class Bucket(double tokens, long updatedAt)
    {
        public double Tokens = tokens;
        public long UpdatedAt = updatedAt;
    }

    private readonly ConcurrentDictionary<Guid, Bucket> _buckets = new();
    private readonly double _perSecond;
    private readonly int _burst;
    private readonly TimeProvider _time;
    private readonly Lock _pruneLock = new();

    public PlayerRateLimiter(double perSecond, int burst, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perSecond);
        ArgumentOutOfRangeException.ThrowIfLessThan(burst, 1);
        _perSecond = perSecond;
        _burst = burst;
        _time = time;
    }

    public int TrackedPlayers => _buckets.Count;

    /// <summary>取一个令牌。成功返回 true；失败时 <paramref name="retryAfter"/> 是攒够下一个令牌还要多久。</summary>
    public bool TryTake(Guid playerId, out TimeSpan retryAfter)
    {
        long now = _time.GetTimestamp();
        if (!_buckets.TryGetValue(playerId, out var bucket))
        {
            if (_buckets.Count >= MaxTrackedPlayers)
            {
                Prune(now);
                if (_buckets.Count >= MaxTrackedPlayers)
                {
                    retryAfter = TimeSpan.FromSeconds(1);
                    return false;
                }
            }
            // 新玩家从满桶开始：第一次操作不应因为“刚来”而被拒绝。
            bucket = _buckets.GetOrAdd(playerId, _ => new Bucket(_burst, now));
        }

        lock (bucket)
        {
            Refill(bucket, now);
            if (bucket.Tokens >= 1)
            {
                bucket.Tokens -= 1;
                retryAfter = TimeSpan.Zero;
                return true;
            }
            retryAfter = TimeSpan.FromSeconds((1 - bucket.Tokens) / _perSecond);
            return false;
        }
    }

    private void Refill(Bucket bucket, long now)
    {
        double elapsed = _time.GetElapsedTime(bucket.UpdatedAt, now).TotalSeconds;
        if (elapsed > 0)
        {
            bucket.Tokens = Math.Min(_burst, bucket.Tokens + elapsed * _perSecond);
            bucket.UpdatedAt = now;
        }
    }

    // 只清回满的桶：它们的状态与“从未见过”相同，删掉不改变任何人的预算。
    // 清理与另一个线程同时从该桶取令牌时，那一次扣减可能落在已删除的桶上，玩家最多多得一次，可以接受。
    private void Prune(long now)
    {
        lock (_pruneLock)
        {
            foreach (var (playerId, bucket) in _buckets)
            {
                lock (bucket)
                {
                    Refill(bucket, now);
                    if (bucket.Tokens >= _burst)
                    {
                        _buckets.TryRemove(new KeyValuePair<Guid, Bucket>(playerId, bucket));
                    }
                }
            }
        }
    }
}
