namespace DarkTide.Server.Tests;

/// <summary>访问令牌与重连凭据的格式和签名：每个用例对应一种伪造或篡改方式。</summary>
public class SessionTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Player = Guid.Parse("0192f0a0-0000-7000-8000-000000000001");

    private static GuestSessionOptions Options(params (string Id, string Secret)[] keys) => new()
    {
        Keys = keys.Length == 0
            ? [new SessionKey { Id = TestSessionKeys.Id, Secret = TestSessionKeys.Secret }]
            : keys.Select(key => new SessionKey { Id = key.Id, Secret = key.Secret }).ToList(),
    };

    private static string Secret(byte fill) => Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray());

    [Fact]
    public void IssuedTokenValidatesUntilItExpires()
    {
        var tokens = new SessionTokens(Options());
        string token = tokens.IssueAccessToken(Player, Now, Now.AddMinutes(10));

        Assert.True(tokens.TryValidateAccessToken(token, Now.AddMinutes(9), out var player, out _));
        Assert.Equal(Player, player);
        Assert.False(tokens.TryValidateAccessToken(token, Now.AddMinutes(10), out _, out _));
    }

    [Fact]
    public void FlippingAnyCharacterInvalidatesTheToken()
    {
        var tokens = new SessionTokens(Options());
        string token = tokens.IssueAccessToken(Player, Now, Now.AddMinutes(10));
        for (int i = 0; i < token.Length; i++)
        {
            if (token[i] == '.')
            {
                continue;
            }
            char replacement = token[i] == 'A' ? 'B' : 'A';
            string tampered = token[..i] + replacement + token[(i + 1)..];
            Assert.False(tokens.TryValidateAccessToken(tampered, Now, out _, out _), $"改第 {i} 个字符后仍然有效");
        }
    }

    [Fact]
    public void TokenSignedWithAnotherKeyIsRejectedEvenWithTheSameKeyId()
    {
        var mine = new SessionTokens(Options());
        var forger = new SessionTokens(Options((TestSessionKeys.Id, Secret(7))));
        string forged = forger.IssueAccessToken(Player, Now, Now.AddMinutes(10));

        Assert.False(mine.TryValidateAccessToken(forged, Now, out _, out _));
    }

    [Fact]
    public void KeyRotationKeepsOldTokensValidAndSignsWithTheNewKey()
    {
        var before = new SessionTokens(Options(("k1", Secret(1))));
        string old = before.IssueAccessToken(Player, Now, Now.AddMinutes(10));

        // 换密钥：新密钥放最前面，旧的留着验证。
        var after = new SessionTokens(Options(("k2", Secret(2)), ("k1", Secret(1))));
        Assert.True(after.TryValidateAccessToken(old, Now, out _, out _));
        string fresh = after.IssueAccessToken(Player, Now, Now.AddMinutes(10));
        Assert.False(before.TryValidateAccessToken(fresh, Now, out _, out _));

        // 旧密钥删掉后，旧令牌失效。
        var retired = new SessionTokens(Options(("k2", Secret(2))));
        Assert.False(retired.TryValidateAccessToken(old, Now, out _, out _));
        Assert.True(retired.TryValidateAccessToken(fresh, Now, out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1.")]
    [InlineData("v1..")]
    [InlineData("v2.AAAA.AAAA")]
    [InlineData("v1.AAAA")]
    [InlineData("v1.!!!!.AAAA")]
    [InlineData("v1.AAAA+/==.AAAA")]
    public void MalformedTokensAreRejected(string? token)
    {
        Assert.False(new SessionTokens(Options()).TryValidateAccessToken(token, Now, out _, out _));
    }

    [Fact]
    public void OversizedInputIsRejectedBeforeAnyDecoding()
    {
        Assert.False(new SessionTokens(Options()).TryValidateAccessToken("v1." + new string('A', 100_000) + ".A", Now, out _, out _));
        Assert.False(SessionTokens.IsWellFormedRefresh("r1." + new string('A', 100_000)));
    }

    [Fact]
    public void RefreshCredentialsAreRandomAndSuccessorsAreDeterministic()
    {
        var tokens = new SessionTokens(Options());
        string first = SessionTokens.NewRefreshCredential();
        string second = SessionTokens.NewRefreshCredential();

        Assert.NotEqual(first, second);
        Assert.True(SessionTokens.IsWellFormedRefresh(first));
        Assert.Equal(tokens.DeriveSuccessor(first), tokens.DeriveSuccessor(first));
        Assert.NotEqual(tokens.DeriveSuccessor(first), tokens.DeriveSuccessor(second));
        Assert.True(SessionTokens.IsWellFormedRefresh(tokens.DeriveSuccessor(first)));
        // 换了导出密钥（新签名密钥在最前）后继就不同：旧服务器签的后继不能在新服务器上被“预测”。
        Assert.NotEqual(tokens.DeriveSuccessor(first), new SessionTokens(Options(("k9", Secret(9)))).DeriveSuccessor(first));
    }

    [Theory]
    [InlineData("")]
    [InlineData("r1.")]
    [InlineData("r1.AAAA")]
    [InlineData("r2.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void MalformedRefreshCredentialsAreRejected(string credential)
    {
        Assert.False(SessionTokens.IsWellFormedRefresh(credential));
    }

    [Fact]
    public void ConfigurationRejectsMissingShortOrDuplicateKeys()
    {
        Assert.NotEmpty(new GuestSessionOptions().Validate());
        Assert.NotEmpty(Options(("k", Convert.ToBase64String(new byte[16]))).Validate());
        Assert.NotEmpty(Options(("k", "not base64!")).Validate());
        Assert.NotEmpty(Options(("k", Secret(1)), ("k", Secret(2))).Validate());
        Assert.NotEmpty(Options(("bad id!", Secret(1))).Validate());
        Assert.Empty(Options(("k", Secret(1))).Validate());
        // 错误信息里不出现密钥内容。
        Assert.All(Options(("k", "c2hvcnQ=")).Validate(), error => Assert.DoesNotContain("c2hvcnQ=", error));
    }
}
