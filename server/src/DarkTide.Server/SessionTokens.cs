using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace DarkTide.Server;

/// <summary>
/// 访问令牌与重连凭据的编码、签名与校验。只用 BCL 的 HMACSHA256，不引入 JWT 库：
/// 令牌只由本服务签发、本服务验证，格式固定，没有算法协商——JWT 的大部分历史漏洞（alg=none、算法混淆）都出在协商上。
///
/// 访问令牌：  "v1." + base64url(载荷) + "." + base64url(HMAC)
///   载荷 = 版本(1) + 密钥编号长度(1) + 密钥编号(≤32) + 玩家编号(16) + 签发时间(8，Unix 秒) + 过期时间(8)
///   HMAC 覆盖 "v1." 前缀与载荷：改动任何一位都验不过。
/// 重连凭据：  "r1." + base64url(32 字节随机数)。数据库只存它的 SHA-256。
///   后继 = "r1." + base64url(HMAC(导出密钥, 旧凭据))：由旧凭据确定性导出，客户端重发时服务端能算出同一张，
///   不必在数据库里存后继原文。每把签名密钥各派生一把导出密钥（与签名用途分开）；新后继用第一把，
///   重发时各把导出的候选都认，所以换签名密钥前后的重发不会被误判为盗用。
/// </summary>
internal sealed class SessionTokens
{
    private const string AccessPrefix = "v1.";
    private const string RefreshPrefix = "r1.";
    private const byte PayloadVersion = 1;
    public const int RefreshBytes = 32;
    // 令牌长度上限：校验前先挡住超长输入，不为攻击者的大字符串做 Base64 解码和 HMAC。
    public const int MaxTokenLength = 256;

    private readonly (string Id, byte[] SigningKey)[] _keys;
    private readonly byte[][] _derivationKeys;

    public SessionTokens(GuestSessionOptions options)
    {
        var errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(options));
        }
        // 每把密钥按用途派生子密钥：同一份秘密不同时用于两种计算，一处泄露的信息不能用于另一处。
        _keys = options.Keys.Select(key => (key.Id, Derive(key.DecodeSecret()!, "darktide.access-token.v1"))).ToArray();
        _derivationKeys = options.Keys.Select(key => Derive(key.DecodeSecret()!, "darktide.refresh-successor.v1")).ToArray();
    }

    public string IssueAccessToken(Guid playerId, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        var (keyId, key) = _keys[0];
        int keyIdLength = Encoding.ASCII.GetByteCount(keyId);
        var payload = new byte[1 + 1 + keyIdLength + 16 + 8 + 8];
        int offset = 0;
        payload[offset++] = PayloadVersion;
        payload[offset++] = (byte)keyIdLength;
        offset += Encoding.ASCII.GetBytes(keyId, payload.AsSpan(offset));
        playerId.TryWriteBytes(payload.AsSpan(offset, 16), bigEndian: true, out _);
        offset += 16;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(offset, 8), issuedAt.ToUnixTimeSeconds());
        offset += 8;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(offset, 8), expiresAt.ToUnixTimeSeconds());

        string body = AccessPrefix + Base64Url(payload);
        return body + "." + Base64Url(Sign(key, body));
    }

    /// <summary>校验访问令牌。签名、密钥编号、版本、过期时间任何一项不对都返回 false，不区分原因（不给攻击者反馈）。</summary>
    public bool TryValidateAccessToken(string? token, DateTimeOffset now, out Guid playerId, out DateTimeOffset expiresAt)
    {
        playerId = Guid.Empty;
        expiresAt = default;
        if (token is null || token.Length > MaxTokenLength || !token.StartsWith(AccessPrefix, StringComparison.Ordinal))
        {
            return false;
        }
        int dot = token.LastIndexOf('.');
        if (dot <= AccessPrefix.Length)
        {
            return false;
        }
        string body = token[..dot];
        if (!TryFromBase64Url(token[(AccessPrefix.Length)..dot], out var payload)
            || !TryFromBase64Url(token[(dot + 1)..], out var signature)
            || payload.Length < 2 || payload[0] != PayloadVersion)
        {
            return false;
        }
        int keyIdLength = payload[1];
        if (payload.Length != 2 + keyIdLength + 16 + 8 + 8)
        {
            return false;
        }
        string keyId = Encoding.ASCII.GetString(payload, 2, keyIdLength);
        byte[]? key = null;
        foreach (var (id, signingKey) in _keys)
        {
            if (string.Equals(id, keyId, StringComparison.Ordinal))
            {
                key = signingKey;
                break;
            }
        }
        // 定长比较：逐字节比较的耗时会泄露“前几个字节对了”，可以被用来逐字节猜签名。
        if (key is null || !CryptographicOperations.FixedTimeEquals(Sign(key, body), signature))
        {
            return false;
        }

        int offset = 2 + keyIdLength;
        var id16 = new Guid(payload.AsSpan(offset, 16), bigEndian: true);
        long expires = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(offset + 24, 8));
        if (id16 == Guid.Empty || expires <= now.ToUnixTimeSeconds())
        {
            return false;
        }
        playerId = id16;
        expiresAt = DateTimeOffset.FromUnixTimeSeconds(expires);
        return true;
    }

    public static string NewRefreshCredential() => RefreshPrefix + Base64Url(RandomNumberGenerator.GetBytes(RefreshBytes));

    /// <summary>由旧凭据确定性导出后继（用第一把密钥）。调用前须已校验格式。</summary>
    public string DeriveSuccessor(string refreshCredential) => DeriveSuccessors(refreshCredential)[0];

    /// <summary>按每把已配置密钥导出的候选后继，第一个是本次换新要用的。</summary>
    public string[] DeriveSuccessors(string refreshCredential)
    {
        var input = Encoding.ASCII.GetBytes(refreshCredential);
        return _derivationKeys.Select(key => RefreshPrefix + Base64Url(HMACSHA256.HashData(key, input))).ToArray();
    }

    /// <summary>格式检查：前缀、长度、Base64 合法、解码后正好 32 字节。数据库查询前先挡住垃圾输入。</summary>
    public static bool IsWellFormedRefresh([NotNullWhen(true)] string? credential) =>
        credential is not null && credential.Length <= MaxTokenLength && credential.StartsWith(RefreshPrefix, StringComparison.Ordinal)
        && TryFromBase64Url(credential[RefreshPrefix.Length..], out var bytes) && bytes.Length == RefreshBytes;

    public static byte[] Hash(string refreshCredential) => SHA256.HashData(Encoding.ASCII.GetBytes(refreshCredential));

    private static byte[] Sign(byte[] key, string body) => HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(body));

    private static byte[] Derive(byte[] secret, string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, info: Encoding.ASCII.GetBytes(purpose));

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryFromBase64Url(string text, out byte[] bytes)
    {
        bytes = [];
        // 只接受 base64url 字符集，拒绝标准 Base64 的 + / 和填充：同一个令牌只有一种写法。
        if (text.Length == 0 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return false;
        }
        string padded = text.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => "!" };
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
