using Microsoft.Extensions.Configuration;

namespace DarkTide.Room.Tests;

public class RoomOptionsTests
{
    [Fact]
    public void DefaultsMatchTheDesignDocument()
    {
        var options = RoomOptions.Default;

        Assert.Equal(56, options.CommandQueueCapacity);
        Assert.Equal(8, options.QueryQueueCapacity);
        Assert.Equal(128, options.ReceiptCacheCapacity);
        Assert.Equal(TimeSpan.FromSeconds(10), options.ShutdownTimeout);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void ValidateReportsEveryProblemWithItsConfigurationKey()
    {
        var options = new RoomOptions
        {
            CommandQueueCapacity = 0,
            QueryQueueCapacity = RoomOptions.MaxQueueCapacity + 1,
            ReceiptCacheCapacity = -5,
            ShutdownTimeout = TimeSpan.FromSeconds(-1)
        };

        var errors = options.Validate();

        // 一次列出全部错误，运维改一次就能启动。
        Assert.Equal(4, errors.Count);
        Assert.Contains(errors, error => error.Contains("Room:CommandQueueCapacity"));
        Assert.Contains(errors, error => error.Contains("Room:QueryQueueCapacity"));
        Assert.Contains(errors, error => error.Contains("Room:ReceiptCacheCapacity"));
        Assert.Contains(errors, error => error.Contains("Room:ShutdownTimeout"));
        var thrown = Assert.Throws<ArgumentException>(options.EnsureValid);
        Assert.Contains("Room:CommandQueueCapacity", thrown.Message);
    }

    [Fact]
    public void ZeroShutdownTimeoutIsAllowed()
    {
        Assert.Empty(new RoomOptions { ShutdownTimeout = TimeSpan.Zero }.Validate());
        Assert.NotEmpty(new RoomOptions { ShutdownTimeout = RoomOptions.MaxShutdownTimeout + TimeSpan.FromSeconds(1) }.Validate());
    }

    [Fact]
    public void BindsFromJsonAndEnvironmentStyleKeys()
    {
        // 用真实的配置绑定器：确认属性名、init 访问器与 TimeSpan 格式都能被宿主直接绑定。
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Room:CommandQueueCapacity"] = "32",
                ["Room:QueryQueueCapacity"] = "4",
                ["Room:ReceiptCacheCapacity"] = "256",
                ["Room:ShutdownTimeout"] = "00:00:03"
            })
            .Build();

        var options = configuration.GetSection(RoomOptions.SectionName).Get<RoomOptions>();

        Assert.NotNull(options);
        Assert.Equal(32, options.CommandQueueCapacity);
        Assert.Equal(4, options.QueryQueueCapacity);
        Assert.Equal(256, options.ReceiptCacheCapacity);
        Assert.Equal(TimeSpan.FromSeconds(3), options.ShutdownTimeout);
    }

    [Fact]
    public void MissingKeysKeepDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Room:QueryQueueCapacity"] = "2" })
            .Build();

        var options = configuration.GetSection(RoomOptions.SectionName).Get<RoomOptions>()!;

        Assert.Equal(2, options.QueryQueueCapacity);
        Assert.Equal(RoomOptions.DefaultCommandQueueCapacity, options.CommandQueueCapacity);
        Assert.Equal(RoomOptions.DefaultShutdownTimeout, options.ShutdownTimeout);
    }

    [Fact]
    public async Task ExecutorUsesConfiguredQuotas()
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(commands: 2), signal.Task);
        try
        {
            var first = executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Guid.NewGuid(), 0));
            var second = executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Guid.NewGuid(), 1));
            await Assert.ThrowsAsync<RoomBusyException>(
                () => executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Guid.NewGuid(), 2)));

            signal.SetResult(true);
            await Task.WhenAll(first, second);
        }
        finally
        {
            signal.TrySetResult(true);
            await executor.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExecutorUsesConfiguredReceiptCacheCapacity()
    {
        // 容量 1：第二条命令进来后第一条回执被淘汰，重发第一条会再执行一次规则并被拒绝。
        await using var executor = new RoomExecutor(Guid.NewGuid(), 8, TestOptions.With(receipts: 1));
        var player = Guid.NewGuid();
        var first = new TakeSeatCommand(Guid.NewGuid(), player, 0);
        await executor.ExecuteAsync(first);
        await executor.ExecuteAsync(new LeaveSeatCommand(Guid.NewGuid(), player));

        // 回执已淘汰：重发不再命中原结果，而是按当前状态重新执行（又坐回 0 号座）。
        await executor.ExecuteAsync(first);
        Assert.Equal<int?>(0, await executor.FindSeatAsync(player));
    }

    [Fact]
    public void InvalidOptionsFailFastAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new RoomExecutor(Guid.NewGuid(), 8, TestOptions.With(commands: 0)));
        Assert.Throws<ArgumentException>(() =>
            new RoomExecutor(new Room(Guid.NewGuid(), 8), new RoomOptions { ShutdownTimeout = TimeSpan.FromDays(1) }));
    }
}
