using DarkTide.Persistence;
using Npgsql;

namespace DarkTide.Server;

/// <summary>
/// 两个数据库账号：
/// - 运行账号（ConnectionStrings:DarkTide）：服务日常使用，只能追加日志、登记和关闭房间，不能改结构；
/// - 迁移账号（ConnectionStrings:DarkTideMigrations）：表的所有者，只在部署时执行迁移并给运行账号授权。
/// 生产环境只给服务运行账号；迁移用 <c>--migrate</c> 作为单独的部署步骤执行。
/// </summary>
internal static class DatabaseSetup
{
    /// <summary>用迁移账号执行迁移，并给运行账号授予最小权限。两个账号相同时跳过授权并警告。</summary>
    public static async Task<IReadOnlyList<string>> MigrateAndGrantAsync(
        string migrationConnectionString, string runtimeConnectionString, ILogger logger, CancellationToken cancellationToken)
    {
        await using var owner = NpgsqlDataSource.Create(migrationConnectionString);
        var applied = await SchemaMigrator.MigrateAsync(owner, cancellationToken);
        if (applied.Count > 0)
        {
            logger.LogInformation("Applied database migrations: {Migrations}.", string.Join(", ", applied));
        }

        string ownerRole = new NpgsqlConnectionStringBuilder(migrationConnectionString).Username ?? "";
        string runtimeRole = new NpgsqlConnectionStringBuilder(runtimeConnectionString).Username ?? "";
        if (string.Equals(ownerRole, runtimeRole, StringComparison.Ordinal))
        {
            // 对所有者执行 REVOKE 会收回它自己的权限，所以不能授权；只记警告。
            logger.LogWarning(
                "The runtime and migration database accounts are the same ({Role}); the journal is not protected by privileges, only by triggers. Use a separate runtime account in production.",
                runtimeRole);
        }
        else
        {
            await SchemaMigrator.GrantRuntimeRoleAsync(owner, runtimeRole, cancellationToken);
            logger.LogInformation("Granted least-privilege journal access to database role {Role}.", runtimeRole);
        }
        return applied;
    }

    /// <summary>运行账号启动时的检查：结构已是最新；账号若是超级用户则警告（超级用户可以绕过权限、停用触发器）。</summary>
    public static async Task VerifyRuntimeAsync(NpgsqlDataSource runtime, ILogger logger, CancellationToken cancellationToken)
    {
        await SchemaMigrator.EnsureUpToDateAsync(runtime, cancellationToken);

        await using var command = runtime.CreateCommand("SELECT rolsuper FROM pg_roles WHERE rolname = current_user");
        if (await command.ExecuteScalarAsync(cancellationToken) is true)
        {
            logger.LogWarning("The runtime database account is a superuser; it can bypass privileges and disable the append-only triggers. Use a dedicated least-privilege account in production.");
        }
    }
}
