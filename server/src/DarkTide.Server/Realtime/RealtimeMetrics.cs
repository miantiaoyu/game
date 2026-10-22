using System.Diagnostics.Metrics;

namespace DarkTide.Server.Realtime;

/// <summary>实时连接的指标。标签只有低基数的原因，不含玩家或房间编号。</summary>
internal sealed class RealtimeMetrics
{
    private readonly Counter<long> _closed;
    private readonly Counter<long> _statesPushed;
    private readonly Counter<long> _bytesSent;
    private readonly Counter<long> _roomsCreated;
    private readonly Counter<long> _rejectedConnections;
    private readonly Counter<long> _throttled;

    public RealtimeMetrics(IMeterFactory meterFactory, ConnectionRegistry registry)
    {
        var meter = meterFactory.Create(RoomDirectory.MeterName);
        meter.CreateObservableGauge("darktide.realtime.connections", () => registry.Count, description: "当前的实时连接数");
        _closed = meter.CreateCounter<long>("darktide.realtime.closed",
            description: "服务端主动关闭的连接，reason 为 rate_limited／malformed／message_too_large／not_binary／too_slow／token_expired／replaced");
        _statesPushed = meter.CreateCounter<long>("darktide.realtime.states_pushed", description: "推送的房间状态快照数");
        _bytesSent = meter.CreateCounter<long>("darktide.realtime.bytes_sent", unit: "By", description: "发送给客户端的字节数（不含 WebSocket 帧头）");
        _roomsCreated = meter.CreateCounter<long>("darktide.realtime.rooms_created", description: "玩家创建的房间数");
        _rejectedConnections = meter.CreateCounter<long>("darktide.realtime.rejected_connections",
            description: "未建立的连接，reason 为 unauthorized／not_websocket／full");
        _throttled = meter.CreateCounter<long>("darktide.realtime.throttled", description: "超过连接软上限、以 BUSY 回复的消息数");
    }

    public void Closed(string reason) => _closed.Add(1, new KeyValuePair<string, object?>("reason", reason));
    public void StatePushed() => _statesPushed.Add(1);
    public void BytesSent(int bytes) => _bytesSent.Add(bytes);
    public void RoomCreated() => _roomsCreated.Add(1);
    public void Throttled() => _throttled.Add(1);
    public void Rejected(string reason) => _rejectedConnections.Add(1, new KeyValuePair<string, object?>("reason", reason));
}
