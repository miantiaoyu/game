using DarkTide.Rules;

namespace DarkTide.Rules.Tests;

/// <summary>整局打完到结算：用排好的牌序，事先知道谁赢。</summary>
public class HandSettlementTests
{
    private static Card C(Suit suit, int rank) => new(suit, rank);
    private const Suit R = Suit.Red, B = Suit.Blue, G = Suit.Green;

    /// <summary>
    /// 按发牌顺序拼出牌序：先每人第一张，再每人第二张，然后三张公共牌，剩下的随便接上。
    /// holes[i] 是参与顺序里第 i 个人的两张底牌。
    /// </summary>
    private static Card[] Deal(Card[][] holes, Card[] board)
    {
        var front = holes.Select(hole => hole[0]).Concat(holes.Select(hole => hole[1])).Concat(board).ToArray();
        return front.Concat(Deck.CreateStandard().Except(front)).ToArray();
    }

    private static void CheckAround(Hand hand)
    {
        while (hand.IsBetting)
        {
            hand.Act(hand.ActingSeat, BettingAction.Check);
        }
    }

    private static void PlayToShowdownChecking(Hand hand)
    {
        while (!hand.IsComplete)
        {
            if (hand.CurrentStage == HandStage.Reroute)
            {
                hand.PassAllReroutes();
                continue;
            }
            if (hand.StartBettingRound())
            {
                CheckAround(hand);
            }
        }
    }

    [Fact]
    public void BestHandTakesTheWholePotAtShowdown()
    {
        // 座位 0：两张 8 配公共 8 → 三条；座位 1：对子；座位 2：散牌。
        var deck = Deal(
            [[C(R, 8), C(B, 8)], [C(R, 5), C(B, 5)], [C(R, 1), C(B, 3)]],
            [C(G, 8), C(R, 2), C(B, 9)]);
        var hand = new Hand(3, deck, new[] { 0, 1, 2 }, 1, new[] { 100, 100, 100 });
        hand.CollectAntes();
        PlayToShowdownChecking(hand);

        var result = hand.Settle();

        Assert.True(result.Showdown);
        Assert.Equal(new[] { 102, 99, 99 }, result.FinalStacks);
        Assert.Equal(HandCategory.ThreeOfAKind, result.HandValues[0]!.Value.Category);
    }

    [Fact]
    public void LastPlayerStandingWinsWithoutShowingCards()
    {
        var hand = new Hand(3, Deck.CreateStandard(), new[] { 0, 1, 2 }, 1, new[] { 100, 100, 100 });
        hand.CollectAntes();
        hand.StartBettingRound();
        hand.Act(0, BettingAction.Raise, 3);
        hand.Act(1, BettingAction.Fold);
        hand.Act(2, BettingAction.Fold);

        var result = hand.Settle();

        // 没有比牌：只揭了一张公共牌，赢家的牌力不计算、不公开。
        Assert.False(result.Showdown);
        Assert.All(result.HandValues, Assert.Null);
        Assert.Equal(new[] { 102, 99, 99 }, result.FinalStacks);
    }

    [Fact]
    public void ShortAllInWinsOnlyTheMainPotAndTheSidePotGoesToTheBestOfTheRest()
    {
        // 座位 1 只有 9 枚（底注后 8），全下到 8；座位 0、2 继续投到 20。座位 1 三条最强，座位 0 对子 5，座位 2 散牌。
        // 全下额度要在底池上限以内：此时池 5、跟注 2，最多新增 9，所以 8 可以全下。
        var deck = Deal(
            [[C(R, 5), C(B, 5)], [C(R, 9), C(B, 9)], [C(R, 1), C(B, 7)]],
            [C(G, 9), C(R, 2), C(G, 4)]);
        var hand = new Hand(3, deck, new[] { 0, 1, 2 }, 1, new[] { 100, 9, 100 });
        hand.CollectAntes();
        hand.StartBettingRound();
        hand.Act(0, BettingAction.Raise, 2);
        hand.Act(1, BettingAction.AllIn);
        hand.Act(2, BettingAction.Raise, 20);
        hand.Act(0, BettingAction.Call);
        PlayToShowdownChecking(hand);

        var result = hand.Settle();

        // 累计投入 [21, 9, 21]。主池 27（三人各 9），座位 1 三条赢；边池 24（座位 0、2 各多 12），座位 0 对子 5 赢。
        Assert.Equal(2, result.Pots.Count);
        Assert.Equal(27, result.Pots[0].Total.Amount);
        Assert.Equal(new[] { 1 }, result.Pots[0].Winners);
        Assert.Equal(24, result.Pots[1].Total.Amount);
        Assert.Equal(new[] { 0 }, result.Pots[1].Winners);
        Assert.Equal(new[] { 79 + 24, 27, 79 }, result.FinalStacks);
    }

