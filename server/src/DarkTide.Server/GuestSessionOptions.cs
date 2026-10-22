namespace DarkTide.Server;

/// <summary>
/// 游客会话的配置（配置节 "Session"）。密钥只从环境变量或密钥管理注入（Session__Keys__0__Secret 等），不写进 appsettings。
///
/// 两种凭据：
/// - 访问令牌：短期（默认 10 分钟），带在每个请求或 WebSocket 握手里，服务端只验签不查库；
/// - 重连凭据：长期（默认 24 小时），只用来换新的访问令牌，每次使用都轮换，数据库只存摘要。
/// 有效期取自设计文档 protocol-and-concurrency.md 第 2 节。
/// </summary>
public sealed class GuestSessionOptions
{
    public const string SectionName = "Session";
    // HMAC-SHA256 的密钥至少 32 字节（与输出等长）；短于它会降低强度。
    public const int MinSecretBytes = 32;
    public const int MaxKeys = 8;

    public static readonly TimeSpan DefaultAccessTokenLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DefaultRefreshLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan DefaultRotationGrace = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 签名密钥。第一把用于签发，其余只用于验证：换密钥时把新密钥放在最前面，旧的留到它签发的令牌全部过期（访问令牌有效期）后再删。
    /// </summary>
    public List<SessionKey> Keys { get; init; } = [];

    public TimeSpan AccessTokenLifetime { get; init; } = DefaultAccessTokenLifetime;
    public TimeSpan RefreshLifetime { get; init; } = DefaultRefreshLifetime;

    /// <summary>重连凭据换新后，旧凭据在这段时间内重发仍得到同一结果（客户端没收到响应时重试）。之外再出现就当作被盗。</summary>
    public TimeSpan RotationGrace { get; init; } = DefaultRotationGrace;

    /// <summary>
    /// 每个来源地址每分钟最多创建多少个游客身份（令牌桶，允许同样大小的突发）。创建身份不需要任何凭据、每次都写数据库，
    /// 是最容易被滥用的入口。网吧、移动网络的运营商 NAT 会让很多人共用一个地址，所以不能压得太低。
    /// </summary>
    public int GuestCreationsPerMinute { get; init; } = 10;

    /// <summary>
    /// 全进程每秒最多创建多少个游客身份。按地址限流挡不住来自大量地址的请求，而每个身份都会在 players 表里永久留下一行；
    /// 这个上限把最坏情况下的增长速度封顶（默认 50/秒 ≈ 每天 430 万行，约 300 MB），并能在指标里看到被拒绝的量。
    /// </summary>
    public int GuestCreationsPerSecondGlobal { get; init; } = 50;

    /// <summary>每个来源地址每分钟最多换新多少次凭据。正常客户端每 10 分钟一次。</summary>
    public int RefreshesPerMinute { get; init; } = 30;

    /// <summary>
    /// 全进程同时进行的会话数据库操作上限。按地址的限流挡不住来自大量地址的请求；这一层保证无论来多少，
    /// 会话操作最多占用这么多数据库连接，对局写入（同一个连接池）不会被挤掉。超出立即返回 503。
    /// </summary>
    public int MaxConcurrentStoreOperations { get; init; } = 16;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Keys.Count == 0)
        {
            errors.Add($"{SectionName}:Keys 至少需要一把签名密钥（环境变量 Session__Keys__0__Id 与 Session__Keys__0__Secret，密钥为 Base64，解码后至少 {MinSecretBytes} 字节）。");
        }
        if (Keys.Count > MaxKeys)
        {
            errors.Add($"{SectionName}:Keys 最多 {MaxKeys} 把，当前 {Keys.Count} 把。");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < Keys.Count; i++)
        {
            var key = Keys[i];
            // 编号进入令牌，限定字符集：既避免歧义，也不让配置把奇怪的字符带进令牌头。
            if (string.IsNullOrEmpty(key.Id) || key.Id.Length > 32 || !key.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                errors.Add($"{SectionName}:Keys:{i}:Id 必须是 1～32 个字母、数字、- 或 _。");
            }
            else if (!seen.Add(key.Id))
            {
                errors.Add($"{SectionName}:Keys:{i}:Id 与前面的密钥重复：{key.Id}。");
            }
            if (key.DecodeSecret() is not { Length: >= MinSecretBytes })
            {
                // 不把密钥内容写进错误信息：配置错误的日志也可能外流。
                errors.Add($"{SectionName}:Keys:{i}:Secret 必须是 Base64，解码后至少 {MinSecretBytes} 字节。");
            }
        }
        RequireRange(errors, nameof(AccessTokenLifetime), AccessTokenLifetime, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
        RequireRange(errors, nameof(RefreshLifetime), RefreshLifetime, TimeSpan.FromHours(1), TimeSpan.FromDays(90));
        RequireRange(errors, nameof(RotationGrace), RotationGrace, TimeSpan.Zero, TimeSpan.FromMinutes(10));
        RequireCount(errors, nameof(GuestCreationsPerMinute), GuestCreationsPerMinute, 1, 10_000);
        RequireCount(errors, nameof(RefreshesPerMinute), RefreshesPerMinute, 1, 10_000);
        RequireCount(errors, nameof(GuestCreationsPerSecondGlobal), GuestCreationsPerSecondGlobal, 1, 10_000);
        RequireCount(errors, nameof(MaxConcurrentStoreOperations), MaxConcurrentStoreOperations, 1, 1_000);
        return errors;
    }

    private static void RequireCount(List<string> errors, string name, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            errors.Add($"{SectionName}:{name} 必须在 {min}～{max} 之间，当前为 {value}。");
        }
    }

    private static void RequireRange(List<string> errors, string name, TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min || value > max)
        {
            errors.Add($"{SectionName}:{name} 必须在 {min}～{max} 之间，当前为 {value}。");
        }
    }
}

public sealed class SessionKey
{
    public string Id { get; init; } = "";
    public string Secret { get; init; } = "";

    // record 的默认 ToString 会打印全部属性；类不会，但为了万一被日志打印，显式只输出编号。
    public override string ToString() => $"SessionKey {{ Id = {Id} }}";

    internal byte[]? DecodeSecret()
    {
        try
        {
            return Convert.FromBase64String(Secret);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
