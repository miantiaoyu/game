namespace DarkTide.Room;

/// <summary>
/// 房间运行参数。上线后要按机器规格和压测结果调整，所以不写死在代码里。
///
/// 属性用 init：配置绑定器（appsettings 的 "Room" 节、环境变量 Room__CommandQueueCapacity 等）可以填充，
/// 构造完成后不能再改。执行器在构造时复制取值，之后即使换了配置对象，也不影响已经在跑的房间。
///
/// 队列默认值来自 docs/technical/protocol-and-concurrency.md（外部 56 条）；回执缓存沿用代码现值 128，
/// 设计文档 capacity-and-storage.md 写的是 256，接入持久化回执时再统一。三者都尚未经压测校准。
/// 上限不是性能目标，而是防误配：写错一个数量级时，启动直接失败，而不是运行一段时间后耗尽内存。
/// </summary>
public sealed class RoomOptions
{
    /// <summary>配置文件中的节名。</summary>
    public const string SectionName = "Room";

    public const int DefaultCommandQueueCapacity = 56;
    public const int DefaultQueryQueueCapacity = 8;

    // 每个排队请求最多持有一份 27 张牌的牌序；1024 条时单房间也只有几十 KiB，再大基本是写错了。
    public const int MaxQueueCapacity = 1024;
    public const int MaxReceiptCacheCapacity = 65_536;
    public static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultJournalTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MinJournalTimeout = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MaxJournalTimeout = TimeSpan.FromSeconds(60);
    public const double DefaultPlayerCommandsPerSecond = 5;
    public const int DefaultPlayerCommandBurst = 10;
    public const double DefaultPlayerQueriesPerSecond = 10;
    public const int DefaultPlayerQueryBurst = 20;
    public const double MaxPerSecond = 10_000;
    public const int MaxBurst = 10_000;
    // 超过这个时长，进程编排器（systemd、Kubernetes）通常早已强杀进程，配得更长没有意义。
    public static readonly TimeSpan MaxShutdownTimeout = TimeSpan.FromMinutes(5);
    // 行动时限与局间间隔：产品初值，尚未经过玩家测试。配成 0 表示关闭（不计时／不自动发牌）。
    public static readonly TimeSpan DefaultTurnTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultNextHandDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxClockDelay = TimeSpan.FromMinutes(10);

    /// <summary>全部取默认值的共享实例。属性只能 init，共享是安全的。</summary>
    public static RoomOptions Default { get; } = new();

    /// <summary>外部命令最多同时排队多少条；超出立即返回繁忙。</summary>
    public int CommandQueueCapacity { get; init; } = DefaultCommandQueueCapacity;

    /// <summary>只读查询最多同时排队多少条；与命令分开计数，刷新视图挤不掉真正的操作。</summary>
    public int QueryQueueCapacity { get; init; } = DefaultQueryQueueCapacity;

    /// <summary>内存里保留多少条最近命令的回执，用于识别重发。只对新建的房间生效。</summary>
    public int ReceiptCacheCapacity { get; init; } = CommandReceiptStore.DefaultCapacity;

    /// <summary>
    /// 关闭时等待已接收请求处理完的最长时间。超时后，仍在排队的请求一律以“未执行”拒绝，
    /// 正在执行的那一条照常做完（规则代码是同步的，中途打断会留下半改的状态）。
    /// 设为 0 表示立即放弃所有排队请求。
    /// </summary>
    public TimeSpan ShutdownTimeout { get; init; } = DefaultShutdownTimeout;

    /// <summary>
    /// 单次日志操作（写入一条结果、按编号查找）的最长时间，含等待连接池。超时后结果未知，房间故障停止。
    /// 它决定数据库卡住时一个房间被占住多久：期间后面的请求全部排队。
    /// 应明显短于 <see cref="ShutdownTimeout"/>，否则关闭时卡住的写入会让排队请求被放弃。
    /// </summary>
    public TimeSpan JournalTimeout { get; init; } = DefaultJournalTimeout;

    /// <summary>
    /// 每名玩家在本房间的命令持续速率（次／秒）。真人操作远低于这个数；它限制的是一名玩家能让房间写多少条日志：
    /// 规则拒绝也要写入，不限的话连续发非法动作就能放大数据库写入。默认值是提案，尚未经压测与试玩校准。
    /// </summary>
    public double PlayerCommandsPerSecond { get; init; } = DefaultPlayerCommandsPerSecond;

