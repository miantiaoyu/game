using DarkTide.Rules;

namespace DarkTide.Bots.Tests;

public class SelfPlayArenaTests
{
    private static readonly EquityTable Table = EquityTable.Standard;

    private static ThresholdBot Threshold(string name, double adjust = 0) =>
        new(name, ThresholdBotParameters.FairShareStart() with { AdjustDown = adjust, AdjustUp = adjust }, Table);

    private static IBot[] MixedTable(int seats, ulong seed) => Enumerable.Range(0, seats).Select<int, IBot>(seat => (seat % 4) switch
    {
        0 => Threshold($"门槛{seat}", 0.02),
        1 => new CallingStationBot(),
        2 => new ManiacBot(),
        _ => new RandomBot(seed + (ulong)seat),
    }).ToArray();

    [Fact]
    public void ManyMatchesFinishWithChipsConservedAndNoIllegalMoves()
    {
        // 各种人数、各种对手组合打完整场：规则层任何非法动作都会抛出，测试即失败。
        for (ulong seed = 1; seed <= 300; seed++)
        {
            int seats = 2 + (int)(seed % 7);
            var result = SelfPlayArena.PlayMatch(MixedTable(seats, seed), seed);

            Assert.Equal(seats * Match.StandardInitialChips, result.FinalChips.Sum());
            Assert.All(result.FinalChips, chips => Assert.True(chips >= 0));
            Assert.InRange(result.HandsPlayed, 1, Match.StandardHandCount);
            Assert.NotEmpty(result.Winners);
            // 提前结束只发生在只剩一人有筹码时。
            Assert.True(result.HandsPlayed == Match.StandardHandCount || result.FinalChips.Count(chips => chips > 0) == 1);
        }
    }

    [Fact]
    public void SameSeedReplaysTheSameMatch()
    {
        var first = SelfPlayArena.PlayMatch(MixedTable(5, 7), seed: 42);
        var second = SelfPlayArena.PlayMatch(MixedTable(5, 7), seed: 42);

        Assert.Equal(first.FinalChips, second.FinalChips);
        Assert.Equal(first.HandsPlayed, second.HandsPlayed);
    }

    [Fact]
    public void BotsOnlyEverSeeTheirOwnHoleCards()
    {
        var spies = Enumerable.Range(0, 5).Select(seat => new RecordingBot(seat)).ToArray();
        SelfPlayArena.PlayMatch(spies, seed: 3);

        foreach (var spy in spies)
        {
            Assert.NotEmpty(spy.Seen);
            Assert.All(spy.Seen, observation =>
            {
                Assert.Equal(spy.Seat, observation.Seat);
                Assert.Equal(2, observation.HoleCards.Count);
                // 看到的牌互不重复：自己的底牌、公共牌、死牌三者不会相交。
                var all = observation.HoleCards.Concat(observation.CommunityCards).Concat(observation.DeadCards).ToArray();
                Assert.Equal(all.Length, all.Distinct().Count());
            });
        }
    }

    [Fact]
    public void IllegalBotMoveIsNotSilentlyCorrected()
    {
        var bots = new IBot[] { new IllegalBot(), new CallingStationBot() };
        Assert.Throws<RuleRejectedException>(() => SelfPlayArena.PlayMatch(bots, seed: 1));
    }

    [Fact]
    public void FoldedHandIsJudgedOnlyAtARealShowdown()
    {
        // 必弃人机与两个永远加注者同桌：加注者从不弃牌，所以它弃牌的局都会摊牌，能得到判断；
        // 与一个永远加注者单挑：它一弃牌就只剩一人，没有摊牌，必须得到“无法判断”。
        var withShowdown = new FoldingBot();
        SelfPlayArena.PlayMatch(new IBot[] { withShowdown, new ManiacBot(), new ManiacBot() }, seed: 5);
        Assert.Contains(withShowdown.Outcomes, outcome => outcome.Folded && outcome.WouldHaveBeenBest is not null);
        Assert.All(withShowdown.Outcomes.Where(outcome => !outcome.Folded), outcome => Assert.Null(outcome.WouldHaveBeenBest));

        var noShowdown = new FoldingBot();
        SelfPlayArena.PlayMatch(new IBot[] { noShowdown, new ManiacBot() }, seed: 5);
        Assert.Contains(noShowdown.Outcomes, outcome => outcome.Folded);
        Assert.All(noShowdown.Outcomes.Where(outcome => outcome.Folded), outcome => Assert.Null(outcome.WouldHaveBeenBest));
    }

