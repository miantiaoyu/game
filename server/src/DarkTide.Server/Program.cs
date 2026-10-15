using DarkTide.Persistence;
using DarkTide.Room;
using DarkTide.Server;
using Npgsql;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);

// ── 房间配置：绑定 Room 节，启动时校验，错误配置无法上线 ──────────────────
builder.Services.AddOptions<RoomOptions>()
    .Bind(builder.Configuration.GetSection(RoomOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<RoomOptions>, RoomOptionsValidator>();

// 宿主总时限必须覆盖房间排空时限，否则宿主先超时，房间的有界关闭形同虚设。
// WebApplication 默认不从配置绑定 HostOptions，这里显式绑定，运维才能用 HostOptions:ShutdownTimeout 调整。
// 选项按注册顺序生效：先绑定配置，再做“只调大、不调小”的修正，显式配得更长时保留运维的值。
builder.Services.AddOptions<HostOptions>()
    .Bind(builder.Configuration.GetSection("HostOptions"))
    .Configure<IConfiguration>((host, configuration) =>
    {
        // 这里直接读配置，不读 IOptions<RoomOptions>.Value：HostOptions 在 Build() 时就被解析，
        // 那时读取会触发校验，配置错误会变成未处理异常（进程以 134 中止），日志系统都还没建好。
        // 配置无效时跳过修正，交给 ValidateOnStart 在启动阶段统一报告。
        var room = configuration.GetSection(RoomOptions.SectionName).Get<RoomOptions>() ?? RoomOptions.Default;
        if (room.Validate().Count > 0)
        {
            return;
        }

        var required = room.ShutdownTimeout + ServerDefaults.ShutdownMargin;
        if (host.ShutdownTimeout < required)
        {
            host.ShutdownTimeout = required;
        }
    });

// ── 持久化：配置了连接串才启用；没有时房间只在内存运行，启动时记警告 ─────────────
// 连接串通过 ConnectionStrings__DarkTide 环境变量或密钥管理注入，不写进 appsettings。
var connectionString = builder.Configuration.GetConnectionString(ServerDefaults.ConnectionStringName);
bool persistent = !string.IsNullOrWhiteSpace(connectionString);
if (persistent)
{
    builder.Services.AddSingleton(_ =>
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        // 连接串里没写时才用这里的默认值，运维显式配置的优先。
        // 每条命令只占用连接一次往返；房间之间并行，连接池上限决定同时在途的提交数。
        if (!settings.ContainsKey("Maximum Pool Size"))
        {
            settings.MaxPoolSize = ServerDefaults.DatabaseMaxPoolSize;
        }
        // 取连接超时略大于日志超时：正常情况下由 Room:JournalTimeout 先触发，并报告为“结果未知”。
        if (!settings.ContainsKey("Timeout"))
        {
            settings.Timeout = ServerDefaults.DatabaseConnectTimeoutSeconds;
        }
        return NpgsqlDataSource.Create(settings.ConnectionString);
    });
    builder.Services.AddSingleton<IRoomJournal, PostgresRoomJournal>();
    // 必须先于房间目录注册：宿主按注册顺序启动，恢复房间前表已建好。
    builder.Services.AddHostedService<SchemaMigrationService>();
}

// ── 房间目录：作为托管服务注册，启动时恢复房间，进程退出时由宿主调用 StopAsync ────
// 注册早于 Web 服务器（Build 时才加入），宿主按逆序停止：Kestrel 先停止接收请求，房间后停。
builder.Services.AddSingleton(services => new RoomDirectory(
    services.GetRequiredService<IOptions<RoomOptions>>(),
    services.GetRequiredService<ILoggerFactory>(),
    services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(),
    services.GetService<IRoomJournal>()));
builder.Services.AddHostedService(services => services.GetRequiredService<RoomDirectory>());

// ── 健康检查 ──────────────────────────────────────────────────────────
var healthChecks = builder.Services.AddHealthChecks()
    .AddCheck("process", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: [ServerDefaults.LiveTag])
    .AddCheck<ReadinessHealthCheck>("ready", tags: [ServerDefaults.ReadyTag]);
if (persistent)
{
    healthChecks.AddCheck<DatabaseHealthCheck>("database", tags: [ServerDefaults.ReadyTag]);
}

// ── 指标导出：只有配置了 OTLP 地址才启用，避免本地开发时不停尝试连接不存在的采集器 ──
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(
            serviceName: builder.Configuration["OTEL_SERVICE_NAME"] ?? ServerDefaults.ServiceName))
        .WithMetrics(metrics => metrics
            .AddMeter(
                "DarkTide.Room",
                RoomDirectory.MeterName,
                // ASP.NET Core 与运行时自带的指标，不需要额外的 instrumentation 包。
                "Microsoft.AspNetCore.Hosting",
                "Microsoft.AspNetCore.Server.Kestrel",
                "System.Runtime")
            .AddOtlpExporter());
}

var app = builder.Build();

// 健康端点不需要认证，也不暴露细节；生产环境应只在内网或管理端口可达。
app.MapHealthChecks("/health/live", new() { Predicate = check => check.Tags.Contains(ServerDefaults.LiveTag) });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains(ServerDefaults.ReadyTag) });

// 还没有身份认证，所以这里刻意不开放任何房间操作端点。
try
{
    await app.RunAsync();
    return 0;
}
catch (OptionsValidationException error)
{
    // 配置错误不是程序崩溃：给出一行可读的原因和约定的退出码，而不是未处理异常导致的进程中止（134）。
    // 编排器据此判定“配置问题，不要无限重启”，运维也能从日志里直接看到要改的键。
    app.Logger.LogCritical("Configuration is invalid; the server will not start. {Failures}", string.Join(" ", error.Failures));
    return ServerDefaults.ExitCodeInvalidConfiguration;
}

// 供集成测试（WebApplicationFactory）引用入口程序集。
public partial class Program;
