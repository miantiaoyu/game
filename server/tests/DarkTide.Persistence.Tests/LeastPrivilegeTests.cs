using DarkTide.Room;
using DarkTide.Rules;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DarkTide.Persistence.Tests;

/// <summary>
/// 分权测试共享的独立容器：一个所有者账号负责迁移，一个运行账号只拿最小权限。
/// 用类夹具而不是测试类自己的 IAsyncLifetime：后者在 xUnit 里每个测试用例都会执行一次，
/// 二十来个用例就要依次起二十来个容器。
/// </summary>
public sealed class LeastPrivilegeFixture : IAsyncLifetime
{
    public const string RuntimeRole = "darktide_app";
    private const string RuntimePassword = "test-only-runtime";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public NpgsqlDataSource Owner { get; private set; } = null!;
    public NpgsqlDataSource Runtime { get; private set; } = null!;
    public string AdminConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var admin = _container.GetConnectionString();
        await using (var superuser = NpgsqlDataSource.Create(admin))
        {
            // 模拟生产：所有者与运行账号都不是超级用户，由管理员事先建好。
            await using var setup = superuser.CreateCommand($"""
                CREATE ROLE darktide_owner LOGIN PASSWORD 'test-only-owner';
                CREATE ROLE {RuntimeRole} LOGIN PASSWORD '{RuntimePassword}';
                GRANT CREATE ON SCHEMA public TO darktide_owner;
                GRANT USAGE ON SCHEMA public TO {RuntimeRole};
                """);
            await setup.ExecuteNonQueryAsync();
        }

        Owner = NpgsqlDataSource.Create(WithUser(admin, "darktide_owner", "test-only-owner"));
        Runtime = NpgsqlDataSource.Create(WithUser(admin, RuntimeRole, RuntimePassword));
        await SchemaMigrator.MigrateAsync(Owner);
        await SchemaMigrator.GrantRuntimeRoleAsync(Owner, RuntimeRole);
    }

    public async Task DisposeAsync()
    {
        await Runtime.DisposeAsync();
        await Owner.DisposeAsync();
        await _container.DisposeAsync();
    }

    private static string WithUser(string connectionString, string user, string password) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Username = user, Password = password }.ConnectionString;
}

/// <summary>验证运行账号能正常工作，但改不了、删不掉日志，也改不了结构；所有者同样被触发器挡住。</summary>
public class LeastPrivilegeTests(LeastPrivilegeFixture database) : IClassFixture<LeastPrivilegeFixture>
{
    private const string RuntimeRole = LeastPrivilegeFixture.RuntimeRole;
    private NpgsqlDataSource _owner => database.Owner;
    private NpgsqlDataSource _runtime => database.Runtime;

