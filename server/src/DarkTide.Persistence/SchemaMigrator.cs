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

    /// <summary>
    /// 给运行账号授予最小权限：能读写日志所需的行，不能改结构、不能修改或删除日志。
    /// 由迁移账号（表的所有者）调用；每次迁移后都执行，新表也会覆盖到。可以重复调用。
    ///
    /// 分权的意义：运行进程被攻破或出了缺陷时，最多能追加记录、标记房间关闭，
    /// 不能改写历史、删掉证据、或改动表结构绕过触发器。
    /// </summary>
    public static async Task GrantRuntimeRoleAsync(NpgsqlDataSource ownerDataSource, string runtimeRole, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRole);
        await using var connection = await ownerDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // 角色名不能参数化，只能拼进语句；先在数据库里确认它是一个已存在的角色，再用 quote_ident 引用，杜绝注入。
        string quoted;
        await using (var check = new NpgsqlCommand("SELECT quote_ident(rolname) FROM pg_roles WHERE rolname = $1", connection))
        {
            check.Parameters.AddWithValue(runtimeRole);
            quoted = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
                ?? throw new InvalidOperationException($"数据库中没有角色 {runtimeRole}，请先由管理员创建。");
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var sql in new[]
        {
            // 先收回再授予：重复执行时结果确定，不会残留以前多给的权限。
            $"REVOKE ALL ON rooms, room_commands, schema_migrations, players, player_credentials FROM {quoted}",
            $"GRANT SELECT, INSERT ON room_commands TO {quoted}",
            $"GRANT SELECT, INSERT ON rooms TO {quoted}",
            // 只能更新 closed_at 一列；触发器再保证只能从“未关闭”改为“已关闭”。
            $"GRANT UPDATE (closed_at) ON rooms TO {quoted}",
            // 运行账号启动时要确认结构已是最新，只读即可。
            $"GRANT SELECT ON schema_migrations TO {quoted}",
            // 游客身份：能创建玩家，不能改封禁状态（封禁由运维账号做）。
            $"GRANT SELECT, INSERT ON players TO {quoted}",
            // 凭据：签发、轮换、吊销、清理过期的。不能改摘要、玩家与过期时间。
            $"GRANT SELECT, INSERT, DELETE ON player_credentials TO {quoted}",
            $"GRANT UPDATE (replaced_at, replaced_by, revoked_at) ON player_credentials TO {quoted}",
        })
        {
            await Execute(connection, transaction, sql, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 运行账号启动时调用：确认所有嵌入的结构脚本都已执行。没有迁移权限的账号不能自己补，
    /// 缺了就说明部署顺序错了（新代码先于迁移上线），直接拒绝启动。
    /// </summary>
    public static async Task EnsureUpToDateAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        var expected = typeof(SchemaMigrator).Assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(ScriptName)
            .ToHashSet(StringComparer.Ordinal);

        await using var command = dataSource.CreateCommand("SELECT name FROM schema_migrations");
        var applied = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                applied.Add(reader.GetString(0));
            }
        }
        catch (PostgresException error) when (error.SqlState == "42P01")
        {
            // 表不存在：一次迁移都没做过。
        }

        var missing = expected.Except(applied).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw new SchemaNotUpToDateException(missing);
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

/// <summary>数据库缺少本版本需要的迁移。属于部署顺序错误（新代码先于迁移上线），不是程序故障。</summary>
public sealed class SchemaNotUpToDateException(IReadOnlyList<string> missing)
    : Exception($"数据库结构不是最新，缺少迁移：{string.Join(", ", missing)}。请先用迁移账号运行迁移（--migrate），再启动服务。")
{
    public IReadOnlyList<string> Missing { get; } = missing;
}
