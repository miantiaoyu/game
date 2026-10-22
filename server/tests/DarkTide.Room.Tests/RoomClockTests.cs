using DarkTide.Rules;
using Microsoft.Extensions.Time.Testing;

namespace DarkTide.Room.Tests;

/// <summary>执行器的计时：开始整场、行动超时、自动发下一局、玩家抢在超时前行动、恢复后重新计时。</summary>
public class RoomClockTests
{
    private static readonly Guid[] Players = Enumerable.Range(1, 8).Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:D12}")).ToArray();
    private static readonly TimeSpan Turn = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(5);
    private static readonly RoomOptions Clocked = new()
    {
        TurnTimeout = Turn, NextHandDelay = Gap,
        PlayerCommandsPerSecond = 10_000, PlayerCommandBurst = 10_000, PlayerQueriesPerSecond = 10_000, PlayerQueryBurst = 10_000,
    };

    private static async Task<RoomExecutor> SeatedAsync(int seats, FakeTimeProvider time, IRoomJournal? journal = null, Guid? roomId = null)
    {
        var executor = journal is null
            ? new RoomExecutor(Guid.NewGuid(), seats, Clocked, time: time)
            : await RoomExecutor.CreatePersistentAsync(roomId!.Value, seats, journal, Clocked, time: time);
        for (int seat = 0; seat < seats; seat++)
        {
            await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Players[seat], seat));
        }
        return executor;
    }

    /// <summary>推进假时钟，再等房间把计时器放进队列的请求处理完（查询排在它后面，查询返回即已处理）。</summary>
    private static async Task<RoomOverview> AdvanceAsync(RoomExecutor executor, FakeTimeProvider time, TimeSpan by)
    {
        time.Advance(by);
        return await executor.CreateOverviewAsync();
    }

    [Fact]
    public async Task StartMatchSeatsEveryoneClockwiseFromARandomStart()
    {
        var starts = new HashSet<int>();
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var time = new FakeTimeProvider();
            await using var executor = await SeatedAsync(4, time);
            await executor.StartMatchAsync();

            var overview = await executor.CreateOverviewAsync();
            Assert.Equal(RoomStage.CardsDealt, overview.Stage);
            var order = overview.ParticipantOrder;
            Assert.Equal(4, order.Count);
            // 从起始位开始按座位号递增、绕回 0。
            Assert.Equal(Enumerable.Range(0, 4).Select(offset => (order[0] + offset) % 4), order);
            starts.Add(order[0]);
        }
        // 起始位是随机的：40 次里应当出现不止一个。
        Assert.True(starts.Count > 1);
    }

    [Fact]
    public async Task StartMatchNeedsTwoSeatedPlayers()
    {
        var time = new FakeTimeProvider();
        await using var executor = new RoomExecutor(Guid.NewGuid(), 4, Clocked, time: time);
        await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Players[0], 2));

        var error = await Assert.ThrowsAsync<RuleRejectedException>(() => executor.StartMatchAsync());
        Assert.Equal(RejectionReason.NotEnoughPlayers, error.Reason);
    }

    [Fact]
    public async Task TimeoutChecksWhenPossibleAndFoldsWhenFacingABet()
    {
        var time = new FakeTimeProvider();
        await using var executor = await SeatedAsync(3, time);
        await executor.StartMatchAsync();
        var overview = await executor.CreateOverviewAsync();
        Assert.Equal(time.GetUtcNow() + Turn, overview.Deadline);

        // 第一位没人下注：超时就过牌，轮到第二位。
        int first = overview.Hand!.ActingSeat;
        overview = await AdvanceAsync(executor, time, Turn);
        Assert.False(overview.Hand!.Folded[first]);
        int second = overview.Hand.ActingSeat;
        Assert.NotEqual(first, second);

        // 第二位开注，第三位超时：面对下注，弃牌。
        await executor.ExecuteAsync(new BetCommand(Guid.NewGuid(), Players[second], BettingAction.Raise, 2));
        int third = (await executor.CreateOverviewAsync()).Hand!.ActingSeat;
        overview = await AdvanceAsync(executor, time, Turn);
        Assert.True(overview.Hand!.Folded[third]);
    }

    [Fact]
    public async Task ActingBeforeTheDeadlineRestartsTheClockForTheNextPlayer()
    {
        var time = new FakeTimeProvider();
        await using var executor = await SeatedAsync(3, time);
        await executor.StartMatchAsync();
        int first = (await executor.CreateOverviewAsync()).Hand!.ActingSeat;

        // 差 1 秒到期时行动：新的期限从现在算起，下一位有完整的 30 秒。
        time.Advance(Turn - TimeSpan.FromSeconds(1));
        await executor.ExecuteAsync(new BetCommand(Guid.NewGuid(), Players[first], BettingAction.Check));
        var overview = await executor.CreateOverviewAsync();
        int second = overview.Hand!.ActingSeat;
        Assert.Equal(time.GetUtcNow() + Turn, overview.Deadline);

        // 旧期限到了（再过 1 秒）：什么都不发生，第二位仍在思考。
        overview = await AdvanceAsync(executor, time, TimeSpan.FromSeconds(1));
        Assert.Equal(second, overview.Hand!.ActingSeat);
        Assert.False(overview.Hand.Folded[second]);
    }

    [Fact]
    public async Task UnattendedTableFinishesTheWholeMatchByItself()
    {
        // 没有任何人操作：每个行动都超时，每局结算后自动发下一局，直到整场结束。
        var time = new FakeTimeProvider();
        await using var executor = await SeatedAsync(3, time);
        await executor.StartMatchAsync();

        var overview = await executor.CreateOverviewAsync();
        int steps = 0;
        while (overview.Stage != RoomStage.MatchFinished)
        {
            Assert.True(++steps < 2000, "无人操作的牌桌没有在合理步数内结束。");
            overview = await AdvanceAsync(executor, time, overview.Stage == RoomStage.HandSettled ? Gap : Turn);
        }

        Assert.Equal(Match.StandardHandCount, overview.HandsPlayed);
        Assert.Equal(300, overview.MatchChips.Sum());
        Assert.Null(overview.Deadline);
    }

    [Fact]
    public async Task StartMarkerMovesClockwiseToTheNextPlayerWithChips()
    {
        var time = new FakeTimeProvider();
        await using var executor = await SeatedAsync(4, time);
        await executor.StartMatchAsync();
        var overview = await executor.CreateOverviewAsync();
        var starts = new List<int> { overview.ParticipantOrder[0] };
        for (int hand = 0; hand < 5 && overview.Stage != RoomStage.MatchFinished; hand++)
        {
            do
            {
                overview = await AdvanceAsync(executor, time, overview.Stage == RoomStage.HandSettled ? Gap : Turn);
            }
            while (overview.Stage == RoomStage.CardsDealt);
            if (overview.Stage == RoomStage.MatchFinished)
            {
                break;
            }
            overview = await AdvanceAsync(executor, time, Gap);
            starts.Add(overview.ParticipantOrder[0]);
        }
        // 无人操作时没人输光（每局只交底注、没人下注），起始位每局顺时针移一位。
        for (int i = 1; i < starts.Count; i++)
        {
            Assert.Equal((starts[i - 1] + 1) % 4, starts[i]);
        }
    }

    [Fact]
    public async Task RerouteWindowTimesOutAsAPass()
    {
        var time = new FakeTimeProvider();
        await using var executor = await SeatedAsync(2, time);
        await executor.StartMatchAsync();
        // 一注、二注都超时过牌，进入改航窗口。
        var overview = await executor.CreateOverviewAsync();
        while (overview.Hand!.RerouteActingSeat < 0)
        {
            overview = await AdvanceAsync(executor, time, Turn);
        }
        int seat = overview.Hand.RerouteActingSeat;
        var feesBefore = overview.Hand.RerouteFees.Sum();

        overview = await AdvanceAsync(executor, time, Turn);

        Assert.NotEqual(seat, overview.Hand!.RerouteActingSeat);
        Assert.Equal(feesBefore, overview.Hand.RerouteFees.Sum());
        Assert.Empty(overview.Hand.DiscardedCards);
    }

    [Fact]
    public async Task TimeoutsAndAutomaticDealsAreJournaledAndReplayExactly()
    {
        var time = new FakeTimeProvider();
        var journal = new InMemoryRoomJournal();
        var roomId = Guid.NewGuid();
        string before;
        int handsBefore;
        await using (var executor = await SeatedAsync(3, time, journal, roomId))
        {
            await executor.StartMatchAsync();
            var overview = await executor.CreateOverviewAsync();
            // 打到第 3 局中间：期间所有超时与自动发牌都写进了日志。
            while (overview.HandsPlayed < 3 || overview.Stage != RoomStage.CardsDealt)
            {
                overview = await AdvanceAsync(executor, time, overview.Stage == RoomStage.HandSettled ? Gap : Turn);
            }
            overview = await AdvanceAsync(executor, time, Turn);
            handsBefore = overview.HandsPlayed;
            before = System.Text.Json.JsonSerializer.Serialize(overview with { Deadline = null });
        }

        var later = new FakeTimeProvider(time.GetUtcNow() + TimeSpan.FromHours(1));
        await using var restored = await RoomExecutor.RestoreAsync(roomId, journal, Clocked, time: later);

        var after = await restored.CreateOverviewAsync();
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(after with { Deadline = null }));
        Assert.Equal(handsBefore, after.HandsPlayed);
        // 停机一小时也不会让人立刻超时：计时从恢复时重新开始。
        Assert.Equal(later.GetUtcNow() + Turn, after.Deadline);
    }

    [Fact]
    public async Task ZeroDurationsTurnTheClockOff()
    {
        var time = new FakeTimeProvider();
        await using var executor = new RoomExecutor(Guid.NewGuid(), 2,
            new RoomOptions { TurnTimeout = TimeSpan.Zero, NextHandDelay = TimeSpan.Zero }, time: time);
        await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Players[0], 0));
        await executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Players[1], 1));
        await executor.StartMatchAsync();
        long version = (await executor.CreateOverviewAsync()).StateVersion;

        var overview = await AdvanceAsync(executor, time, TimeSpan.FromHours(1));
        Assert.Equal(version, overview.StateVersion);
        Assert.Null(overview.Deadline);
    }

    [Fact]
    public async Task ClockFiringEarlyReschedulesInsteadOfStalling()
    {
        // 系统计时器可能比期限早一点触发。早触发时不能把计时丢掉：要按剩余时间再等，期限一到照样超时。
        var time = new FakeTimeProvider();
        await using var executor = await SeatedAsync(2, time);
        await executor.StartMatchAsync();
        int first = (await executor.CreateOverviewAsync()).Hand!.ActingSeat;

        // 模拟“早触发”：在期限前 1 毫秒手动触发，再推进到期限。
        time.Advance(Turn - TimeSpan.FromMilliseconds(1));
        var overview = await executor.CreateOverviewAsync();
        Assert.Equal(first, overview.Hand!.ActingSeat);
        overview = await AdvanceAsync(executor, time, TimeSpan.FromMilliseconds(1));
        Assert.NotEqual(first, overview.Hand!.ActingSeat);
    }

    [Fact]
    public async Task TimeoutStillGetsQueuedWhenPlayersFillTheCommandQueue()
    {
        // 房间已在计时（轮到某人）。卡住消费循环，把命令名额全部占满，再让期限到期：
        // 计时请求有预留位置，必须照样进队；放开循环后，它排在那些命令之后执行，超时生效。
        var time = new FakeTimeProvider();
        await using var executor = await SeatedAsync(3, time);
        await executor.StartMatchAsync();
        int acting = (await executor.CreateOverviewAsync()).Hand!.ActingSeat;

        var release = new ManualResetEventSlim();
        var stuck = executor.RunInLoopForTestAsync(_ => release.Wait(TimeSpan.FromSeconds(10)));
        // 名额 56 条：用 56 个不同玩家各发一条（不在座，会被规则拒绝，但都占名额）。
        var flood = Enumerable.Range(0, RoomOptions.DefaultCommandQueueCapacity)
            .Select(_ => executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Guid.NewGuid(), 0)))
            .ToArray();
        var overflow = await Assert.ThrowsAsync<RoomBusyException>(() => executor.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), Guid.NewGuid(), 0)));
        Assert.Equal(BusyReason.QueueFull, overflow.Reason);

        time.Advance(Turn);
        release.Set();
        await stuck;
        foreach (var request in flood)
        {
            await Assert.ThrowsAsync<RuleRejectedException>(() => request);
        }
        var overview = await executor.CreateOverviewAsync();
        Assert.NotEqual(acting, overview.Hand!.ActingSeat);
    }

    [Fact]
    public async Task SystemCommandsCannotBeSubmittedThroughThePublicEntryPoint()
    {
        var time = new FakeTimeProvider();
        await using var executor = await SeatedAsync(2, time);

        // 牌序由客户端指定、替别人超时，都必须在入口处被挡住，而不是交给规则判断。
        Assert.Throws<ArgumentException>(() => { _ = executor.ExecuteAsync(new DealCommand(Guid.NewGuid(), Deck.CreateStandard(), [0, 1])); });
        Assert.Throws<ArgumentException>(() => { _ = executor.ExecuteAsync(new TimeoutCommand(Guid.NewGuid(), 2)); });
        Assert.Equal(RoomStage.WaitingForSeats, (await executor.CreateOverviewAsync()).Stage);
    }

    [Fact]
    public async Task MatchFinishedCompletesWhenTheLastHandSettles()
    {
        var time = new FakeTimeProvider();
        await using var executor = await SeatedAsync(2, time);
        await executor.StartMatchAsync();
        Assert.False(executor.MatchFinished.IsCompleted);

        var overview = await executor.CreateOverviewAsync();
        while (overview.Stage != RoomStage.MatchFinished)
        {
            overview = await AdvanceAsync(executor, time, overview.Stage == RoomStage.HandSettled ? Gap : Turn);
        }
        await executor.MatchFinished.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ClockOptionsAreValidated()
    {
        Assert.NotEmpty(new RoomOptions { TurnTimeout = TimeSpan.FromSeconds(-1) }.Validate());
        Assert.NotEmpty(new RoomOptions { NextHandDelay = TimeSpan.FromHours(1) }.Validate());
        Assert.Empty(new RoomOptions { TurnTimeout = TimeSpan.Zero, NextHandDelay = TimeSpan.Zero }.Validate());
        Assert.NotEmpty(new RoomOptions { FinishedRoomRetention = TimeSpan.FromDays(2) }.Validate());
    }
}
