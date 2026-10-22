using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DarkTide.Server;

/// <summary>
/// 从 Authorization: Bearer 头读取访问令牌并验签。只验签与过期，不查数据库：每个请求都查库会把数据库变成认证的瓶颈。
/// 代价是吊销不即时：被盗的访问令牌最多还能用到过期（默认 10 分钟）；重连凭据的吊销是即时的。
/// 令牌不从查询字符串读取：URL 会进访问日志、代理日志和浏览器历史。
/// </summary>
internal sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SessionService sessions,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DarkTideSession";
    public const string PlayerIdClaim = "darktide:player_id";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrEmpty(header))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        const string bearer = "Bearer ";
        if (!header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)
            || !sessions.Tokens.TryValidateAccessToken(header[bearer.Length..].Trim(), time.GetUtcNow(), out var playerId, out _))
        {
            // 不说明是哪一项不对：格式、签名、过期在外部看来一样。
            return Task.FromResult(AuthenticateResult.Fail("invalid access token"));
        }
        var identity = new ClaimsIdentity([new Claim(PlayerIdClaim, playerId.ToString())], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

internal static class PlayerIdentity
{
    /// <summary>从已认证的请求取玩家编号。只能在要求认证的端点里调用；拿不到说明管道配置错了，按故障处理。</summary>
    public static Guid GetPlayerId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(SessionAuthenticationHandler.PlayerIdClaim), out var id) && id != Guid.Empty
            ? id
            : throw new InvalidOperationException("请求未经认证，却到达了要求认证的端点。");
}
