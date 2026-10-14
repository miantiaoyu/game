using System.Diagnostics.Metrics;
using System.Net;
using DarkTide.Room;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DarkTide.Server.Tests;

public class ServerHostTests
{
    private static WebApplicationFactory<Program> CreateFactory(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    [Fact]
    public async Task LiveAndReadyEndpointsReportHealthy()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RoomOptionsAreBoundFromConfiguration()
    {
        await using var factory = CreateFactory(
            ("Room:CommandQueueCapacity", "12"),
            ("Room:ShutdownTimeout", "00:00:02"));
        _ = factory.CreateClient();

        var options = factory.Services.GetRequiredService<IOptions<RoomOptions>>().Value;

        Assert.Equal(12, options.CommandQueueCapacity);
        Assert.Equal(TimeSpan.FromSeconds(2), options.ShutdownTimeout);
    }

    [Fact]
    public async Task HostShutdownTimeoutCoversRoomDrainTimeout()
    {
        await using var factory = CreateFactory(("Room:ShutdownTimeout", "00:01:00"));
        _ = factory.CreateClient();

        var host = factory.Services.GetRequiredService<IOptions<HostOptions>>().Value;

        Assert.Equal(TimeSpan.FromMinutes(1) + ServerDefaults.ShutdownMargin, host.ShutdownTimeout);
    }

    [Fact]
    public async Task ExplicitLongerHostShutdownTimeoutIsKept()
    {
        await using var factory = CreateFactory(
            ("Room:ShutdownTimeout", "00:00:05"),
            ("HostOptions:ShutdownTimeout", "00:02:00"));
        _ = factory.CreateClient();

        var host = factory.Services.GetRequiredService<IOptions<HostOptions>>().Value;

        Assert.Equal(TimeSpan.FromMinutes(2), host.ShutdownTimeout);
    }

    [Fact]
    public async Task NoRoomEndpointsAreExposedWithoutAuthentication()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // 还没有身份认证：除健康检查外不应有任何可访问的端点。
        var response = await client.GetAsync("/rooms");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task HostShutdownStopsAllRoomsAndMarksNotReady()
    {
        var factory = CreateFactory(("Room:ShutdownTimeout", "00:00:02"));
        _ = factory.CreateClient();
        var directory = factory.Services.GetRequiredService<RoomDirectory>();
        var first = await directory.CreateRoomAsync(Guid.NewGuid(), 5);
        var second = await directory.CreateRoomAsync(Guid.NewGuid(), 8);
        await first.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Guid.NewGuid(), 0));

        await factory.DisposeAsync();

        // 宿主退出时目录停止了所有房间；关闭后不再接受新房间。
        await first.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        await second.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(directory.IsStopping);
        await Assert.ThrowsAsync<InvalidOperationException>(() => directory.CreateRoomAsync(Guid.NewGuid(), 4));
    }

    [Fact]
    public async Task ReadinessTurnsUnhealthyWhenDirectoryStops()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var directory = factory.Services.GetRequiredService<RoomDirectory>();

        await directory.StopAsync(CancellationToken.None);

        var ready = await client.GetAsync("/health/ready");
        var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        // 关闭中的进程仍然存活，编排器不应把它当卡死强杀。
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task ActiveRoomGaugeIsPublished()
    {
        await using var factory = CreateFactory();
        _ = factory.CreateClient();
        var directory = factory.Services.GetRequiredService<RoomDirectory>();
        var meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        await directory.CreateRoomAsync(Guid.NewGuid(), 5);
        await directory.CreateRoomAsync(Guid.NewGuid(), 5);

        var observed = new Dictionary<string, int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            // 只订阅本宿主创建的 Meter：同进程里其他测试的宿主不会串进来。
            if (instrument.Meter.Name == RoomDirectory.MeterName && instrument.Meter.Scope == meterFactory)
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, value, _, _) => observed[instrument.Name] = value);
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Equal(2, observed["darktide.rooms.active"]);
        Assert.Equal(0, observed["darktide.rooms.waiting_commands"]);
    }
}
