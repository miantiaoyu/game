using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace DarkTide.Server.Realtime;

/// <summary>
/// 全进程的连接登记：总数上限、每个玩家的连接上限，以及“同一玩家只有最新的连接能操作”。
///
/// 控制权：每个玩家同一时刻只有一个控制连接（最近打开的那个）。旧连接仍可接收状态，
/// 发命令会被拒绝（FORBIDDEN）。超过每玩家上限时，最旧的连接被取代并关闭。
/// 这对应设计文档的“每个座位只允许一个控制连接，新的 epoch 原子取代旧连接”；控制权代数只在本进程内，多节点时需要持久化。
/// </summary>
internal sealed class ConnectionRegistry(IOptions<RealtimeOptions> options)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, List<PlayerConnection>> _byPlayer = [];
    private readonly int _maxConnections = options.Value.MaxConnections;
    private readonly int _maxPerPlayer = options.Value.MaxConnectionsPerPlayer;
    private int _count;
    private long _nextEpoch;

    public int Count
    {
        get { lock (_lock) { return _count; } }
    }

    /// <summary>
    /// 登记新连接，它成为该玩家的控制连接。总数已满时返回 false。
    /// <paramref name="replaced"/> 是因超过每玩家上限而被取代的旧连接，调用方负责关闭它（在锁外关闭，关闭可能要等网络）。
    /// </summary>
    public bool TryRegister(PlayerConnection connection, [NotNullWhen(true)] out IReadOnlyList<PlayerConnection>? replaced)
    {
        lock (_lock)
        {
            if (_count >= _maxConnections)
            {
                replaced = null;
                return false;
            }
            if (!_byPlayer.TryGetValue(connection.PlayerId, out var list))
            {
                list = [];
                _byPlayer[connection.PlayerId] = list;
            }
            var evicted = new List<PlayerConnection>();
            while (list.Count >= _maxPerPlayer)
            {
                evicted.Add(list[0]);
                list.RemoveAt(0);
                _count--;
            }
            connection.Epoch = ++_nextEpoch;
            list.Add(connection);
            _count++;
            replaced = evicted;
            return true;
        }
    }

    public void Unregister(PlayerConnection connection)
    {
        lock (_lock)
        {
            if (_byPlayer.TryGetValue(connection.PlayerId, out var list) && list.Remove(connection))
            {
                _count--;
                if (list.Count == 0)
                {
                    // 删掉空列表：字典不随历史玩家数增长。
                    _byPlayer.Remove(connection.PlayerId);
                }
            }
        }
    }

    /// <summary>这个连接是否是该玩家当前的控制连接（最近登记且仍在登记中的那个）。</summary>
    public bool IsController(PlayerConnection connection)
    {
        lock (_lock)
        {
            return _byPlayer.TryGetValue(connection.PlayerId, out var list) && list.Count > 0 && ReferenceEquals(list[^1], connection);
        }
    }
}

/// <summary>登记表里的一项：连接属于谁、第几代、怎样让它关闭。</summary>
internal sealed class PlayerConnection(Guid playerId, Action<Protocol.V1.CloseReason> requestClose)
{
    public Guid PlayerId { get; } = playerId;

    /// <summary>登记时分配，单调递增。只用于日志与诊断。</summary>
    public long Epoch { get; set; }

    /// <summary>请求连接自行关闭（发 Closing 后关 socket）。可以从任意线程调用，多次调用无害。</summary>
    public void RequestClose(Protocol.V1.CloseReason reason) => requestClose(reason);
}
