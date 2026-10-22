using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DarkTide.Server.Tests;

/// <summary>会话接口端到端：真实的 HTTP 管道（认证、限流、请求体上限、缓存头），内存存储。</summary>
public class SessionEndpointTests
{
    private static WebApplicationFactory<Program> CreateFactory(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in TestSessionKeys.Settings.Concat(settings))
            {
                builder.UseSetting(key, value);
            }
        });

    private sealed record Session(Guid PlayerId, string AccessToken, string RefreshCredential);

    private static async Task<Session> ReadSessionAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        return new Session(root.GetProperty("playerId").GetGuid(), root.GetProperty("accessToken").GetString()!,
            root.GetProperty("refreshCredential").GetString()!);
    }

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string? credential) =>
        client.PostAsJsonAsync("/sessions/refresh", new { refreshCredential = credential });

    private static async Task<HttpResponseMessage> MeAsync(HttpClient client, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sessions/me");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task GuestGetsAnIdentityThatTheAccessTokenProves()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/sessions/guest", null);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var session = await ReadSessionAsync(response);

        var me = await MeAsync(client, session.AccessToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Contains(session.PlayerId.ToString(), await me.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("v1.AAAA.AAAA")]
    public async Task MissingOrInvalidAccessTokenIsUnauthorized(string? token)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await MeAsync(client, token)).StatusCode);
    }

    [Fact]
    public async Task AccessTokenInTheQueryStringIsIgnored()
    {
        // URL 会进访问日志：不接受从查询字符串带令牌。
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var session = await ReadSessionAsync(await client.PostAsync("/sessions/guest", null));

        var response = await client.GetAsync($"/sessions/me?access_token={Uri.EscapeDataString(session.AccessToken)}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RefreshKeepsTheSameIdentityAndRotatesTheCredential()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var first = await ReadSessionAsync(await client.PostAsync("/sessions/guest", null));

        var second = await ReadSessionAsync(await RefreshAsync(client, first.RefreshCredential));

        Assert.Equal(first.PlayerId, second.PlayerId);
        Assert.NotEqual(first.RefreshCredential, second.RefreshCredential);
        Assert.Equal(HttpStatusCode.OK, (await MeAsync(client, second.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task RetryWithinGraceReturnsTheSameSuccessor()
    {
        // 客户端没收到换新的响应、用旧凭据重发：得到同一张后继，不算盗用，也不会生出两张都能用的凭据。
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var first = await ReadSessionAsync(await client.PostAsync("/sessions/guest", null));

        var a = await ReadSessionAsync(await RefreshAsync(client, first.RefreshCredential));
        var b = await ReadSessionAsync(await RefreshAsync(client, first.RefreshCredential));

        Assert.Equal(a.RefreshCredential, b.RefreshCredential);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(client, b.RefreshCredential)).StatusCode);
    }

    [Fact]
    public async Task ReusingARotatedCredentialAfterItsSuccessorWasUsedRevokesTheWholeFamily()
    {
        // 攻击者偷到旧凭据。合法客户端已经用后继又换了一次；攻击者此时拿旧凭据来换：识别为盗用，全部吊销。
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var stolen = await ReadSessionAsync(await client.PostAsync("/sessions/guest", null));
        var legit = await ReadSessionAsync(await RefreshAsync(client, stolen.RefreshCredential));
        var legitNext = await ReadSessionAsync(await RefreshAsync(client, legit.RefreshCredential));

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, stolen.RefreshCredential)).StatusCode);
        // 合法用户最新的凭据也被吊销：宁可让他重新建身份，也不让攻击者继续。
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, legitNext.RefreshCredential)).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("r1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("v1.not-a-refresh-credential")]
    public async Task UnknownOrMalformedRefreshCredentialIsUnauthorized(string? credential)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, credential)).StatusCode);
    }

    [Fact]
    public async Task AccessTokenCannotBeUsedAsARefreshCredential()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var session = await ReadSessionAsync(await client.PostAsync("/sessions/guest", null));

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(client, session.AccessToken)).StatusCode);
    }

    [Fact]
    public async Task GuestCreationIsRateLimitedPerAddress()
    {
        await using var factory = CreateFactory(("Session:GuestCreationsPerMinute", "3"));
        using var client = factory.CreateClient();

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/sessions/guest", null)).StatusCode);
        }
        var limited = await client.PostAsync("/sessions/guest", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
    }

    [Fact]
    public async Task GlobalGuestCreationBudgetCapsGrowthEvenAcrossManyAddresses()
    {
        // 按地址的额度放得很宽（模拟大量不同地址），全局额度 2：第三个立即 503，带重试提示。
        await using var factory = CreateFactory(("Session:GuestCreationsPerMinute", "1000"), ("Session:GuestCreationsPerSecondGlobal", "2"));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/sessions/guest", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/sessions/guest", null)).StatusCode);
        var capped = await client.PostAsync("/sessions/guest", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, capped.StatusCode);
        Assert.NotNull(capped.Headers.RetryAfter);
    }

    [Fact]
    public async Task OversizedRequestBodyIsRejected()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var content = new StringContent("{\"refreshCredential\":\"" + new string('A', 64 * 1024) + "\"}", System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/sessions/refresh", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
}
