using System.Reflection;
using Npgsql;

namespace DarkTide.Persistence;

/// <summary>
/// 按文件名顺序执行嵌入的结构脚本，每个脚本只执行一次（记录在 schema_migrations）。
/// 用咨询锁串行化：多个实例同时启动时只有一个在迁移，其余等待后发现已完成。
/// 脚本只追加、不修改：已部署的脚本改动后不会重跑，需要新写一个。
/// </summary>
public static class SchemaMigrator
{
    // 任意固定的 64 位数，只需在本数据库内与其他咨询锁不冲突。
    private const long AdvisoryLockKey = 0x4441524B54494445;

    public static async Task<IReadOnlyList<string>> MigrateAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        var scripts = typeof(SchemaMigrator).Assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await Execute(connection, null, $"SELECT pg_advisory_lock({AdvisoryLockKey})", cancellationToken).ConfigureAwait(false);
        try
        {
            await Execute(connection, null,
                "CREATE TABLE IF NOT EXISTS schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())",
                cancellationToken).ConfigureAwait(false);

            var applied = new List<string>();
            foreach (var resource in scripts)
            {
                string name = ScriptName(resource);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (var check = new NpgsqlCommand("SELECT 1 FROM schema_migrations WHERE name = $1", connection, transaction))
                {
                    check.Parameters.AddWithValue(name);
                    if (await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    {
                        continue;
                    }
                }

                // 脚本与登记在同一事务里：中途失败时两者一起回滚，下次启动重试。
                await Execute(connection, transaction, await ReadScript(resource).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                await using (var record = new NpgsqlCommand("INSERT INTO schema_migrations (name) VALUES ($1)", connection, transaction))
                {
                    record.Parameters.AddWithValue(name);
                    await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                applied.Add(name);
            }
            return applied;
        }
        finally
        {
            await Execute(connection, null, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None).ConfigureAwait(false);
        }
    }

    // 资源名形如 DarkTide.Persistence.Schema.001_room_journal.sql，只取文件名部分作为登记名。
    private static string ScriptName(string resource)
    {
        const string marker = ".Schema.";
        int index = resource.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? resource : resource[(index + marker.Length)..];
    }

    private static async Task<string> ReadScript(string resource)
    {
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"找不到嵌入的结构脚本 {resource}。");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
