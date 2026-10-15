using DarkTide.Rules;

namespace DarkTide.Rules.Tests;

/// <summary>
/// 随机打完整局：每一步都从合法动作里随机选，检查任何走法下都必须成立的不变量。
/// 手写用例只覆盖想得到的局面，这里用于发现想不到的组合（连环短额全下、多次弃牌等）。
/// 种子固定，失败可以复现。
/// </summary>
public class HandPropertyTests
{
    [Fact]
    public void RandomHandsConserveChipsAndKeepPotsConsistent()
    {
        var random = new Random(20261014);
        for (int game = 0; game < 3000; game++)
        {
            var log = new List<string>();
            try
            {
                PlayRandomHand(random, game, log);
            }
            catch (Exception error) when (error is not Xunit.Sdk.XunitException)
            {
                // 规则代码抛出的非断言异常：带上完整动作记录，便于直接写成回归用例。
                throw new Xunit.Sdk.XunitException($"{error.GetType().Name}: {error.Message}\n" + string.Join("\n", log));
            }
        }
    }

    private static void PlayRandomHand(Random random, int game, List<string> log)
    {
        int seatCount = random.Next(TableLimits.MinSeats, TableLimits.MaxSeats + 1);
        var stacks = new int[seatCount];
        var participants = Enumerable.Range(0, seatCount).Where(_ => random.Next(5) > 0).ToList();
        while (participants.Count < 2)
        {
            participants = Enumerable.Range(0, seatCount).ToList();
        }
        foreach (int seat in participants)
        {
            // 混入很短的筹码，频繁触发底注不足、短额全下与边池。
            stacks[seat] = random.Next(4) == 0 ? random.Next(1, 6) : random.Next(1, 150);
        }
        var order = participants.OrderBy(_ => random.Next()).ToArray();
        var deck = Shuffler.ShuffleStandard().ToArray();
        int ante = random.Next(1, 3);
        var hand = new Hand(seatCount, deck, order, ante, stacks);
        string context = $"第 {game} 局：座位 {seatCount}，名单 [{string.Join(",", order)}]，筹码 [{string.Join(",", stacks)}]，底注 {ante}";
        log.Add(context);

        hand.CollectAntes();
        AssertInvariants(hand, stacks, context);

        int steps = 0;
        while (!hand.IsComplete)
        {
            if (hand.CurrentStage == HandStage.Reroute)
            {
                hand.SkipReroute();
                continue;
            }
            log.Add($"开始 {hand.CurrentStage}，投入 [{string.Join(",", hand.Contributions)}]");
            if (!hand.StartBettingRound())
            {
                AssertBetweenRounds(hand, stacks, context);
                continue;
            }

            while (hand.IsBetting)
            {
                Assert.True(++steps < 500, $"{context}：下注没有在合理步数内结束。");
                int seat = hand.ActingSeat;
                Assert.False(hand.HasFolded(seat), context);
                Assert.True(hand.StackAt(seat) > 0, $"{context}：没有筹码的座位 {seat} 不应被要求行动。");
                ActRandomly(hand, seat, random, context, log);
                AssertInvariants(hand, stacks, context);
            }
            AssertBetweenRounds(hand, stacks, context);
        }

        Assert.Equal(HandStage.Showdown, hand.CurrentStage);

        // 结算：任何走法下都必须守恒、不出负数，并且能写回整场。
        var settlement = hand.Settle();
        Assert.Equal(stacks.Sum(), settlement.FinalStacks.Sum());
        Assert.All(settlement.FinalStacks, stack => Assert.True(stack >= 0, context));
        // 弃牌者拿不到任何池；有比牌时，每个池的赢家在该池资格者里牌力最大。
        Assert.All(settlement.Pots, pot => Assert.All(pot.Winners, seat => Assert.False(hand.HasFolded(seat), context)));
        if (settlement.Showdown)
        {
            foreach (var pot in settlement.Pots.Where(pot => pot.EligibleSeats.Count >= 2))
            {
                var best = pot.EligibleSeats.Max(seat => settlement.HandValues[seat]!.Value);
                Assert.All(pot.Winners, seat => Assert.Equal(best, settlement.HandValues[seat]!.Value));
            }
        }
        var match = new Match(seatCount, initialChips: 0);
        for (int seat = 0; seat < seatCount; seat++)
        {
            match.AddChips(seat, new Chips(stacks[seat]));
        }
        match.StartNextHand();
        match.SettleHand(settlement.FinalStacks);
        log.Add($"结算 [{string.Join(",", settlement.FinalStacks)}]");
        // 只剩一名未弃牌者时提前结束，可能不足三张公共牌；否则三张都已揭开。
        int notFolded = order.Count(seat => !hand.HasFolded(seat));
        Assert.True(notFolded == 1 || hand.CommunityCards.Count == Hand.CommunityCardCount, context);
        Assert.Equal(deck.Skip(order.Length * Hand.HoleCardsPerPlayer).Take(hand.CommunityCards.Count), hand.CommunityCards);
    }

