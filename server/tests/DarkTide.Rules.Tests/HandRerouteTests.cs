using DarkTide.Rules;

namespace DarkTide.Rules.Tests;

/// <summary>改航窗口接入单局：只换第二张、反向顺序、冻结报价、附加池与结算。</summary>
public class HandRerouteTests
{
    private static Card C(Suit suit, int rank) => new(suit, rank);
    private const Suit R = Suit.Red, B = Suit.Blue, G = Suit.Green;

    private static void CheckAround(Hand hand)
    {
        while (hand.IsBetting)
        {
            hand.Act(hand.ActingSeat, BettingAction.Check);
        }
    }

    /// <summary>收底注、一注二注都过牌，停在改航窗口。</summary>
    private static Hand ToRerouteWindow(int[] stacks, IReadOnlyList<Card>? deck = null, int[]? order = null)
    {
        var hand = new Hand(stacks.Length, deck ?? Deck.CreateStandard(), order ?? Enumerable.Range(0, stacks.Length).ToArray(), 1, stacks);
        hand.CollectAntes();
        hand.StartBettingRound();
        CheckAround(hand);
        hand.StartBettingRound();
        CheckAround(hand);
        return hand;
    }

    [Fact]
    public void WindowRunsInReverseBettingOrderWithFrozenQuotes()
    {
        var hand = ToRerouteWindow([100, 100, 100], order: [2, 0, 1]);

        Assert.Equal(HandStage.Reroute, hand.CurrentStage);
        // 下注顺序 2→0→1，改航 1→0→2。池 3，E 都是 3，报价 max(底注 1, 3×费率 向上取整) 都是 1。
        Assert.Equal(new[] { 1, 0, 2 }, hand.RerouteQuotes.Select(quote => quote.Seat));
        Assert.Equal(1, hand.RerouteActingSeat);
        Assert.All(hand.RerouteQuotes, quote => Assert.Equal(1, quote.Fee));
    }

    [Fact]
    public void RerouteReplacesOnlyTheSecondCardAndDiscardsItPublicly()
    {
        var deck = Deck.CreateStandard();
        var hand = ToRerouteWindow([100, 100, 100], deck);
        // 6 张底牌之后：deck[6] 第一张公共牌，deck[7] 第二张，下一张 deck[8] 用来替换。
        var first = hand.CommunityCards[0];
        var second = hand.CommunityCards[1];

        hand.Reroute(hand.RerouteActingSeat);

        Assert.Equal(first, hand.CommunityCards[0]);
        Assert.Equal(deck[8], hand.CommunityCards[1]);
        Assert.Equal(new[] { second }, hand.DiscardedCards);
        Assert.Equal(2, hand.CommunityCards.Count);
    }

    [Fact]
    public void LaterPlayerCanReplaceTheCardThePreviousPlayerJustDrew()
    {
        var deck = Deck.CreateStandard();
        var hand = ToRerouteWindow([100, 100, 100], deck);

        hand.Reroute(hand.RerouteActingSeat);
        hand.PassReroute(hand.RerouteActingSeat);
        hand.Reroute(hand.RerouteActingSeat);

        // 第一人换入 deck[8]，第三人再把它换成 deck[9]。被换掉的两张都公开弃置，本局不再出现。
        Assert.Equal(deck[9], hand.CommunityCards[1]);
        Assert.Equal(new[] { deck[7], deck[8] }, hand.DiscardedCards);
        Assert.Equal(HandStage.ThirdBet, hand.CurrentStage);

        // 第三张公共牌在窗口结束后才揭开，取牌库的下一张。
        hand.StartBettingRound();
        Assert.Equal(deck[10], hand.CommunityCards[2]);
    }

    [Fact]
    public void FeeIsPaidFromTheStackAndGoesToAPotOutsideRegularContributions()
    {
        int[] stacks = [100, 100, 100];
        var hand = ToRerouteWindow(stacks);
        int seat = hand.RerouteActingSeat;
        int fee = hand.CurrentRerouteQuote!.Fee;

        hand.Reroute(seat);

        Assert.Equal(99 - fee, hand.StackAt(seat));
        Assert.Equal(fee, hand.RerouteFees[seat]);
        // 普通投入不变：改航费不抵跟注、不增加边池资格、不提高底池上限。
        Assert.Equal(new[] { 1, 1, 1 }, hand.Contributions);
        Assert.Equal(3, hand.RegularPotTotal);
        var bonus = Assert.Single(hand.BonusPots);
        Assert.Equal(fee, bonus.Total.Amount);
    }

