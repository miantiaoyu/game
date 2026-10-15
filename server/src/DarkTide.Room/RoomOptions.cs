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
    // 超过这个时长，进程编排器（systemd、Kubernetes）通常早已强杀进程，配得更长没有意义。
    public static readonly TimeSpan MaxShutdownTimeout = TimeSpan.FromMinutes(5);

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
        if (JournalTimeout < MinJournalTimeout || JournalTimeout > MaxJournalTimeout)
        {
            errors.Add($"{SectionName}:{nameof(JournalTimeout)} 必须在 {MinJournalTimeout}～{MaxJournalTimeout} 之间，当前为 {JournalTimeout}。");
        }
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

    // 错误消息带上完整配置键（Room:CommandQueueCapacity），运维可以直接定位到配置文件或环境变量。
    private static void RequireRange(List<string> errors, string name, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            errors.Add($"{SectionName}:{name} 必须在 {min}～{max} 之间，当前为 {value}。");
        }
    }
}
