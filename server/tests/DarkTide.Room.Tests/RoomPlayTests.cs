using System.Text.Json;
using DarkTide.Rules;

namespace DarkTide.Room.Tests;

/// <summary>通过命令打完整局与整场：下注、改航、自动结算、连续发牌、整场结束，以及日志重放。</summary>
public class RoomPlayTests
{
    private static readonly Guid[] Players = Enumerable.Range(1, 8).Select(i => Guid.Parse($"00000000-0000-0000-0000-{i:D12}")).ToArray();

    private static Room SeatedRoom(int seats, out Guid[] seated)
    {
        var room = new Room(Guid.NewGuid(), seats);
        seated = Players.Take(seats).ToArray();
        for (int seat = 0; seat < seats; seat++)
        {
            room.Execute(new TakeSeatCommand(Guid.NewGuid(), seated[seat], seat));
        }
        return room;
    }

    /// <summary>可信流程：下一局名单＝仍有筹码的座位，从起始位开始按座位号绕一圈。</summary>
    private static int[] NextLineup(RoomOverview overview, int startSeat)
    {
        int seats = overview.SeatOccupied.Count;
        return Enumerable.Range(0, seats).Select(offset => (startSeat + offset) % seats)
            .Where(seat => overview.HandsPlayed == 0 ? overview.SeatOccupied[seat] : overview.MatchChips[seat] > 0)
            .ToArray();
    }

    private static Card[] Shuffled(Random random) => Deck.CreateStandard().OrderBy(_ => random.Next()).ToArray();

    /// <summary>只看公开概况，在合法动作里随机选一个，组成玩家命令。</summary>
    private static RoomCommand RandomMove(RoomOverview overview, Guid[] players, Random random)
    {
        var hand = overview.Hand!;
        if (hand.RerouteActingSeat >= 0)
        {
            int seat = hand.RerouteActingSeat;
            // 付不起报价时只能放弃（付不起还要换会被规则拒绝，那是另一个测试的事）。
            int fee = hand.RerouteQuotes.Single(quote => quote.Seat == seat).Fee;
            bool affordable = hand.Stacks[seat] >= fee;
            return new RerouteCommand(Guid.NewGuid(), players[seat], affordable && random.Next(2) == 0, overview.StateVersion);
        }
        var legal = hand.LegalActions!.Value;
        var options = new List<(BettingAction Action, int Target)>();
        if (legal.CanCheck) options.Add((BettingAction.Check, 0));
        if (legal.CanCall) options.Add((BettingAction.Call, 0));
        if (legal.CanFold) options.Add((BettingAction.Fold, 0));
        if (legal.CanGoAllIn) options.Add((BettingAction.AllIn, 0));
        if (legal.CanRaise) options.Add((BettingAction.Raise, random.Next(legal.MinRaiseTarget, legal.MaxRaiseTarget + 1)));
        var (action, target) = options[random.Next(options.Count)];
        return new BetCommand(Guid.NewGuid(), players[hand.ActingSeat], action, target, overview.StateVersion);
    }

    /// <summary>打完一整场，返回执行过的全部命令。</summary>
    private static List<RoomCommand> PlayWholeMatch(Room room, Guid[] players, Random random)
    {
        var commands = new List<RoomCommand>();
        int start = random.Next(room.SeatCount);
        int guard = 0;
        while (room.Stage != RoomStage.MatchFinished)
        {
            Assert.True(++guard < 5000, "整场没有在合理步数内结束。");
            var overview = room.CreateOverview();
            RoomCommand command;
            if (overview.Stage is RoomStage.WaitingForSeats or RoomStage.HandSettled)
            {
                var lineup = NextLineup(overview, start);
                start = (lineup[0] + 1) % room.SeatCount;
                command = new DealCommand(Guid.NewGuid(), Shuffled(random), lineup, overview.StateVersion);
            }
            else
            {
                command = RandomMove(overview, players, random);
            }
            room.Execute(command);
            commands.Add(command);
            AssertConserved(room.CreateOverview());
        }
        return commands;
    }

