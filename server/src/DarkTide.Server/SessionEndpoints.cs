using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace DarkTide.Server;

/// <summary>
/// 会话接口：
///   POST /sessions/guest    创建游客身份，返回访问令牌与重连凭据；
///   POST /sessions/refresh  用重连凭据换新的一组（请求体 {"refreshCredential": "..."}）；
///   GET  /sessions/me       用访问令牌查询自己的玩家编号（客户端自检与测试用）。
/// 凭据只放在请求体与响应体里，不放 URL；响应禁止缓存。请求体大小由全局的 <see cref="RequestBodyLimit"/> 限制。
/// </summary>
internal static class SessionEndpoints
{
    public const string GuestPolicy = "session-guest";
    public const string RefreshPolicy = "session-refresh";

    public sealed record RefreshRequest([property: JsonPropertyName("refreshCredential")] string? RefreshCredential);

    public sealed record SessionResponse(
        [property: JsonPropertyName("playerId")] Guid PlayerId,
        [property: JsonPropertyName("accessToken")] string AccessToken,
        [property: JsonPropertyName("accessTokenExpiresAt")] DateTimeOffset AccessTokenExpiresAt,
        [property: JsonPropertyName("refreshCredential")] string RefreshCredential,
        [property: JsonPropertyName("refreshExpiresAt")] DateTimeOffset RefreshExpiresAt)
    {
        public static SessionResponse From(SessionGrant grant) => new(grant.PlayerId, grant.AccessToken, grant.AccessTokenExpiresAt,
            grant.RefreshCredential, grant.RefreshExpiresAt);

        public override string ToString() => $"SessionResponse {{ PlayerId = {PlayerId} }}";
    }

    public sealed record MeResponse([property: JsonPropertyName("playerId")] Guid PlayerId);

    public static void AddSessionRateLimits(this IServiceCollection services)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                }
                return ValueTask.CompletedTask;
            };
            limiter.AddPolicy(GuestPolicy, context => PerAddress(context, options => options.GuestCreationsPerMinute));
            limiter.AddPolicy(RefreshPolicy, context => PerAddress(context, options => options.RefreshesPerMinute));
        });
    }

    // 令牌桶：允许一分钟额度的突发，然后按平均速率补充。内置分区限流器会回收长期空闲的分区，内存不随历史地址数增长。
    private static RateLimitPartition<string> PerAddress(HttpContext context, Func<GuestSessionOptions, int> perMinute)
    {
        int limit = perMinute(context.RequestServices.GetRequiredService<IOptions<GuestSessionOptions>>().Value);
        return RateLimitPartition.GetTokenBucketLimiter(ClientAddress.PartitionKey(context), _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = limit,
            TokensPerPeriod = limit,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/sessions").AddEndpointFilter(async (context, next) =>
        {
            // 响应里有凭据：不许任何缓存（浏览器、代理、CDN）保存。
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers.Pragma = "no-cache";
            return await next(context);
        });

        group.MapPost("/guest", async Task<Results<Ok<SessionResponse>, StatusCodeHttpResult>> (
                SessionService sessions, StoreGate gate, HttpContext http) =>
            {
                if (!gate.TryTakeGuestCreation())
                {
                    http.Response.Headers.RetryAfter = "1";
                    return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
                using var slot = gate.TryEnter();
                if (slot is null)
                {
                    return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
                return TypedResults.Ok(SessionResponse.From(await sessions.CreateGuestAsync(http.RequestAborted)));
            })
            .RequireRateLimiting(GuestPolicy);

        group.MapPost("/refresh", async Task<Results<Ok<SessionResponse>, UnauthorizedHttpResult, StatusCodeHttpResult>> (
                [FromBody] RefreshRequest request, SessionService sessions, StoreGate gate, HttpContext http) =>
            {
                using var slot = gate.TryEnter();
                if (slot is null)
                {
                    return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
                var grant = await sessions.RefreshAsync(request.RefreshCredential, http.RequestAborted);
                return grant is null ? TypedResults.Unauthorized() : TypedResults.Ok(SessionResponse.From(grant));
            })
            .RequireRateLimiting(RefreshPolicy);

        group.MapGet("/me", (HttpContext http) => TypedResults.Ok(new MeResponse(http.User.GetPlayerId())))
            .RequireAuthorization();
    }
}

/// <summary>
/// 会话的全进程上限：数据库操作并发数（<see cref="GuestSessionOptions.MaxConcurrentStoreOperations"/>）
/// 与游客创建速率（<see cref="GuestSessionOptions.GuestCreationsPerSecondGlobal"/>）。都不排队：满了立即拒绝。
/// </summary>
internal sealed class StoreGate(IOptions<GuestSessionOptions> options)
{
    private readonly SemaphoreSlim _slots = new(options.Value.MaxConcurrentStoreOperations, options.Value.MaxConcurrentStoreOperations);
    private readonly TokenBucketRateLimiter _guestCreations = new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = options.Value.GuestCreationsPerSecondGlobal,
        TokensPerPeriod = options.Value.GuestCreationsPerSecondGlobal,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    });

    public bool TryTakeGuestCreation()
    {
        using var lease = _guestCreations.AttemptAcquire();
        return lease.IsAcquired;
    }

    public IDisposable? TryEnter() => _slots.Wait(0) ? new Slot(_slots) : null;

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}
