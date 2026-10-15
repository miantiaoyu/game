using Npgsql;
using Testcontainers.PostgreSql;

namespace DarkTide.Persistence.Tests;

/// <summary>整个测试类共享一个 PostgreSQL 17 容器；每个测试用自己的房间编号，互不干扰。</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // 版本与设计文档一致（PostgreSQL 17）。
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>用调整过的连接串新建数据源，例如限制连接池大小。调用方负责释放。</summary>
    public NpgsqlDataSource CreateDataSource(Action<NpgsqlConnectionStringBuilder> configure)
    {
        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());
        configure(builder);
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

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