    /// <summary>一局进行中：余额＋累计投入＋本轮投入＋改航费＝整场筹码（本局开始时）；结算后整场筹码就是结算结果。</summary>
    private static void AssertConserved(RoomOverview overview)
    {
        var hand = overview.Hand;
        if (hand is null)
        {
            return;
        }
        if (hand.Settlement is null)
        {
            int inHand = hand.Stacks.Sum() + hand.Contributions.Sum() + hand.CommittedThisRound.Sum() + hand.RerouteFees.Sum();
            Assert.Equal(overview.MatchChips.Sum(), inHand);
        }
        else
        {
            Assert.Equal(hand.Settlement.FinalStacks, overview.MatchChips);
        }
    }

    [Fact]
    public void WholeMatchesCanBePlayedThroughCommandsAlone()
    {
        var random = new Random(20261020);
        for (int game = 0; game < 60; game++)
        {
            int seats = 2 + game % 7;
            var room = SeatedRoom(seats, out var players);

            PlayWholeMatch(room, players, random);

            var overview = room.CreateOverview();
            Assert.Equal(RoomStage.MatchFinished, overview.Stage);
            Assert.Equal(seats * Match.StandardInitialChips, overview.MatchChips.Sum());
            Assert.True(overview.HandsPlayed == Match.StandardHandCount || overview.MatchChips.Count(chips => chips > 0) == 1);
        }
    }

    [Fact]
    public void DealingStartsTheFirstBetAndAntesAreCollected()
    {
        var room = SeatedRoom(3, out _);
        room.Execute(new DealCommand(Guid.NewGuid(), Deck.CreateStandard(), [1, 2, 0]));

        var hand = room.CreateOverview().Hand!;
        Assert.Equal(HandStage.FirstBet, hand.Stage);
        Assert.Single(hand.CommunityCards);
        Assert.Equal(1, hand.ActingSeat);
        Assert.Equal(new[] { 1, 1, 1 }, hand.Contributions);
        Assert.NotNull(hand.LegalActions);
    }

    [Fact]
    public void OnlyTheActingPlayerMayBetAndRejectionsLeaveStateUnchanged()
    {
        var room = SeatedRoom(3, out var players);
        room.Execute(new DealCommand(Guid.NewGuid(), Deck.CreateStandard(), [0, 1, 2]));
        long version = room.StateVersion;

        AssertRejected(RejectionReason.NotYourTurn, () => room.Execute(new BetCommand(Guid.NewGuid(), players[1], BettingAction.Check)));
        // 没人下注时不能弃牌：规则拒绝。
        AssertRejected(RejectionReason.IllegalAction, () => room.Execute(new BetCommand(Guid.NewGuid(), players[0], BettingAction.Fold)));
        AssertRejected(RejectionReason.NotSeated, () => room.Execute(new BetCommand(Guid.NewGuid(), Guid.NewGuid(), BettingAction.Check)));
        AssertRejected(RejectionReason.StageNotAllowed, () => room.Execute(new RerouteCommand(Guid.NewGuid(), players[0], true)));
        AssertRejected(RejectionReason.StageNotAllowed, () => room.Execute(new DealCommand(Guid.NewGuid(), Deck.CreateStandard(), [0, 1, 2])));
        Assert.Equal(version, room.StateVersion);

        room.Execute(new BetCommand(Guid.NewGuid(), players[0], BettingAction.Check));
        Assert.Equal(version + 1, room.StateVersion);
    }

