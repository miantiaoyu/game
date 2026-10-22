using System.Diagnostics.Metrics;
using DarkTide.Rules;
using Microsoft.Extensions.Logging;

namespace DarkTide.Room.Tests;

public class RoomObservabilityTests
{
    private static readonly Guid Alice = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("00000000-0000-0000-0000-000000000002");

    // 断言日志与指标之前必须先关闭执行器：请求的 Task 通过 RunContinuationsAsynchronously 完成，
    // 调用方的后续代码可能先于消费循环的记账代码运行。DisposeAsync 会等消费循环彻底结束。
    private static async Task<RecordingLogger> RunAndStopAsync(Func<RoomExecutor, Task> 工作)
    {
        var logger = new RecordingLogger();
        var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), logger: logger);
        try
        {
            await 工作(executor);
        }
        finally
        {
            await executor.DisposeAsync();
        }
        return logger;
    }

    [Fact]
    public async Task SuccessfulCommandIsLoggedAtDebugWithItsOutcome()
    {
        var logger = await RunAndStopAsync(async executor =>
            await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 3)));

        var applied = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Debug);
        Assert.Contains("applied", applied.Message);
        Assert.Contains("TakeSeatCommand", applied.Message);
        // 正常关闭也要留痕，便于与故障停止区分。
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("Faulted: False"));
    }

    [Fact]
    public async Task RuleRejectionIsLoggedWithItsReasonCode()
    {
        var logger = await RunAndStopAsync(async executor =>
        {
            await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 3));
            await Assert.ThrowsAsync<RuleRejectedException>(
                () => executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Bob, 3)));
        });

        var rejected = Assert.Single(logger.Entries, entry => entry.Message.Contains("rejected"));
        Assert.Equal(LogLevel.Debug, rejected.Level);
        // 记原因代码而不是只记文字：告警规则要按代码聚合。
        Assert.Contains(nameof(RejectionReason.SeatTaken), rejected.Message);
    }

    [Fact]
    public async Task CapacityPressureIsLoggedAsWarningWithQueueDepth()
    {
        // 暂缓消费，才能构造出“队列已满”的确定状态。
        var startSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new RecordingLogger();
        var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(commands: 1), startSignal.Task, logger: logger);
        try
        {
            var accepted = executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 0));
            await Assert.ThrowsAsync<RoomBusyException>(
                () => executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Bob, 1)));
            startSignal.SetResult(true);
            await accepted;
        }
        finally
        {
            startSignal.TrySetResult(true);
            await executor.DisposeAsync();
        }

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("busy", warning.Message);
        // 队列深度必须写进日志，否则事后无法判断当时积压到什么程度。
        Assert.Contains("Waiting commands 1/1", warning.Message);
    }

    [Fact]
    public async Task UnexpectedFaultIsLoggedAsError()
    {
        var logger = new RecordingLogger();
        var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), logger: logger);
        try
        {
            await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 0));
            await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Bob, 1));

            // 张数正确、内容重复的牌序：可信流程的缺陷，必须按故障处理。
            var duplicateDeck = Deck.CreateStandard().ToArray();
            duplicateDeck[26] = duplicateDeck[0];
            await Assert.ThrowsAsync<ArgumentException>(
                () => executor.ExecuteSystemAsync(new DealCommand(Guid.NewGuid(), duplicateDeck, new[] { 0, 1 })));
        }
        finally
        {
            await executor.DisposeAsync();
        }

        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("stopped after an unexpected error", error.Message);
        Assert.NotNull(executor.FaultReason);
        // 故障与正常关闭要能从日志里区分开，上层据此决定摘除还是重建。
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("Faulted: True"));
    }

    [Fact]
    public async Task LogsNeverContainCardData()
    {
        var deck = new List<Card>(Deck.CreateStandard());
        var logger = await RunAndStopAsync(async executor =>
        {
            await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 0));
            await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Bob, 1));
            await executor.ExecuteSystemAsync(new DealCommand(Guid.NewGuid(), deck, new[] { 0, 1 }));
        });

        // 发出去的牌一张也不能出现在日志里。
        var text = logger.AllText;
        Assert.All(deck.Take(4), card => Assert.DoesNotContain(card.ToString(), text, StringComparison.Ordinal));
        Assert.NotEmpty(logger.Entries);

        // 视图对象的 ToString 也不能带底牌，否则一旦被写进日志就等于泄漏。
        var view = TestHands.Deal(5, Deck.CreateStandard(), new[] { 0, 1, 2, 3, 4 }).CreatePlayerView(0);
        Assert.DoesNotContain(view.HoleCards[0].ToString(), view.ToString(), StringComparison.Ordinal);
        Assert.Contains("cards", view.ToString());
    }

    [Fact]
    public async Task DealCommandToStringDoesNotExposeTheDeck()
    {
        var deck = Deck.CreateStandard();
        var command = new DealCommand(Guid.NewGuid(), deck, new[] { 0, 1 });

        var text = command.ToString();
        Assert.DoesNotContain(deck[0].ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain(deck[10].ToString(), text, StringComparison.Ordinal);
        Assert.Contains("2 seats", text);
    }

    [Fact]
    public async Task MetricsCountAppliedAndRejectedRequests()
    {
        var gate = new object();
        var requests = new List<(string Outcome, string? Reason)>();
        var durations = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "DarkTide.Room")
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            if (instrument.Name != "darktide.room.requests")
            {
                return;
            }

            string? outcome = null;
            string? reason = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome")
                {
                    outcome = tag.Value?.ToString();
                }
                if (tag.Key == "reason")
                {
                    reason = tag.Value?.ToString();
                }
            }
            lock (gate)
            {
                requests.Add((outcome!, reason));
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            if (instrument.Name == "darktide.room.request.duration")
            {
                lock (gate)
                {
                    durations.Add(value);
                }
            }
        });
        listener.Start();

        await RunAndStopAsync(async executor =>
        {
            await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 0));
            await Assert.ThrowsAsync<RuleRejectedException>(
                () => executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Bob, 0)));
        });

        lock (gate)
        {
            Assert.Equal(2, requests.Count);
            Assert.Contains(requests, item => item.Outcome == "applied");
            Assert.Contains(requests, item => item.Outcome == "rejected" && item.Reason == nameof(RejectionReason.SeatTaken));
            Assert.Equal(2, durations.Count);
            Assert.All(durations, value => Assert.True(value >= 0));
        }
    }

    [Fact]
    public async Task MetricsReportRejectedSubmissionsWithTheirReason()
    {
        var gate = new object();
        var reasons = new List<string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Name == "darktide.room.submissions.rejected")
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            lock (gate)
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "reason")
                    {
                        reasons.Add(tag.Value?.ToString());
                    }
                }
            }
        });
        listener.Start();

        var startSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(commands: 1), startSignal.Task);
        try
        {
            var accepted = executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 0));
            await Assert.ThrowsAsync<RoomBusyException>(
                () => executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Bob, 1)));
            startSignal.SetResult(true);
            await accepted;
        }
        finally
        {
            startSignal.TrySetResult(true);
            await executor.DisposeAsync();
        }

        lock (gate)
        {
            Assert.Equal(new[] { "busy" }, reasons);
        }
    }

    [Fact]
    public async Task QueueDepthIsReadableForHealthChecks()
    {
        var startSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new RoomExecutor(new Room(Guid.NewGuid(), 8), TestOptions.With(commands: 4), startSignal.Task);
        try
        {
            Assert.Equal(0, executor.WaitingCommandCount);
            Assert.Equal(0, executor.WaitingQueryCount);
            Assert.False(executor.IsStopped);

            var pending = executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Alice, 0));
            Assert.Equal(1, executor.WaitingCommandCount);

            startSignal.SetResult(true);
            await pending;

            // 出队即归还名额。这里不断言立刻回到 0：归还发生在消费循环里，与调用方的后续代码存在竞争。
            // 真正需要保证的是它最终会回到 0，由下面的关闭流程确认。
        }
        finally
        {
            startSignal.TrySetResult(true);
            await executor.DisposeAsync();
        }

        Assert.True(executor.IsStopped);
        Assert.Equal(0, executor.WaitingCommandCount);
        Assert.Null(executor.FaultReason);
    }
}