    private static void ActRandomly(Hand hand, int seat, Random random, string context, List<string> log)
    {
        var legal = hand.GetLegalActions();
        var choices = new List<(BettingAction Action, int Target)>();
        if (legal.CanCheck) choices.Add((BettingAction.Check, 0));
        if (legal.CanCall) choices.Add((BettingAction.Call, 0));
        if (legal.CanFold) choices.Add((BettingAction.Fold, 0));
        if (legal.CanGoAllIn) choices.Add((BettingAction.AllIn, 0));
        if (legal.CanRaise && legal.MinRaiseTarget <= legal.MaxRaiseTarget)
        {
            choices.Add((BettingAction.Raise, random.Next(legal.MinRaiseTarget, legal.MaxRaiseTarget + 1)));
        }
        Assert.NotEmpty(choices);

        // 合法动作偶尔也会被规则拒绝（例如没有加价权的全下）；拒绝时状态不能改变，然后换一个动作。
        foreach (var (action, target) in choices.OrderBy(_ => random.Next()))
        {
            int stackBefore = hand.StackAt(seat);
            int committedBefore = hand.CommittedThisRound(seat);
            try
            {
                log.Add($"  座位 {seat} {action}{(action == BettingAction.Raise ? " " + target : "")}（余额 {stackBefore}，本轮已投 {committedBefore}）");
                hand.Act(seat, action, target);
                return;
            }
            catch (RuleRejectedException error)
            {
                log.Add($"    被拒绝：{error.Message}");
                Assert.Equal(RejectionReason.IllegalAction, error.Reason);
                Assert.Equal(seat, hand.ActingSeat);
                Assert.Equal(stackBefore, hand.StackAt(seat));
                Assert.Equal(committedBefore, hand.CommittedThisRound(seat));
            }
        }
        Assert.Fail($"{context}：座位 {seat} 列出的合法动作全部被拒绝。");
    }

    private static void AssertInvariants(Hand hand, int[] startingStacks, string context)
    {
        for (int seat = 0; seat < hand.SeatCount; seat++)
        {
            int total = hand.StackAt(seat) + hand.Contributions[seat] + hand.CommittedThisRound(seat);
            Assert.True(startingStacks[seat] == total, $"{context}：座位 {seat} 筹码不守恒，开局 {startingStacks[seat]}，现在合计 {total}。");
            Assert.True(hand.StackAt(seat) >= 0, context);
        }
    }

    private static void AssertBetweenRounds(Hand hand, int[] startingStacks, string context)
    {
        AssertInvariants(hand, startingStacks, context);
        Assert.False(hand.IsBetting, context);

        // 退款后的不变量：最高累计投入至少被两个座位持有（规则 4.5）。
        int max = hand.Contributions.Max();
        Assert.True(max == 0 || hand.Contributions.Count(amount => amount == max) >= 2,
            $"{context}：轮间仍有未被匹配的超额，投入 [{string.Join(",", hand.Contributions)}]。");

        var pots = PotLayering.SplitRegularPots(hand.Contributions, hand.FoldedSnapshot());
        Assert.Equal(hand.RegularPotTotal, PotLayering.TotalAmount(pots));
        foreach (var pot in pots)
        {
            Assert.All(pot.EligibleSeats, seat => Assert.False(hand.HasFolded(seat), context));
        }
        // 有人没弃牌时，最高一层（最后的边池）一定有人可以争夺。
        if (pots.Count > 0)
        {
            Assert.NotEmpty(pots[^1].EligibleSeats);
        }
    }
}