    [Fact]
    public void LastFoldSettlesTheHandAndTheNextDealContinuesTheMatch()
    {
        var room = SeatedRoom(2, out var players);
        room.Execute(new DealCommand(Guid.NewGuid(), Deck.CreateStandard(), [0, 1]));
        room.Execute(new BetCommand(Guid.NewGuid(), players[0], BettingAction.Raise, 2));
        room.Execute(new BetCommand(Guid.NewGuid(), players[1], BettingAction.Fold));

        var overview = room.CreateOverview();
        Assert.Equal(RoomStage.HandSettled, overview.Stage);
        Assert.Equal(new[] { 101, 99 }, overview.MatchChips);
        // 没有摊牌：谁的牌都不公开。
        Assert.False(overview.Hand!.Settlement!.Showdown);
        Assert.Empty(overview.Hand.Settlement.ShownHoleCards);
        // 结算后仍能看自己刚才的底牌。
        Assert.Equal(2, room.CreatePlayerView(players[1]).HoleCards.Count);

        room.Execute(new DealCommand(Guid.NewGuid(), Deck.CreateStandard().Reverse().ToArray(), [1, 0]));
        overview = room.CreateOverview();
        Assert.Equal(RoomStage.CardsDealt, overview.Stage);
        Assert.Equal(2, overview.HandsPlayed);
        Assert.Equal(new[] { 100, 98 }, overview.Hand!.Stacks);
    }

    [Fact]
    public void LineupMustBeExactlyTheSeatsWithChipsAfterTheFirstHand()
    {
        var room = SeatedRoom(3, out var players);
        // 首局只让座位 0、1 参赛：座位 2 入座但不参赛，整场筹码为 0。
        room.Execute(new DealCommand(Guid.NewGuid(), Deck.CreateStandard(), [0, 1]));
        room.Execute(new BetCommand(Guid.NewGuid(), players[0], BettingAction.Raise, 2));
        room.Execute(new BetCommand(Guid.NewGuid(), players[1], BettingAction.Fold));
        Assert.Equal(new[] { 101, 99, 0 }, room.CreateOverview().MatchChips);

        Assert.Throws<ArgumentException>(() => room.Execute(new DealCommand(Guid.NewGuid(), Deck.CreateStandard(), [0, 1, 2])));
        Assert.Throws<ArgumentException>(() => room.Execute(new DealCommand(Guid.NewGuid(), Deck.CreateStandard(), [0, 2])));
        Assert.Equal(RoomStage.HandSettled, room.Stage);
        room.Execute(new DealCommand(Guid.NewGuid(), Deck.CreateStandard(), [1, 0]));
    }

    [Fact]
    public void ShowdownRevealsOnlyTheHandsThatReachedIt()
    {
        var room = SeatedRoom(3, out var players);
        room.Execute(new DealCommand(Guid.NewGuid(), Deck.CreateStandard(), [0, 1, 2]));
        // 一注：座位 0 开注，1 跟，2 弃。之后全部过牌、放弃改航，到摊牌。
        room.Execute(new BetCommand(Guid.NewGuid(), players[0], BettingAction.Raise, 2));
        room.Execute(new BetCommand(Guid.NewGuid(), players[1], BettingAction.Call));
        room.Execute(new BetCommand(Guid.NewGuid(), players[2], BettingAction.Fold));
        while (room.Stage == RoomStage.CardsDealt)
        {
            var hand = room.CreateOverview().Hand!;
            room.Execute(hand.RerouteActingSeat >= 0
                ? new RerouteCommand(Guid.NewGuid(), players[hand.RerouteActingSeat], false)
                : new BetCommand(Guid.NewGuid(), players[hand.ActingSeat], BettingAction.Check));
        }

        var settlement = room.CreateOverview().Hand!.Settlement!;
        Assert.True(settlement.Showdown);
        Assert.Equal(new[] { 0, 1 }, settlement.ShownHoleCards.Keys.Order());
        Assert.Equal(room.CreatePlayerView(players[0]).HoleCards, settlement.ShownHoleCards[0]);
    }

