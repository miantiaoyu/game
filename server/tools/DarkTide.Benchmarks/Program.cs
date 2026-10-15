using DarkTide.Benchmarks;
using DarkTide.Persistence;
using Npgsql;

// 在真实 PostgreSQL 上测：命令确认延迟、每条命令的数据库调用次数、数据库卡住时多久失败、启动恢复耗时与内存。
// 用法：ConnectionStrings__DarkTide=... dotnet run -c Release --project server/tools/DarkTide.Benchmarks -- [steady|stall|restore|all]
// 每次请用一个空库：恢复场景会批量写入大量房间。
var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DarkTide")
    ?? throw new InvalidOperationException("请设置环境变量 ConnectionStrings__DarkTide。");
string scenario = args.Length > 0 ? args[0] : "all";

await using var dataSource = NpgsqlDataSource.Create(connectionString);
await SchemaMigrator.MigrateAsync(dataSource);

if (scenario is "steady" or "all")
{
    await SteadyLoad.RunAsync(dataSource, rooms: 200, warmCommandsPerRoom: 200, rates: [200, 1000], duration: TimeSpan.FromSeconds(20));
}
if (scenario is "stall" or "all")
{
    await StalledDatabase.RunAsync(dataSource);
}
if (scenario is "restore" or "all")
{
    await StartupRestore.RunAsync(dataSource, rooms: 1000, commandsPerRoom: 360);
}
