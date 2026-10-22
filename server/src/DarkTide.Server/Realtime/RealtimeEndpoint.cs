using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace DarkTide.Server.Realtime;

/// <summary>
/// GET /realtime：WebSocket 升级。握手时用 Authorization: Bearer 访问令牌认证（与其他接口相同，不从 URL 读令牌）；
/// 未认证返回 401，不是 WebSocket 请求返回 400，连接数满返回 503。都在升级之前判断，被拒绝的请求不占 socket。
/// </summary>
internal static class RealtimeEndpoint
{
    public const string Path = "/realtime";
    public const string SubProtocol = "darktide.v1";

    public static void AddRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RealtimeOptions>()
            .Bind(configuration.GetSection(RealtimeOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RealtimeOptions>, RealtimeOptionsValidator>();
        services.AddSingleton<ConnectionRegistry>();
        services.AddSingleton<RealtimeMetrics>();
        services.AddSingleton(provider => new RoomCreationLimiter(provider.GetRequiredService<IOptions<RealtimeOptions>>().Value));
    }

    public static void MapRealtime(this IEndpointRouteBuilder app)
    {
        app.Map(Path, HandleAsync).RequireAuthorization();
    }

    private static async Task HandleAsync(HttpContext context, RoomDirectory directory, ConnectionRegistry registry,
        IOptions<RealtimeOptions> options, RoomCreationLimiter roomCreations, SessionService sessions, TimeProvider time,
        RealtimeMetrics metrics, IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            metrics.Rejected("not_websocket");
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        // 认证中间件已验过签；这里再读一次过期时间，连接不能比令牌活得更久。
        string header = context.Request.Headers.Authorization.ToString();
        if (!sessions.Tokens.TryValidateAccessToken(header["Bearer ".Length..].Trim(), time.GetUtcNow(), out var playerId, out var expiresAt))
        {
            metrics.Rejected("unauthorized");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (directory.IsStopping || lifetime.ApplicationStopping.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        // 客户端声明了子协议时必须是我们的；不声明也接受（简单客户端），但声明了别的版本就拒绝，不猜测格式。
        var requested = context.WebSockets.WebSocketRequestedProtocols;
        if (requested.Count > 0 && !requested.Contains(SubProtocol))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var realtime = options.Value;
        RoomConnection? connection = null;
        IReadOnlyList<PlayerConnection>? replaced = null;
        var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = requested.Count > 0 ? SubProtocol : null,
            KeepAliveInterval = realtime.KeepAliveInterval,
            // 发出 ping 后这么久没收到 pong 就断开：发现静默断开（拔网线、手机切后台）的连接，释放它占的资源。
            KeepAliveTimeout = realtime.KeepAliveInterval,
        });
        try
        {
            connection = new RoomConnection(socket, playerId, expiresAt, directory, registry, realtime, roomCreations.Limiter, time,
                loggerFactory.CreateLogger<RoomConnection>(), metrics);
            if (!registry.TryRegister(connection.Registration, out replaced))
            {
                // 握手之后才知道满没满（登记要和取代旧连接原子完成），所以这里用关闭码告诉客户端。
                metrics.Rejected("full");
                await socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.EndpointUnavailable, null, CancellationToken.None);
                return;
            }
            foreach (var old in replaced)
            {
                metrics.Closed("replaced");
                old.RequestClose(Protocol.V1.CloseReason.Replaced);
            }
            await connection.RunAsync(lifetime.ApplicationStopping);
        }
        finally
        {
            if (connection is not null)
            {
                registry.Unregister(connection.Registration);
            }
            socket.Dispose();
        }
    }
}

/// <summary>全进程的建房速率，所有连接共用一个桶。</summary>
internal sealed class RoomCreationLimiter(RealtimeOptions options) : IDisposable
{
    public TokenBucketRateLimiter Limiter { get; } = new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = options.RoomCreationsPerSecondGlobal,
        TokensPerPeriod = options.RoomCreationsPerSecondGlobal,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    });

    public void Dispose() => Limiter.Dispose();
}

internal sealed class RealtimeOptionsValidator : IValidateOptions<RealtimeOptions>
{
    public ValidateOptionsResult Validate(string? name, RealtimeOptions options)
    {
        var errors = options.Validate();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