    [Fact]
    public void OverviewNeverContainsHoleCardsBeforeShowdown()
    {
        var random = new Random(9);
        var room = SeatedRoom(4, out var players);
        room.Execute(new DealCommand(Guid.NewGuid(), Shuffled(random), [0, 1, 2, 3]));
        var allHoles = players.Take(4).SelectMany(player => room.CreatePlayerView(player).HoleCards).ToHashSet();

        while (room.Stage == RoomStage.CardsDealt)
        {
            var overview = room.CreateOverview();
            // 公共牌和弃置牌里不会出现底牌；概况的其他部分不含牌；没结算就没有公开的底牌。
            Assert.DoesNotContain(overview.Hand!.CommunityCards, allHoles.Contains);
            Assert.DoesNotContain(overview.Hand.DiscardedCards, allHoles.Contains);
            Assert.Null(overview.Hand.Settlement);
            room.Execute(RandomMove(overview, players, random));
        }
    }

    [Fact]
    public void NewCommandsRoundTripThroughTheCodec()
    {
        RoomCommand[] commands =
        [
            new BetCommand(Guid.NewGuid(), Players[0], BettingAction.Raise, 12, 7),
            new BetCommand(Guid.NewGuid(), Players[1], BettingAction.Fold),
            new BetCommand(Guid.NewGuid(), Players[2], BettingAction.AllIn, expectedVersion: 3),
            new RerouteCommand(Guid.NewGuid(), Players[3], true, 9),
            new RerouteCommand(Guid.NewGuid(), Players[4], false),
        ];
        foreach (var command in commands)
        {
            var (kind, payload) = CommandCodec.Encode(command);
            Assert.Equal(command, CommandCodec.Decode(kind, payload));
        }
    }

    [Fact]
    public void MalformedBetCommandsAreRejectedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BetCommand(Guid.NewGuid(), Players[0], (BettingAction)99));
        Assert.Throws<ArgumentException>(() => new BetCommand(Guid.NewGuid(), Players[0], BettingAction.Call, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BetCommand(Guid.NewGuid(), Players[0], BettingAction.Raise, 0));
        Assert.Throws<ArgumentException>(() => new RerouteCommand(Guid.NewGuid(), Guid.Empty, true));
    }

    // 测试一口气发几百条命令；正式的每玩家限流会把它当成刷屏。
    private static readonly RoomOptions Unthrottled = new() { PlayerCommandsPerSecond = 10_000, PlayerCommandBurst = 10_000, PlayerQueriesPerSecond = 10_000, PlayerQueryBurst = 10_000 };

    [Fact]
    public async Task RestoringFromTheJournalMidHandReproducesTheExactState()
    {
        var random = new Random(77);
        for (int game = 0; game < 8; game++)
        {
            var journal = new InMemoryRoomJournal();
            var roomId = Guid.NewGuid();
            int seats = 3 + game % 4;
            var reference = SeatedRoom(seats, out var players);
            var commands = PlayWholeMatch(reference, players, random);

            // 在不同位置“断电”：执行器只执行到第 cut 条命令，然后从日志恢复，比较概况。
            int cut = random.Next(1, commands.Count);
            string expected;
            await using (var original = await RoomExecutor.CreatePersistentAsync(roomId, seats, journal, Unthrottled))
            {
                for (int seat = 0; seat < seats; seat++)
                {
                    await original.ExecuteAsync(new TakeSeatCommand(Guid.NewGuid(), players[seat], seat));
                }
                // 重新编版本：参考房间的版本号从入座开始算，这里一样，所以可以原样重发。
                foreach (var command in commands.Take(cut))
                {
                    await original.ExecuteAsync(command);
                }
                expected = JsonSerializer.Serialize(await original.CreateOverviewAsync());
            }

            await using var restored = await RoomExecutor.RestoreAsync(roomId, journal, Unthrottled);
            Assert.Equal(expected, JsonSerializer.Serialize(await restored.CreateOverviewAsync()));

            // 恢复后继续打完：剩下的命令照样被接受。
            foreach (var command in commands.Skip(cut))
            {
                await restored.ExecuteAsync(command);
            }
            Assert.Equal(RoomStage.MatchFinished, (await restored.CreateOverviewAsync()).Stage);
        }
    }

    private static void AssertRejected(RejectionReason reason, Action action) =>
        Assert.Equal(reason, Assert.Throws<RuleRejectedException>(action).Reason);
}