    [Fact]
    public void TiedHandsSplitAndTheOddChipFollowsBettingOrder()
    {
        // 座位 2、0 都是对子 7 带 9（花色不同），座位 1 散牌 9-7-5。参与顺序从座位 2 开始。池 3，余 1 给座位 2。
        // 参与顺序 [2, 0, 1]：holes 依次对应座位 2、0、1。挑牌时避开了意外的同花与顺子。
        var ordered = Deal(
            [[C(R, 7), C(R, 1)], [C(B, 7), C(R, 4)], [C(R, 2), C(B, 5)]],
            [C(G, 7), C(G, 9), C(B, 3)]);
        var hand = new Hand(3, ordered, new[] { 2, 0, 1 }, 1, new[] { 100, 100, 100 });
        hand.CollectAntes();
        PlayToShowdownChecking(hand);

        var result = hand.Settle();

        Assert.Equal(new[] { 2, 0 }, result.Pots[0].Winners);
        Assert.Equal(new[] { 2, 1 }, result.Pots[0].Shares);
        Assert.Equal(new[] { 100, 99, 101 }, result.FinalStacks);
    }

    [Fact]
    public void SettlementWritesBackToTheMatchAndEliminationFollows()
    {
        // 座位 1 只有 1 枚，交完底注就全下；座位 0 的牌更强，座位 1 结算后归零，下一局不再参加。
        var deck = Deal(
            [[C(R, 9), C(B, 9)], [C(R, 1), C(B, 3)]],
            [C(G, 9), C(R, 2), C(G, 4)]);
        var match = new Match(2, initialChips: 1);
        match.AddChips(0, new Chips(99));
        match.StartNextHand();
        var hand = new Hand(2, deck, match.SeatsWithChips(), match.CurrentAnte().Amount, match.ChipsSnapshot());
        hand.CollectAntes();
        PlayToShowdownChecking(hand);

        match.SettleHand(hand.Settle().FinalStacks);

        Assert.Equal(101, match.ChipsAt(0).Amount);
        Assert.Equal(0, match.ChipsAt(1).Amount);
        Assert.Equal(new[] { 1 }, match.FindEliminatedSeats());
        Assert.True(match.IsDecided);
    }

    [Fact]
    public void NonParticipantsKeepTheirChips()
    {
        var hand = new Hand(4, Deck.CreateStandard(), new[] { 0, 2 }, 1, new[] { 100, 37, 100, 0 });
        hand.CollectAntes();
        PlayToShowdownChecking(hand);

        var result = hand.Settle();

        Assert.Equal(37, result.FinalStacks[1]);
        Assert.Equal(0, result.FinalStacks[3]);
        Assert.Equal(237, result.FinalStacks.Sum());
    }

    [Fact]
    public void SettlingTooEarlyIsRejectedAndSettlingTwiceReturnsTheSameResult()
    {
        var hand = new Hand(2, Deck.CreateStandard(), new[] { 0, 1 }, 1, new[] { 100, 100 });
        Assert.Throws<InvalidOperationException>(() => hand.Settle());
        hand.CollectAntes();
        hand.StartBettingRound();
        Assert.Throws<InvalidOperationException>(() => hand.Settle());
        CheckAround(hand);
        Assert.Throws<InvalidOperationException>(() => hand.Settle());

        PlayToShowdownChecking(hand);
        var first = hand.Settle();

        Assert.Same(first, hand.Settle());
        Assert.True(hand.IsSettled);
    }
}
