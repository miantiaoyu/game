using System.Diagnostics;
using System.Text.Json;

namespace DarkTide.Server.Tests;

/// <summary>
/// 以真实子进程启动服务器。进程内的 WebApplicationFactory 测不到退出码与日志落地格式，
/// 而这两样正是编排器（systemd、Kubernetes）与日志采集依赖的契约。
/// </summary>
public class ServerProcessTests
{
    [Fact]
    public async Task InvalidConfigurationExitsWithConfigErrorCodeAndListsEveryKey()
    {
        var result = await RunServerAsync(new Dictionary<string, string>
        {
            ["Room__CommandQueueCapacity"] = "0",
            ["Room__ShutdownTimeout"] = "-00:00:01"
        });

        // 78 = EX_CONFIG。未处理异常会是 134（SIGABRT），编排器会把它当崩溃反复重启。
        Assert.Equal(78, result.ExitCode);

        var critical = Assert.Single(result.Logs, entry => entry.Level == "Critical");
        Assert.Contains("Room:CommandQueueCapacity", critical.Message);
        Assert.Contains("Room:ShutdownTimeout", critical.Message);
        // 生产环境的每一行都必须是 JSON，日志采集才能解析；未处理异常的堆栈会混进纯文本行。
        Assert.Empty(result.NonJsonLines);
    }

    [Fact]
    public async Task MissingSigningKeyIsAConfigurationErrorNotAnOpenDoor()
    {
        // 没有密钥就没有身份；不能退化成“谁都能冒充”。按配置错误退出，并指出要配的键。
        var result = await RunServerAsync(new Dictionary<string, string>(), includeSessionKeys: false);

        Assert.Equal(78, result.ExitCode);
        var critical = Assert.Single(result.Logs, entry => entry.Level == "Critical");
        Assert.Contains("Session:Keys", critical.Message);
        Assert.Empty(result.NonJsonLines);
    }

    [Fact]
    public async Task UnreachableDatabaseExitsCleanlyInsteadOfAborting()
    {
        // 端口 1 上没有数据库：启动时连接失败。以前异常逃出 Main，进程以 134 中止，堆栈混进日志。
        var result = await RunServerAsync(new Dictionary<string, string>
        {
            ["ConnectionStrings__DarkTide"] = "Host=127.0.0.1;Port=1;Username=nobody;Password=x;Timeout=2"
        });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.Logs, entry => entry.Level == "Critical" && entry.Message.Contains("failed to start"));
        Assert.Empty(result.NonJsonLines);
    }

    private sealed record LogLine(string Level, string Message);

    private sealed record ProcessResult(int ExitCode, IReadOnlyList<LogLine> Logs, IReadOnlyList<string> NonJsonLines);

    private static async Task<ProcessResult> RunServerAsync(IReadOnlyDictionary<string, string> environment, bool includeSessionKeys = true)
    {
        // 服务器程序集与 appsettings.json 随项目引用复制到测试输出目录。
        var serverDll = Path.Combine(AppContext.BaseDirectory, "DarkTide.Server.dll");
        // 测试由 dotnet 宿主进程运行；SDK 也会设置 DOTNET_HOST_PATH。两者都指向同一个 dotnet。
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!;

        var start = new ProcessStartInfo(dotnet, [serverDll])
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        // 端口 0：万一意外启动成功，也不会和别的进程抢端口。
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        foreach (var (key, value) in (includeSessionKeys ? TestSessionKeys.Environment : new Dictionary<string, string>()).Concat(environment))
        {
            start.Environment[key] = value;
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("服务器进程在 30 秒内没有退出；配置错误时它应当立即退出。");
        }

        var logs = new List<LogLine>();
        var nonJson = new List<string>();
        foreach (var line in ((await stdout) + "\n" + (await stderr)).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                logs.Add(new LogLine(
                    document.RootElement.GetProperty("LogLevel").GetString()!,
                    document.RootElement.GetProperty("Message").GetString()!));
            }
            catch (JsonException)
            {
                nonJson.Add(trimmed);
            }
        }

        return new ProcessResult(process.ExitCode, logs, nonJson);
    }
}