    [Fact]
    public void OnlyTheCurrentSeatMayActAndOnlyOnce()
    {
        var hand = ToRerouteWindow([100, 100, 100], order: [0, 1, 2]);
        // 改航顺序 2→1→0。

        Assert.Equal(RejectionReason.NotYourTurn, Assert.Throws<RuleRejectedException>(() => hand.Reroute(0)).Reason);
        hand.PassReroute(2);
        // 放弃后不能补做：已经轮到下一个人。
        Assert.Equal(RejectionReason.NotYourTurn, Assert.Throws<RuleRejectedException>(() => hand.Reroute(2)).Reason);
        hand.Reroute(1);
        hand.PassReroute(0);
        Assert.Equal(RejectionReason.StageNotAllowed, Assert.Throws<RuleRejectedException>(() => hand.PassReroute(0)).Reason);
    }

    [Fact]
    public void PlayerWhoCannotAffordTheQuoteMayOnlyPassAndKeepsThePosition()
    {
        // 先确认全下者不占顺位：座位 2 开局 12 枚，两轮跟到投入 12 后全下。
        var hand = new Hand(3, Deck.CreateStandard(), [0, 1, 2], 1, [100, 100, 12]);
        hand.CollectAntes();
        hand.StartBettingRound();
        hand.Act(0, BettingAction.Raise, 3);
        hand.Act(1, BettingAction.Call);
        hand.Act(2, BettingAction.Call);
        hand.StartBettingRound();
        hand.Act(0, BettingAction.Raise, 8);
        hand.Act(1, BettingAction.Call);
        hand.Act(2, BettingAction.Call);
        // 每人投入 12，座位 2 余额 0 → 全下，不在名单里；名单 1→0，各人 E = 36。
        Assert.Equal(new[] { 1, 0 }, hand.RerouteQuotes.Select(quote => quote.Seat));
        Assert.Equal(new[] { 2, 4 }, hand.RerouteQuotes.Select(quote => quote.Fee));
        hand.PassAllReroutes();

        // 另起一局，让排在前面的人付不起：座位 1 余额只剩 1，报价 2。
        var poor = new Hand(3, Deck.CreateStandard(), [0, 1, 2], 1, [100, 14, 100]);
        poor.CollectAntes();
        poor.StartBettingRound();
        poor.Act(0, BettingAction.Raise, 3);
        poor.Act(1, BettingAction.Call);
        poor.Act(2, BettingAction.Call);
        poor.StartBettingRound();
        poor.Act(0, BettingAction.Raise, 8);
        poor.Act(1, BettingAction.Call);
        poor.Act(2, BettingAction.Call);
        // 投入各 12，座位 1 剩 2 枚；名单 2→1→0，E 都是 36，报价 2、3、4。
        Assert.Equal(new[] { 2, 1, 0 }, poor.RerouteQuotes.Select(quote => quote.Seat));
        Assert.Equal(new[] { 2, 3, 4 }, poor.RerouteQuotes.Select(quote => quote.Fee));
        poor.PassReroute(2);
        Assert.Equal(RejectionReason.IllegalAction, Assert.Throws<RuleRejectedException>(() => poor.Reroute(1)).Reason);
        // 付不起的人仍占原顺位：座位 0 的报价仍是 4，不因前面有人付不起而降价或前移。
        poor.PassReroute(1);
        Assert.Equal(0, poor.RerouteActingSeat);
        Assert.Equal(4, poor.CurrentRerouteQuote!.Fee);
    }

    [Fact]
    public void PayingTheExactFeeLeavesThePlayerAllInForTheRestOfTheHand()
    {
        // 座位 1 交完底注剩 2 枚，二注后仍是 2；名单 1→0（反向），座位 1 第 1 位，池 2，报价 max(1, 2×5%) = 1。
        // 用底注 2 让报价等于余额：座位 1 开局 4，交底注 2 后剩 2，报价 max(2, 4×5%) = 2。
        var hand = new Hand(2, Deck.CreateStandard(), [0, 1], 2, [100, 4]);
        hand.CollectAntes();
        hand.StartBettingRound();
        CheckAround(hand);
        hand.StartBettingRound();
        CheckAround(hand);

        Assert.Equal(1, hand.RerouteActingSeat);
        Assert.Equal(2, hand.CurrentRerouteQuote!.Fee);
        hand.Reroute(1);

        Assert.True(hand.IsAllIn(1));
        hand.PassAllReroutes();
        // 只剩座位 0 有筹码，三注跳过；牌照常揭开。
        Assert.False(hand.StartBettingRound());
        Assert.Equal(HandStage.Showdown, hand.CurrentStage);
    }

