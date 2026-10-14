using Npgsql;
using Testcontainers.PostgreSql;

namespace DarkTide.Persistence.Tests;

/// <summary>整个测试类共享一个 PostgreSQL 17 容器；每个测试用自己的房间编号，互不干扰。</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // 版本与设计文档一致（PostgreSQL 17）。
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        await SchemaMigrator.MigrateAsync(DataSource);
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}
