namespace DarkTide.Server;

internal static class ServerDefaults
{
    public const string ServiceName = "darktide-server";

    /// <summary>连接串名：ConnectionStrings:DarkTide，环境变量 ConnectionStrings__DarkTide。</summary>
    public const string ConnectionStringName = "DarkTide";

    /// <summary>迁移账号的连接串名：ConnectionStrings:DarkTideMigrations。只在部署迁移时需要。</summary>
    public const string MigrationConnectionStringName = "DarkTideMigrations";

    /// <summary>
    /// 连接池上限。实测本机数据库在 200 房间饱和写入时约 1500 提交/秒，瓶颈是落盘而不是连接数；
    /// 50 足够让多个提交合并落盘，又不至于压垮数据库。PostgreSQL 默认 max_connections 为 100，多实例部署时要一起核算。
    /// </summary>
    public const int DatabaseMaxPoolSize = 50;

    /// <summary>取连接与建立连接的超时（秒）。</summary>
    public const int DatabaseConnectTimeoutSeconds = 10;
    public const string LiveTag = "live";
    public const string ReadyTag = "ready";

    /// <summary>配置无效时的退出码。取 78（sysexits.h 的 EX_CONFIG），systemd 等工具能识别为配置错误。</summary>
    public const int ExitCodeInvalidConfiguration = 78;

    /// <summary>其他启动失败（例如数据库不可达）的退出码。可能是暂时问题，可以按常规策略重启。</summary>
    public const int ExitCodeStartupFailed = 1;

    /// <summary>宿主总时限比房间排空时限多留的余量：留给 Kestrel 关闭连接与日志落盘。</summary>
    public static readonly TimeSpan ShutdownMargin = TimeSpan.FromSeconds(5);
}