    [Fact]
    public void AdjustmentFollowsTheFourRulesAndIsBounded()
    {
        var bot = new ThresholdBot("调整", ThresholdBotParameters.FairShareStart() with { AdjustDown = 0.03, AdjustUp = 0.01, AdjustLimit = 0.05 }, Table);
        bot.StartMatch();

        bot.EndHand(new HandOutcome(Folded: true, WentToShowdown: false, WonAnyPot: false, WouldHaveBeenBest: true));
        Assert.Equal(-0.03, bot.Offset, 10);
        bot.EndHand(new HandOutcome(Folded: true, WentToShowdown: false, WonAnyPot: false, WouldHaveBeenBest: false));
        bot.EndHand(new HandOutcome(Folded: true, WentToShowdown: false, WonAnyPot: false, WouldHaveBeenBest: null));
        bot.EndHand(new HandOutcome(Folded: false, WentToShowdown: true, WonAnyPot: true, WouldHaveBeenBest: null));
        Assert.Equal(-0.03, bot.Offset, 10);
        bot.EndHand(new HandOutcome(Folded: false, WentToShowdown: true, WonAnyPot: false, WouldHaveBeenBest: null));
        Assert.Equal(-0.02, bot.Offset, 10);
        for (int i = 0; i < 10; i++)
        {
            bot.EndHand(new HandOutcome(Folded: true, WentToShowdown: false, WonAnyPot: false, WouldHaveBeenBest: true));
        }
        Assert.Equal(-0.05, bot.Offset, 10);
        bot.StartMatch();
        Assert.Equal(0, bot.Offset);
    }

    [Fact]
    public void ThresholdBotBeatsTheFixedBaselinesHeadsUp()
    {
        // 第一版门槛人机的最低要求：单挑整场稳定赢过三个固定对照。
        foreach (Func<ulong, IBot> opponent in new Func<ulong, IBot>[] { _ => new CallingStationBot(), _ => new ManiacBot(), seed => new RandomBot(seed) })
        {
            int wins = 0;
            const int matches = 400;
            for (ulong seed = 1; seed <= matches; seed++)
            {
                // 交换座位，抵消座位先后的影响。
                bool swap = seed % 2 == 0;
                var me = Threshold("门槛");
                var other = opponent(seed);
                var result = SelfPlayArena.PlayMatch(swap ? new[] { other, me } : new IBot[] { me, other }, seed);
                int mySeat = swap ? 1 : 0;
                if (result.FinalChips[mySeat] > result.FinalChips[1 - mySeat])
                {
                    wins++;
                }
            }
            Assert.True(wins > matches * 0.55, $"对 {opponent(0).Name} 只赢了 {wins}/{matches} 场。");
        }
    }

    private sealed class RecordingBot(int seat) : IBot
    {
        public int Seat => seat;
        public List<BotObservation> Seen { get; } = [];
        public string Name => "记录";
        public void StartMatch() { }
        public BetDecision DecideBet(BettingObservation observation)
        {
            Seen.Add(observation.Common);
            return observation.Legal.CanCheck ? BetDecision.Check : observation.Legal.CanCall ? BetDecision.Call : BetDecision.AllIn;
        }
        public bool DecideReroute(RerouteObservation observation)
        {
            Seen.Add(observation.Common);
            return observation.Common.Stack >= observation.Fee;
        }
        public void EndHand(HandOutcome outcome) { }
    }

    private sealed class IllegalBot : IBot
    {
        public string Name => "非法";
        public void StartMatch() { }
        // 没人下注时也弃牌，规则不允许。
        public BetDecision DecideBet(BettingObservation observation) => BetDecision.Fold;
        public bool DecideReroute(RerouteObservation observation) => false;
        public void EndHand(HandOutcome outcome) { }
    }

    /// <summary>面对下注就弃，没人下注时过牌（规则不允许无注时弃牌）。</summary>
    private sealed class FoldingBot : IBot
    {
        public List<HandOutcome> Outcomes { get; } = [];
        public string Name => "必弃";
        public void StartMatch() { }
        public BetDecision DecideBet(BettingObservation observation) =>
            observation.Legal.CanCheck ? BetDecision.Check : BetDecision.Fold;
        public bool DecideReroute(RerouteObservation observation) => false;
        public void EndHand(HandOutcome outcome) => Outcomes.Add(outcome);
    }
}
