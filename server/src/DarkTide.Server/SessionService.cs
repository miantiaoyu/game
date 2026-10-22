using System.Diagnostics.Metrics;
using DarkTide.Persistence;
using Microsoft.Extensions.Options;

namespace DarkTide.Server;

/// <summary>签发给客户端的一组凭据。重连凭据只在这里出现原文，服务端此后只保存摘要。</summary>
public sealed record SessionGrant(
    Guid PlayerId,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshCredential,
    DateTimeOffset RefreshExpiresAt)
{
    // 不让凭据进日志：record 默认 ToString 会打印全部属性。
    public override string ToString() => $"SessionGrant {{ PlayerId = {PlayerId} }}";
}

/// <summary>
/// 游客会话：创建身份、用重连凭据换新访问令牌。
/// 只有这里接触重连凭据原文，进存储前一律换成摘要。
/// </summary>
internal sealed class SessionService
{
    private readonly IPlayerCredentialStore _store;
    private readonly SessionTokens _tokens;
    private readonly GuestSessionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionService> _logger;
    private readonly Counter<long> _outcomes;

    public SessionService(IPlayerCredentialStore store, IOptions<GuestSessionOptions> options, TimeProvider time,
        IMeterFactory meterFactory, ILogger<SessionService> logger)
    {
        _store = store;
        _options = options.Value;
        _tokens = new SessionTokens(_options);
        _time = time;
        _logger = logger;
        _outcomes = meterFactory.Create(RoomDirectory.MeterName).CreateCounter<long>("darktide.sessions",
            description: "会话操作，outcome 为 guest_created／refreshed／retried／reuse_detected／invalid");
    }

    internal SessionTokens Tokens => _tokens;

    public async Task<SessionGrant> CreateGuestAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var playerId = Guid.CreateVersion7(now);
        string refresh = SessionTokens.NewRefreshCredential();
        var refreshExpires = now + _options.RefreshLifetime;
        await _store.CreateGuestAsync(playerId, SessionTokens.Hash(refresh), refreshExpires, cancellationToken).ConfigureAwait(false);
        _outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "guest_created"));
        return Grant(playerId, now, refresh, refreshExpires);
    }

    /// <summary>用重连凭据换新的一组凭据。无效、过期、被吊销、被盗用都返回 null，对外不区分原因。</summary>
    public async Task<SessionGrant?> RefreshAsync(string? refreshCredential, CancellationToken cancellationToken)
    {
        if (!SessionTokens.IsWellFormedRefresh(refreshCredential))
        {
            _outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "invalid"));
            return null;
        }
        var now = _time.GetUtcNow();
        string[] successors = _tokens.DeriveSuccessors(refreshCredential);
        var result = await _store.RotateAsync(SessionTokens.Hash(refreshCredential), successors.Select(SessionTokens.Hash).ToArray(),
            now, now + _options.RefreshLifetime, _options.RotationGrace, cancellationToken).ConfigureAwait(false);

        switch (result.Status)
        {
            case RotationStatus.Rotated:
            case RotationStatus.AlreadyRotated:
                _outcomes.Add(1, new KeyValuePair<string, object?>("outcome", result.Status == RotationStatus.Rotated ? "refreshed" : "retried"));
                return Grant(result.PlayerId, now, successors[result.SuccessorIndex], result.SuccessorExpiresAt);
            case RotationStatus.ReuseDetected:
                // 同一张已换过的凭据又出现：要么客户端有严重错误，要么凭据被别人拿到了。整个家族已吊销，玩家需要重新建身份。
                _outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "reuse_detected"));
                _logger.LogWarning("Refresh credential reuse detected for player {PlayerId}; all of their credentials were revoked.", result.PlayerId);
                return null;
            default:
                _outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "invalid"));
                return null;
        }
    }

    private SessionGrant Grant(Guid playerId, DateTimeOffset now, string refresh, DateTimeOffset refreshExpires)
    {
        var accessExpires = now + _options.AccessTokenLifetime;
        return new SessionGrant(playerId, _tokens.IssueAccessToken(playerId, now, accessExpires), accessExpires, refresh, refreshExpires);
    }
}