    [Fact]
    public void BonusPotGoesToTheBestEligibleHandAndConservesChips()
    {
        // 座位 0：两张 9 配第一张公共牌 9 → 三条，无论第二张换成什么。座位 1：散牌。
        // 座位 1 付费改航，费用进入只有 0/1 能赢的附加池，被座位 0 赢走。
        var front = new[] { C(R, 9), C(R, 1), C(B, 9), C(B, 3), C(G, 9), C(G, 5) };
        var deck = front.Concat(Deck.CreateStandard().Except(front)).ToArray();
        int[] stacks = [100, 100];
        var hand = ToRerouteWindow(stacks, deck, order: [0, 1]);

        Assert.Equal(1, hand.RerouteActingSeat);
        int fee = hand.CurrentRerouteQuote!.Fee;
        hand.Reroute(1);
        hand.PassAllReroutes();
        hand.StartBettingRound();
        CheckAround(hand);

        var result = hand.Settle();

        var bonus = Assert.Single(result.BonusPots);
        Assert.Equal(fee, bonus.Total.Amount);
        Assert.Equal(new[] { 0 }, bonus.Winners);
        Assert.Equal(new[] { 99 + 2 + fee, 99 - fee }, result.FinalStacks);
        Assert.Equal(stacks.Sum(), result.FinalStacks.Sum());
    }

    [Fact]
    public void FoldAfterPayingLosesBonusEligibilityButTheFeeStaysInThePot()
    {
        // 规则 4.1：弃牌后，已付的改航费不退；规则 5.3：冻结名单里后来弃牌的人失去资格。
        int[] stacks = [100, 100, 100];
        var hand = ToRerouteWindow(stacks, order: [0, 1, 2]);
        // 改航顺序 2→1→0：座位 2 付费，然后在三注里弃牌。
        int fee = hand.CurrentRerouteQuote!.Fee;
        hand.Reroute(2);
        hand.PassAllReroutes();
        hand.StartBettingRound();
        hand.Act(0, BettingAction.Raise, 2);
        hand.Act(1, BettingAction.Call);
        hand.Act(2, BettingAction.Fold);

        var result = hand.Settle();

        var bonus = Assert.Single(result.BonusPots);
        Assert.Equal(new[] { 0, 1 }, bonus.EligibleSeats);
        Assert.DoesNotContain(2, bonus.Winners);
        // 座位 2 只投了底注 1，加上改航费；三注面对开注直接弃牌，没有再投。
        Assert.Equal(99 - fee, result.FinalStacks[2]);
        Assert.Equal(stacks.Sum(), result.FinalStacks.Sum());
    }

    [Fact]
    public void BonusPotsWithTheSameEligibilityAreMergedAtSettlement()
    {
        int[] stacks = [100, 100, 100];
        var hand = ToRerouteWindow(stacks);
        hand.Reroute(hand.RerouteActingSeat);
        hand.Reroute(hand.RerouteActingSeat);
        hand.Reroute(hand.RerouteActingSeat);
        Assert.Equal(3, hand.BonusPots.Count);
        hand.StartBettingRound();
        CheckAround(hand);

        var result = hand.Settle();

        // 三笔费用的资格名单相同，合并成一个池，余数只分配一次。
        var merged = Assert.Single(result.BonusPots);
        Assert.Equal(hand.RerouteFees.Sum(), merged.Total.Amount);
        Assert.Equal(stacks.Sum(), result.FinalStacks.Sum());
    }

    [Fact]
    public void EmptyWindowGoesStraightToTheThirdBet()
    {
        // 名单为空要求所有未弃牌者都没有余额：两人都只有 1 枚，交完底注都全下。
        var hand = new Hand(2, Deck.CreateStandard(), [0, 1], 1, [1, 1]);
        hand.CollectAntes();
        Assert.False(hand.StartBettingRound());
        Assert.False(hand.StartBettingRound());

        Assert.Equal(HandStage.ThirdBet, hand.CurrentStage);
        Assert.Empty(hand.RerouteQuotes);
        Assert.Equal(-1, hand.RerouteActingSeat);
    }
}
