using DarkTide.Rules;
using Microsoft.Extensions.Time.Testing;

namespace DarkTide.Room.Tests;

/// <summary>执行器发布的快照：每次变化一份、只给本人看自己的底牌、变化与停止都会唤醒等待者。</summary>
public class PublishedRoomStateTests
{
    private static readonly Guid[] Players = Enumerable.Range(1, 4).Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:D12}")).ToArray();
    private static readonly RoomOptions NoClock = new() { TurnTimeout = TimeSpan.Zero, NextHandDelay = TimeSpan.Zero };

    [Fact]
    public async Task EachStateChangePublishesANewSnapshotAndWakesWaiters()
    {
        await using var executor = new RoomExecutor(Guid.NewGuid(), 4, NoClock);
        var first = executor.Published;
        Assert.Equal(0, first.StateVersion);
        Assert.False(first.Superseded.IsCompleted);

        await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Players[0], 0));

        await first.Superseded.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, executor.Published.StateVersion);
        Assert.False(executor.Published.Superseded.IsCompleted);
    }

    [Fact]
    public async Task RejectedCommandsAndQueriesDoNotPublish()
    {
        await using var executor = new RoomExecutor(Guid.NewGuid(), 4, NoClock);
        await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Players[0], 0));
        var current = executor.Published;

        await Assert.ThrowsAsync<RuleRejectedException>(() => executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Players[1], 0)));
        await executor.CreateOverviewAsync();

        Assert.Same(current, executor.Published);
        Assert.False(current.Superseded.IsCompleted);
    }

    [Fact]
    public async Task EachViewerSeesOnlyTheirOwnHoleCards()
    {
        await using var executor = new RoomExecutor(Guid.NewGuid(), 4, NoClock);
        for (int seat = 0; seat < 3; seat++)
        {
            await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Players[seat], seat));
        }
        await executor.StartMatchAsync();
        var published = executor.Published;

        var views = Enumerable.Range(0, 3).Select(seat => published.ViewFor(Players[seat])).ToArray();
        for (int seat = 0; seat < 3; seat++)
        {
            Assert.Equal(seat, views[seat].Seat);
            Assert.Equal(2, views[seat].HoleCards!.Count);
            // 与房间自己给本人的视图一致。
            Assert.Equal((await executor.CreatePlayerViewAsync(Players[seat])).HoleCards, views[seat].HoleCards);
        }
        Assert.Empty(views[0].HoleCards!.Intersect(views[1].HoleCards!));

        // 旁观者（没入座的身份）只有公开概况。
        var spectator = published.ViewFor(Guid.NewGuid());
        Assert.Null(spectator.Seat);
        Assert.Null(spectator.HoleCards);
        // 入座但空座的第 4 位没有人：用 4 号玩家看也是旁观者。
        Assert.Null(published.ViewFor(Players[3]).HoleCards);
    }

    [Fact]
    public async Task DeadlineChangesArePublishedToo()
    {
        var time = new FakeTimeProvider();
        await using var executor = new RoomExecutor(Guid.NewGuid(), 2,
            new RoomOptions { TurnTimeout = TimeSpan.FromSeconds(30), NextHandDelay = TimeSpan.FromSeconds(5) }, time: time);
        await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Players[0], 0));
        await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Players[1], 1));
        await executor.StartMatchAsync();

        Assert.Equal(time.GetUtcNow() + TimeSpan.FromSeconds(30), executor.Published.Deadline);
    }

    [Fact]
    public async Task StoppingTheRoomWakesWaiters()
    {
        var executor = new RoomExecutor(Guid.NewGuid(), 4, NoClock);
        var current = executor.Published;

        await executor.StopAsync();

        await current.Superseded.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(executor.IsStopped);
    }

    [Fact]
    public void ToStringNeverContainsCards()
    {
        var room = new Room(Guid.NewGuid(), 2);
        room.Execute(new TakeSeatCommand(Guid.NewGuid(), Players[0], 0));
        room.Execute(new TakeSeatCommand(Guid.NewGuid(), Players[1], 1));
        room.Execute(new DealCommand(Guid.NewGuid(), Deck.CreateStandard(), [0, 1]));
        var published = PublishedRoomState.Capture(room, null);
        var view = published.ViewFor(Players[0]);

        foreach (var text in new[] { published.ToString(), view.ToString() })
        {
            foreach (var card in view.HoleCards!)
            {
                Assert.DoesNotContain(card.ToString(), text);
            }
        }
    }
}