    [Fact]
    public async Task RuntimeAccountCanRunRoomsEndToEnd()
    {
        var journal = new PostgresRoomJournal(_runtime);
        var roomId = Guid.NewGuid();
        var player = Guid.NewGuid();
        await using (var executor = await RoomExecutor.CreatePersistentAsync(roomId, 4, journal))
        {
            await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), player, 1));
            await Assert.ThrowsAsync<RuleRejectedException>(() => executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Guid.NewGuid(), 1)));
        }

        await using (var restored = await RoomExecutor.RestoreAsync(roomId, journal))
        {
            Assert.Equal<int?>(1, await restored.FindSeatAsync(player));
        }
        await journal.MarkClosedAsync(roomId, CancellationToken.None);
        Assert.DoesNotContain(roomId, await journal.ListOpenRoomsAsync(CancellationToken.None));
        await SchemaMigrator.EnsureUpToDateAsync(_runtime);
    }

    [Theory]
    [InlineData("UPDATE room_commands SET applied_version = 99")]
    [InlineData("DELETE FROM room_commands")]
    [InlineData("TRUNCATE room_commands")]
    [InlineData("DELETE FROM rooms")]
    [InlineData("UPDATE rooms SET seat_count = 8")]
    [InlineData("DROP TABLE room_commands")]
    [InlineData("ALTER TABLE room_commands DISABLE TRIGGER room_commands_append_only")]
    [InlineData("CREATE TABLE sneaky (id int)")]
    [InlineData("DELETE FROM schema_migrations")]
    public async Task RuntimeAccountCannotRewriteHistoryOrSchema(string sql)
    {
        await SeedRoomAsync();
        await using var command = _runtime.CreateCommand(sql);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        // 42501：权限不足。非所有者执行 DROP／ALTER 也报这个代码；PostgreSQL 15 起 public 模式默认不给普通账号建表权限。
        Assert.Equal("42501", error.SqlState);
    }

    [Theory]
    [InlineData("UPDATE room_commands SET applied_version = 99")]
    [InlineData("DELETE FROM room_commands")]
    [InlineData("TRUNCATE room_commands")]
    [InlineData("DELETE FROM rooms")]
    [InlineData("TRUNCATE rooms CASCADE")]
    [InlineData("UPDATE rooms SET seat_count = 8")]
    public async Task EvenTheOwnerCannotRewriteTheJournal(string sql)
    {
        await SeedRoomAsync();
        await using var command = _owner.CreateCommand(sql);

        // 权限可能被误配，触发器在权限之外再挡一层。
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("42501", error.SqlState);
    }

    [Fact]
    public async Task ClosingIsTheOnlyAllowedChangeAndCannotBeUndone()
    {
        var roomId = await SeedRoomAsync();

        await using (var close = _runtime.CreateCommand("UPDATE rooms SET closed_at = now() WHERE room_id = $1"))
        {
            close.Parameters.AddWithValue(roomId);
            Assert.Equal(1, await close.ExecuteNonQueryAsync());
        }

        // 已关闭的房间不能重新打开，否则可以把结束的牌局拉回来继续写。
        await using var reopen = _runtime.CreateCommand("UPDATE rooms SET closed_at = NULL WHERE room_id = $1");
        reopen.Parameters.AddWithValue(roomId);
        var error = await Assert.ThrowsAsync<PostgresException>(() => reopen.ExecuteNonQueryAsync());
        Assert.Equal("42501", error.SqlState);
    }

    [Fact]
    public async Task GrantIsIdempotentAndRejectsUnknownRoles()
    {
        await SchemaMigrator.GrantRuntimeRoleAsync(_owner, RuntimeRole);
        await SchemaMigrator.GrantRuntimeRoleAsync(_owner, RuntimeRole);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrator.GrantRuntimeRoleAsync(_owner, "no_such_role"));
        // 角色名里带引号或分号也只会被当作名字查找，不会被执行。
        await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrator.GrantRuntimeRoleAsync(_owner, "x\"; DROP TABLE rooms; --"));
        await RuntimeAccountCanRunRoomsEndToEnd();
    }

    [Fact]
    public async Task RuntimeCheckRejectsADatabaseThatWasNeverMigrated()
    {
        await using (var superuser = NpgsqlDataSource.Create(database.AdminConnectionString))
        await using (var create = superuser.CreateCommand("CREATE DATABASE empty_db"))
        {
            await create.ExecuteNonQueryAsync();
        }
        var builder = new NpgsqlConnectionStringBuilder(database.AdminConnectionString) { Database = "empty_db" };
        await using var empty = NpgsqlDataSource.Create(builder.ConnectionString);

        var error = await Assert.ThrowsAsync<SchemaNotUpToDateException>(() => SchemaMigrator.EnsureUpToDateAsync(empty));
        Assert.Contains("001_room_journal.sql", error.Missing);
        Assert.Contains("002_append_only.sql", error.Missing);
    }

    private async Task<Guid> SeedRoomAsync()
    {
        var journal = new PostgresRoomJournal(_runtime);
        var roomId = Guid.NewGuid();
        await journal.CreateRoomAsync(roomId, 4, CancellationToken.None);
        await journal.AppendAsync(roomId, new JournalEntry(1, new TakeSeatCommand(Guid.NewGuid(), Guid.NewGuid(), 0), CommandOutcome.Applied(1)), CancellationToken.None);
        return roomId;
    }
}