    /// <summary>每名玩家的命令突发上限：连续快速操作最多这么多次，之后按持续速率放行。</summary>
    public int PlayerCommandBurst { get; init; } = DefaultPlayerCommandBurst;

    /// <summary>每名玩家的查询持续速率（次／秒）。查询队列只有几个位置，一名玩家刷视图不能挤掉其他人。</summary>
    public double PlayerQueriesPerSecond { get; init; } = DefaultPlayerQueriesPerSecond;

    /// <summary>每名玩家的查询突发上限。</summary>
    public int PlayerQueryBurst { get; init; } = DefaultPlayerQueryBurst;

    /// <summary>轮到某人下注或改航后，最多等多久；到期按超时处理（能过牌就过牌，否则弃牌；改航则放弃）。0 表示不计时。</summary>
    public TimeSpan TurnTimeout { get; init; } = DefaultTurnTimeout;

    /// <summary>一局结算后，等多久自动发下一局（留给客户端展示结算）。0 表示不自动发牌。</summary>
    public TimeSpan NextHandDelay { get; init; } = DefaultNextHandDelay;

    /// <summary>返回全部错误，而不是遇到第一个就停：运维一次就能改完所有配置。</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        RequireRange(errors, nameof(CommandQueueCapacity), CommandQueueCapacity, 1, MaxQueueCapacity);
        RequireRange(errors, nameof(QueryQueueCapacity), QueryQueueCapacity, 1, MaxQueueCapacity);
        RequireRange(errors, nameof(ReceiptCacheCapacity), ReceiptCacheCapacity, 1, MaxReceiptCacheCapacity);
        if (ShutdownTimeout < TimeSpan.Zero || ShutdownTimeout > MaxShutdownTimeout)
        {
            errors.Add($"{SectionName}:{nameof(ShutdownTimeout)} 必须在 0～{MaxShutdownTimeout} 之间，当前为 {ShutdownTimeout}。");
        }
        RequireRate(errors, nameof(PlayerCommandsPerSecond), PlayerCommandsPerSecond);
        RequireRange(errors, nameof(PlayerCommandBurst), PlayerCommandBurst, 1, MaxBurst);
        RequireRate(errors, nameof(PlayerQueriesPerSecond), PlayerQueriesPerSecond);
        RequireRange(errors, nameof(PlayerQueryBurst), PlayerQueryBurst, 1, MaxBurst);
        if (JournalTimeout < MinJournalTimeout || JournalTimeout > MaxJournalTimeout)
        {
            errors.Add($"{SectionName}:{nameof(JournalTimeout)} 必须在 {MinJournalTimeout}～{MaxJournalTimeout} 之间，当前为 {JournalTimeout}。");
        }
        RequireClockDelay(errors, nameof(TurnTimeout), TurnTimeout);
        RequireClockDelay(errors, nameof(NextHandDelay), NextHandDelay);
        return errors;
    }

    /// <summary>配置无效时抛出，消息列出所有问题。应在进程启动时调用，让错误配置无法上线。</summary>
    public void EnsureValid()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("房间配置无效：" + string.Join(" ", errors), "options");
        }
    }

    private static void RequireClockDelay(List<string> errors, string name, TimeSpan value)
    {
        if (value < TimeSpan.Zero || value > MaxClockDelay)
        {
            errors.Add($"{SectionName}:{name} 必须在 0～{MaxClockDelay} 之间（0 表示关闭），当前为 {value}。");
        }
    }

    private static void RequireRate(List<string> errors, string name, double value)
    {
        // NaN 与无穷大也要挡住：配置绑定器能把 "NaN" 绑成 double.NaN，它与任何数比较都是 false。
        if (!double.IsFinite(value) || value <= 0 || value > MaxPerSecond)
        {
            errors.Add($"{SectionName}:{name} 必须大于 0 且不超过 {MaxPerSecond}，当前为 {value}。");
        }
    }

    // 错误消息带上完整配置键（Room:CommandQueueCapacity），运维可以直接定位到配置文件或环境变量。
    private static void RequireRange(List<string> errors, string name, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            errors.Add($"{SectionName}:{name} 必须在 {min}～{max} 之间，当前为 {value}。");
        }
    }
}
