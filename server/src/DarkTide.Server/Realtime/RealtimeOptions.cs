namespace DarkTide.Server.Realtime;

/// <summary>
/// 实时连接（WebSocket）的限额，配置节 "Realtime"。每一项都对应一种资源，上限的意义写在属性上。
/// 默认值是提案，按本机压测与“8 人桌、思考 3～10 秒”估算，尚未经真实玩家校准。
/// </summary>
public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";

    /// <summary>
    /// 单条客户端消息的最大字节数（整条消息，不是单个分片）。最大的合法消息（下注）不到 60 字节；
    /// 超过就关闭连接，不为攻击者的大消息分配内存。
    /// </summary>
    public int MaxMessageBytes { get; init; } = 1024;

    /// <summary>
    /// 每个连接每秒处理多少条消息（令牌桶，允许同样大小的突发）。两级：
    /// 超过这个速率，回复 BUSY 并给出退避时间，消息不处理——按协议退避的客户端永远不会因为发得快被断开；
    /// 超过它的 <see cref="HardLimitMultiplier"/> 倍，说明客户端无视退避，关闭连接。
    /// </summary>
    public int MessagesPerSecond { get; init; } = 20;

    /// <summary>硬上限是 <see cref="MessagesPerSecond"/> 的几倍。</summary>
    public const int HardLimitMultiplier = 3;

    /// <summary>全进程最多同时多少个连接。每个连接占一个 socket、两个缓冲区和一个任务；满了新连接得到 503。</summary>
    public int MaxConnections { get; init; } = 20_000;

    /// <summary>同一玩家最多同时多少个连接（多开窗口、断线重连期间旧连接未关）。超出时最旧的被取代。</summary>
    public int MaxConnectionsPerPlayer { get; init; } = 2;

    /// <summary>
    /// 一次推送最多等客户端接收多久。超时说明客户端接收太慢（或网络断了），关闭它：不能让一个慢连接的发送缓冲无限积压。
    /// 推送总是“最新快照”，不排队；所以这里限制的是单次发送，不是队列长度。
    /// </summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>服务端发送 WebSocket ping 的间隔；同时作为等待 pong 的时限，超时关闭连接。用来发现静默断开的连接。</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 每个玩家同时开着的房间（作为房主建的、尚未结束的）上限。建房要写数据库、占内存，必须有上限，
    /// 否则一个身份就能建出无数空房间。
    /// </summary>
    public int MaxOpenRoomsPerOwner { get; init; } = 3;

    /// <summary>全进程每秒最多建多少个房间。按玩家的上限挡不住大量游客身份。</summary>
    public int RoomCreationsPerSecondGlobal { get; init; } = 20;

    /// <summary>全进程最多同时多少个房间。满了建房返回 UNAVAILABLE。</summary>
    public int MaxRooms { get; init; } = 10_000;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        Require(errors, nameof(MaxMessageBytes), MaxMessageBytes, 128, 64 * 1024);
        Require(errors, nameof(MessagesPerSecond), MessagesPerSecond, 1, 1000);
        Require(errors, nameof(MaxConnections), MaxConnections, 1, 1_000_000);
        Require(errors, nameof(MaxConnectionsPerPlayer), MaxConnectionsPerPlayer, 1, 16);
        Require(errors, nameof(MaxOpenRoomsPerOwner), MaxOpenRoomsPerOwner, 1, 100);
        Require(errors, nameof(RoomCreationsPerSecondGlobal), RoomCreationsPerSecondGlobal, 1, 10_000);
        Require(errors, nameof(MaxRooms), MaxRooms, 1, 1_000_000);
        if (SendTimeout < TimeSpan.FromSeconds(1) || SendTimeout > TimeSpan.FromMinutes(1))
        {
            errors.Add($"{SectionName}:{nameof(SendTimeout)} 必须在 1 秒～1 分钟之间，当前为 {SendTimeout}。");
        }
        if (KeepAliveInterval < TimeSpan.FromSeconds(5) || KeepAliveInterval > TimeSpan.FromMinutes(5))
        {
            errors.Add($"{SectionName}:{nameof(KeepAliveInterval)} 必须在 5 秒～5 分钟之间，当前为 {KeepAliveInterval}。");
        }
        return errors;
    }

    private static void Require(List<string> errors, string name, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            errors.Add($"{SectionName}:{name} 必须在 {min}～{max} 之间，当前为 {value}。");
        }
    }
}
