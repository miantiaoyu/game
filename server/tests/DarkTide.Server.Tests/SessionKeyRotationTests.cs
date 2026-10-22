using DarkTide.Persistence;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DarkTide.Server.Tests;

/// <summary>换签名密钥的过程：同一个存储，先后两套密钥配置（模拟滚动部署）。</summary>
public class SessionKeyRotationTests
{
    private static string Secret(byte fill) => Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray());

    private static SessionService Service(IPlayerCredentialStore store, ServiceProvider services, params (string Id, string Secret)[] keys) =>
        new(store, Options.Create(new GuestSessionOptions { Keys = keys.Select(key => new SessionKey { Id = key.Id, Secret = key.Secret }).ToList() }),
            TimeProvider.System, services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(), NullLogger<SessionService>.Instance);

    [Fact]
    public async Task RetryAcrossAKeyChangeIsNotMistakenForTheft()
    {
        await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var store = new InMemoryPlayerCredentialStore();
        var before = Service(store, services, ("k1", Secret(1)));
        var after = Service(store, services, ("k2", Secret(2)), ("k1", Secret(1)));

        var guest = await before.CreateGuestAsync(CancellationToken.None);
        // 旧服务器换新成功，但响应丢了；客户端重试时请求落到已换上新密钥的服务器。
        var lost = await before.RefreshAsync(guest.RefreshCredential, CancellationToken.None);
        var retried = await after.RefreshAsync(guest.RefreshCredential, CancellationToken.None);

        Assert.NotNull(lost);
        Assert.NotNull(retried);
        Assert.Equal(guest.PlayerId, retried.PlayerId);
        // 拿到的是同一张后继，可以继续正常换新。
        Assert.Equal(lost.RefreshCredential, retried.RefreshCredential);
        Assert.NotNull(await after.RefreshAsync(retried.RefreshCredential, CancellationToken.None));
    }

    [Fact]
    public async Task OnceTheOldKeyIsRemovedItsSuccessorsAreNoLongerRecognizedOnRetry()
    {
        // 旧密钥删掉之后，用它导出的后继就认不出了：重发会被当作盗用。这是删除旧密钥前要等宽限期过去的原因。
        await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var store = new InMemoryPlayerCredentialStore();
        var before = Service(store, services, ("k1", Secret(1)));
        var retired = Service(store, services, ("k2", Secret(2)));

        var guest = await before.CreateGuestAsync(CancellationToken.None);
        await before.RefreshAsync(guest.RefreshCredential, CancellationToken.None);

        Assert.Null(await retired.RefreshAsync(guest.RefreshCredential, CancellationToken.None));
    }
}
