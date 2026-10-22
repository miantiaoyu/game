using DarkTide.Rules;

namespace DarkTide.Room;

/// <summary>
/// 房间在某一版本的不可变快照，由消费循环在每次状态变化后生成一次（单线程实测约 2～3 µs）。
/// 任何线程都可以读；按观看者取视图时只做数组下标查找，不分配新对象以外的工作，与订阅者数量无关。
///
/// 底牌按座位保存在私有字段里，只能经 <see cref="ViewFor"/> 取到观看者自己的那两张。
/// 调用方必须传入已认证的身份，不能用客户端自报的编号。
/// </summary>
public sealed class PublishedRoomState
{
    private readonly Guid?[] _playerBySeat;
    private readonly IReadOnlyList<Card>[]? _holeCardsBySeat;
    private readonly TaskCompletionSource _superseded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PublishedRoomState(RoomOverview overview, Guid?[] playerBySeat, IReadOnlyList<Card>[]? holeCardsBySeat)
    {
        Overview = overview;
        _playerBySeat = playerBySeat;
        _holeCardsBySeat = holeCardsBySeat;
    }

    public RoomOverview Overview { get; }
    public long StateVersion => Overview.StateVersion;
    public DateTimeOffset? Deadline => Overview.Deadline;

    /// <summary>有更新的快照替代它（或房间停止）时完成。</summary>
    public Task Superseded => _superseded.Task;

    internal static PublishedRoomState Capture(Room room, DateTimeOffset? deadline) =>
        new(room.CreateOverview() with { Deadline = deadline }, room.SeatPlayersSnapshot(), room.HoleCardsBySeat());

    internal void MarkSuperseded() => _superseded.TrySetResult();

    /// <summary>某一观看者能看到的内容：公开概况，加上他自己的座位与底牌。没有入座时只有公开概况。</summary>
    public ViewerSnapshot ViewFor(Guid viewerId)
    {
        for (int seat = 0; seat < _playerBySeat.Length; seat++)
        {
            if (_playerBySeat[seat] == viewerId)
            {
                var holeCards = _holeCardsBySeat?[seat];
                return new ViewerSnapshot(Overview, seat, holeCards is { Count: > 0 } ? holeCards : null);
            }
        }
        return new ViewerSnapshot(Overview, null, null);
    }

    // 不打印任何牌面。
    public override string ToString() => $"{nameof(PublishedRoomState)} {{ Version = {StateVersion} }}";
}
