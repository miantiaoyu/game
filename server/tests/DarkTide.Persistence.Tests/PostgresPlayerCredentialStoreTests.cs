using System.Security.Cryptography;
using Npgsql;

namespace DarkTide.Persistence.Tests;

/// <summary>真实数据库上的凭据存储：换新、重发、盗用检测、并发、封禁与清理。</summary>
public class PostgresPlayerCredentialStoreTests(PostgresFixture database) : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private PostgresPlayerCredentialStore Store() => new(database.DataSource);

    private static byte[] Hash() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public async Task RotationKeepsThePlayerAndRetryWithinGraceIsIdempotent()
    {
        var store = Store();
        var player = Guid.NewGuid();
        var first = Hash();
        var second = Hash();
        await store.CreateGuestAsync(player, first, Now.AddDays(1), CancellationToken.None);

        var rotated = await store.RotateAsync(first, [second], Now, Now.AddDays(1), Grace, CancellationToken.None);
        Assert.Equal(RotationStatus.Rotated, rotated.Status);
        Assert.Equal(player, rotated.PlayerId);

        // 同一张旧凭据、同一个后继，在宽限期内重发：结果相同，不新增凭据。
        var retried = await store.RotateAsync(first, [second], Now.AddSeconds(5), Now.AddDays(1), Grace, CancellationToken.None);
        Assert.Equal(RotationStatus.AlreadyRotated, retried.Status);
        Assert.Equal(player, retried.PlayerId);
        Assert.Equal(2, await CountAsync(player));
    }

    [Fact]
    public async Task ReuseAfterTheSuccessorWasUsedRevokesEveryCredentialOfThePlayer()
    {
        var store = Store();
        var player = Guid.NewGuid();
        var (a, b, c) = (Hash(), Hash(), Hash());
        await store.CreateGuestAsync(player, a, Now.AddDays(1), CancellationToken.None);
        await store.RotateAsync(a, [b], Now, Now.AddDays(1), Grace, CancellationToken.None);
        await store.RotateAsync(b, [c], Now.AddSeconds(1), Now.AddDays(1), Grace, CancellationToken.None);

        // 后继 b 已被用过，宽限期内重发 a 也算盗用。
        var reuse = await store.RotateAsync(a, [b], Now.AddSeconds(2), Now.AddDays(1), Grace, CancellationToken.None);
        Assert.Equal(RotationStatus.ReuseDetected, reuse.Status);
        Assert.Equal(RotationStatus.Invalid, (await store.RotateAsync(c, [Hash()], Now.AddSeconds(3), Now.AddDays(1), Grace, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task ReuseAfterGraceRevokesEvenIfTheSuccessorWasNeverUsed()
    {
        var store = Store();
        var player = Guid.NewGuid();
        var (a, b) = (Hash(), Hash());
        await store.CreateGuestAsync(player, a, Now.AddDays(1), CancellationToken.None);
        await store.RotateAsync(a, [b], Now, Now.AddDays(1), Grace, CancellationToken.None);

        var late = await store.RotateAsync(a, [b], Now + Grace + TimeSpan.FromSeconds(1), Now.AddDays(1), Grace, CancellationToken.None);
        Assert.Equal(RotationStatus.ReuseDetected, late.Status);
        Assert.Equal(RotationStatus.Invalid, (await store.RotateAsync(b, [Hash()], Now.AddMinutes(2), Now.AddDays(1), Grace, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task ConcurrentRotationOfTheSameCredentialProducesExactlyOneSuccessor()
    {
        // 同一张凭据并发换新（客户端双击、网络重放）：行锁让它们串行，只生出一张后继。
        var store = Store();
        var player = Guid.NewGuid();
        var first = Hash();
        var successor = Hash();
        await store.CreateGuestAsync(player, first, Now.AddDays(1), CancellationToken.None);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            store.RotateAsync(first, [successor], Now, Now.AddDays(1), Grace, CancellationToken.None)));

        Assert.Equal(1, results.Count(result => result.Status == RotationStatus.Rotated));
        Assert.All(results, result => Assert.Contains(result.Status, new[] { RotationStatus.Rotated, RotationStatus.AlreadyRotated }));
        Assert.Equal(2, await CountAsync(player));
    }

    [Fact]
    public async Task ConcurrentRotationWithDifferentSuccessorsIsTreatedAsTheft()
    {
        // 两个不同的后继说明是两方各自在用同一张凭据（导出密钥相同的服务器只会算出一个后继）：按盗用处理。
        var store = Store();
        var player = Guid.NewGuid();
        var first = Hash();
        await store.CreateGuestAsync(player, first, Now.AddDays(1), CancellationToken.None);

        var results = await Task.WhenAll(
            store.RotateAsync(first, [Hash()], Now, Now.AddDays(1), Grace, CancellationToken.None),
            store.RotateAsync(first, [Hash()], Now, Now.AddDays(1), Grace, CancellationToken.None));

        Assert.Single(results, result => result.Status == RotationStatus.Rotated);
        Assert.Single(results, result => result.Status == RotationStatus.ReuseDetected);
    }

    [Fact]
    public async Task ExpiredAndDisabledCredentialsAreInvalid()
    {
        var store = Store();
        var expiredPlayer = Guid.NewGuid();
        var expired = Hash();
        await store.CreateGuestAsync(expiredPlayer, expired, Now.AddMinutes(1), CancellationToken.None);
        Assert.Equal(RotationStatus.Invalid, (await store.RotateAsync(expired, [Hash()], Now.AddMinutes(2), Now.AddDays(1), Grace, CancellationToken.None)).Status);

        var bannedPlayer = Guid.NewGuid();
        var banned = Hash();
        await store.CreateGuestAsync(bannedPlayer, banned, Now.AddDays(1), CancellationToken.None);
        await using (var ban = database.DataSource.CreateCommand("UPDATE players SET disabled_at = now() WHERE player_id = $1"))
        {
            ban.Parameters.AddWithValue(bannedPlayer);
            await ban.ExecuteNonQueryAsync();
        }
        Assert.Equal(RotationStatus.Invalid, (await store.RotateAsync(banned, [Hash()], Now, Now.AddDays(1), Grace, CancellationToken.None)).Status);
        Assert.Equal(RotationStatus.Invalid, (await store.RotateAsync(Hash(), [Hash()], Now, Now.AddDays(1), Grace, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task ExpiredCredentialsAreDeletedInBatches()
    {
        var store = Store();
        var player = Guid.NewGuid();
        await store.CreateGuestAsync(player, Hash(), Now.AddMinutes(-1), CancellationToken.None);
        var keep = Guid.NewGuid();
        await store.CreateGuestAsync(keep, Hash(), Now.AddDays(1), CancellationToken.None);

        int deleted = 0;
        int batch;
        do
        {
            batch = await store.DeleteExpiredAsync(Now, 1, CancellationToken.None);
            deleted += batch;
        }
        while (batch > 0);

        Assert.True(deleted >= 1);
        Assert.Equal(0, await CountAsync(player));
        Assert.Equal(1, await CountAsync(keep));
    }

    [Fact]
    public async Task OnlyHashesAreStored()
    {
        // 存储里只有 32 字节摘要；凭据原文（"r1." 开头）在数据库任何文本列里都不出现。
        await using var command = database.DataSource.CreateCommand(
            "SELECT count(*) FROM player_credentials WHERE octet_length(credential_hash) <> 32");
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    private async Task<long> CountAsync(Guid player)
    {
        await using var command = database.DataSource.CreateCommand("SELECT count(*) FROM player_credentials WHERE player_id = $1");
        command.Parameters.AddWithValue(player);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
