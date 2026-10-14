using DarkTide.Rules;

namespace DarkTide.Rules.Tests;

public class HandTests
{
    public static IEnumerable<object[]> SeatAndParticipantConfigurations()
    {
        for (int totalSeats = 2; totalSeats <= 8; totalSeats++)
        {
            for (int participantCount = 2; participantCount <= totalSeats; participantCount++)
            {
                yield return new object[] { totalSeats, participantCount };
            }
        }
    }

    [Theory]
    [MemberData(nameof(SeatAndParticipantConfigurations))]
    public void OnlyParticipatingSeatsAreDealtAcrossAllTableSizes(int totalSeats, int participantCount)
    {
        var drawOrder = Deck.CreateStandard();
        var participantOrder = Enumerable.Range(0, totalSeats).Reverse().Take(participantCount).ToArray();
        var hand = TestHands.Deal(totalSeats, drawOrder, participantOrder);
        var dealt = new List<Card>();

        Assert.Equal(totalSeats, hand.SeatCount);
        Assert.Equal(participantCount, hand.ParticipantCount);
        Assert.Equal(27 - 2 * participantCount, hand.RemainingCount);
        Assert.Equal(participantOrder, hand.ParticipantOrder);

        for (int seat = 0; seat < totalSeats; seat++)
        {
            var view = hand.CreatePlayerView(seat);
            Assert.Equal(participantOrder.Contains(seat) ? 2 : 0, view.HoleCards.Count);
            Assert.Equal(Enumerable.Range(0, totalSeats).Where(Id => Id != seat),
                view.OtherSeats.Select(overview => overview.seat));
            Assert.All(view.OtherSeats, overview =>
                Assert.Equal(participantOrder.Contains(overview.seat) ? 2 : 0, overview.CardCount));
            dealt.AddRange(view.HoleCards);
        }

        Assert.Equal(2 * participantCount, dealt.Distinct().Count());
        Assert.True(drawOrder.Take(2 * participantCount).ToHashSet().SetEquals(dealt));
    }

    [Theory]
    [InlineData(6, 1, 4)]
    [InlineData(0, 2, 5)]
    [InlineData(3, 3, 6)]
    public void ThreeOfEightSeatsAreDealtInGivenOrder(int seat, int firstId, int secondId)
    {
        var hand = TestHands.Deal(8, Deck.CreateStandard(), new[] { 6, 0, 3 });

        Assert.Equal(new[] { firstId, secondId },
            hand.CreatePlayerView(seat).HoleCards.Select(card => (int)card.Id));
        Assert.Equal(21, hand.RemainingCount);
    }

    [Theory]
    [InlineData(0, 4, 9)]
    [InlineData(1, 5, 10)]
    [InlineData(2, 1, 6)]
    [InlineData(3, 2, 7)]
    [InlineData(4, 3, 8)]
    public void TwoRoundsDealtInGivenSeatOrder(int seat, int firstId, int secondId)
    {
        var hand = TestHands.Deal(5, Deck.CreateStandard(), new[] { 2, 3, 4, 0, 1 });

        var view = hand.CreatePlayerView(seat);

        Assert.Equal(seat, view.Seat);
        Assert.Equal(new[] { firstId, secondId }, view.HoleCards.Select(card => (int)card.Id));
    }

    [Fact]
    public void FivePlayersUseFirstTenCards()
    {
        var drawOrder = Deck.CreateStandard().Reverse().ToArray();
        var hand = TestHands.Deal(5, drawOrder, new[] { 0, 1, 2, 3, 4 });
        var allDealt = new List<Card>();

        for (int seat = 0; seat < 5; seat++)
        {
            var view = hand.CreatePlayerView(seat);
            Assert.Equal(2, view.HoleCards.Count);
            allDealt.AddRange(view.HoleCards);
        }

        Assert.Equal(10, allDealt.Distinct().Count());
        Assert.True(drawOrder.Take(10).ToHashSet().SetEquals(allDealt));
        Assert.Equal(17, hand.RemainingCount);
    }

    [Fact]
    public void MutatingInputsDoesNotChangeExistingHoleCards()
    {
        var inputDeck = Deck.CreateStandard().ToArray();
        int[] inputSeats = { 0, 1, 2, 3, 4 };
        var hand = TestHands.Deal(5, inputDeck, inputSeats);

        Array.Reverse(inputDeck);
        Array.Reverse(inputSeats);
        _ = TestHands.Deal(5, inputDeck, inputSeats);

        Assert.Equal(new[] { new Card(Suit.Red, 1), new Card(Suit.Red, 6) }, hand.CreatePlayerView(0).HoleCards);
        Assert.Equal(17, hand.RemainingCount);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, hand.ParticipantOrder);
    }

    [Fact]
    public void ParticipantOrderIsReadOnlyAndKeepsGivenOrder()
    {
        var hand = TestHands.Deal(8, Deck.CreateStandard(), new[] { 6, 0, 3 });

        Assert.Equal(new[] { 6, 0, 3 }, hand.ParticipantOrder);
        Assert.Equal(3, hand.ParticipantCount);
        // 只读接口若底层就是数组，调用方强转回 int[] 即可改写局面。
        Assert.False(hand.ParticipantOrder is int[]);
        Assert.Same(hand.ParticipantOrder, hand.ParticipantOrder);
    }

    [Fact]
    public void InvalidParticipantLineupsAreRejected()
    {
        int[][] invalidOrders =
        {
            Array.Empty<int>(),
            new[] { 0 },
            new[] { 0, 1, 2, 3, 4, 0 },
            new[] { 0, 1, 2, 3, 3 },
            new[] { -1, 1, 2, 3, 4 },
            new[] { 0, 1, 2, 3, 5 }
        };

        foreach (var order in invalidOrders)
        {
            Assert.Throws<ArgumentException>(() => TestHands.Deal(5, Deck.CreateStandard(), order));
        }
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(int.MaxValue)]
    public void UnsupportedTableSizeIsRejected(int invalidCapacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TestHands.Deal(invalidCapacity, Deck.CreateStandard(), new[] { 0, 1 }));
    }

    [Fact]
    public void NullInputsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => TestHands.Deal(5, null!, new[] { 0, 1, 2, 3, 4 }));
        Assert.Throws<ArgumentNullException>(() => TestHands.Deal(5, Deck.CreateStandard(), null!));
    }

    [Fact]
    public void IncompleteOrDuplicateDeckIsRejected()
    {
        var inputDeck = Deck.CreateStandard().ToArray();
        int[] order = { 0, 1, 2, 3, 4 };

        Assert.Throws<ArgumentException>(() => TestHands.Deal(5, inputDeck.Take(26).ToArray(), order));

        inputDeck[26] = inputDeck[0];
        Assert.Throws<ArgumentException>(() => TestHands.Deal(5, inputDeck, order));
    }

    // ── 底注、下注流程、退款 ───────────────────────────────────────────

    private static Hand NewHand(int[] stacks, int ante = 1, int[]? order = null, IReadOnlyList<Card>? deck = null) =>
        new(stacks.Length, deck ?? Deck.CreateStandard(), order ?? Enumerable.Range(0, stacks.Length).ToArray(), ante, stacks);

    /// <summary>每个参与座位：开局余额 = 剩余可用 + 本局累计投入 + 进行中一轮的投入。</summary>
    private static void AssertConserved(Hand hand, int[] startingStacks)
    {
        for (int seat = 0; seat < hand.SeatCount; seat++)
        {
            Assert.Equal(startingStacks[seat], hand.StackAt(seat) + hand.Contributions[seat] + hand.CommittedThisRound(seat));
        }
    }

    [Fact]
    public void NewHandAwaitsAnteWithNoContributionsOrCommunityCards()
    {
        var hand = NewHand(new[] { 100, 100, 100, 100, 100 });

        Assert.Equal(HandStage.Ante, hand.CurrentStage);
        Assert.Equal(0, hand.RegularPotTotal);
        Assert.Empty(hand.CommunityCards);
        Assert.False(hand.IsBetting);
        Assert.All(Enumerable.Range(0, 5), seat =>
        {
            Assert.Equal(100, hand.StackAt(seat));
            Assert.False(hand.HasFolded(seat));
            Assert.False(hand.IsAllIn(seat));
        });
    }

    [Fact]
    public void CollectAntesMovesChipsFromStacksIntoContributions()
    {
        var stacks = new[] { 100, 100, 100, 100, 100 };
        var hand = NewHand(stacks, ante: 2);

        var paid = hand.CollectAntes();

        Assert.All(Enumerable.Range(0, 5), seat => Assert.Equal(2, paid[seat]));
        Assert.Equal(10, hand.RegularPotTotal);
        Assert.Equal(98, hand.StackAt(0));
        Assert.Equal(HandStage.FirstBet, hand.CurrentStage);
        AssertConserved(hand, stacks);
    }

    [Fact]
    public void ShortAntePaysEverythingAndCountsAsAllIn()
    {
        var hand = NewHand(new[] { 100, 1, 100 }, ante: 2);

        var paid = hand.CollectAntes();

        Assert.Equal(new[] { 2, 1, 2 }, paid);
        // 只交出 1 枚的座位等同全下，但仍发牌、不弃牌。
        Assert.True(hand.IsAllIn(1));
        Assert.False(hand.HasFolded(1));
        Assert.Equal(2, hand.CreatePlayerView(1).HoleCards.Count);
    }

    [Fact]
    public void NonParticipantsPayNoAnteAndHaveNoStack()
    {
        var hand = new Hand(5, Deck.CreateStandard(), new[] { 1, 0 }, 2, new[] { 100, 100, 0, 0, 0 });

        var paid = hand.CollectAntes();

        Assert.Equal(0, paid[2]);
        Assert.Equal(0, hand.Contributions[2]);
        Assert.False(hand.IsAllIn(2));
        Assert.Equal(4, hand.RegularPotTotal);
    }

    [Fact]
    public void ParticipantWithoutChipsOrBadStacksIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new Hand(3, Deck.CreateStandard(), new[] { 0, 1, 2 }, 1, new[] { 100, 0, 100 }));
        Assert.Throws<ArgumentException>(() => new Hand(3, Deck.CreateStandard(), new[] { 0, 1, 2 }, 1, new[] { 100, 100 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Hand(3, Deck.CreateStandard(), new[] { 0, 1, 2 }, 0, new[] { 100, 100, 100 }));
    }

    [Fact]
    public void CollectingAntesTwiceIsRejected()
    {
        var hand = NewHand(new[] { 100, 100, 100 });
        hand.CollectAntes();

        Assert.Throws<InvalidOperationException>(() => hand.CollectAntes());
        Assert.Equal(3, hand.RegularPotTotal);
    }

    [Fact]
    public void EachBettingRoundRevealsTheNextCommunityCard()
    {
        var deck = Deck.CreateStandard();
        var hand = NewHand(new[] { 100, 100, 100 }, deck: deck);
        hand.CollectAntes();

        // 三人各两张底牌用掉前六张，公共牌从第七张开始。
        Assert.True(hand.StartBettingRound());
        Assert.Equal(new[] { deck[6] }, hand.CommunityCards);
        CheckAround(hand);

        Assert.Equal(HandStage.SecondBet, hand.CurrentStage);
        Assert.True(hand.StartBettingRound());
        Assert.Equal(new[] { deck[6], deck[7] }, hand.CommunityCards);
        CheckAround(hand);

        Assert.Equal(HandStage.Reroute, hand.CurrentStage);
        hand.SkipReroute();
        Assert.True(hand.StartBettingRound());
        Assert.Equal(new[] { deck[6], deck[7], deck[8] }, hand.CommunityCards);
        CheckAround(hand);

        Assert.Equal(HandStage.Showdown, hand.CurrentStage);
        Assert.True(hand.IsComplete);
        Assert.Equal(27 - 6 - 3, hand.RemainingCount);
    }

    [Fact]
    public void StagesCannotBeSkippedOrRepeated()
    {
        var hand = NewHand(new[] { 100, 100, 100 });

        Assert.Throws<InvalidOperationException>(() => hand.StartBettingRound());
        Assert.Throws<InvalidOperationException>(() => hand.SkipReroute());

        hand.CollectAntes();
        hand.StartBettingRound();
        // 本轮还没结束，不能再开一轮。
        Assert.Throws<InvalidOperationException>(() => hand.StartBettingRound());
        Assert.Throws<InvalidOperationException>(() => hand.SkipReroute());
        CheckAround(hand);

        hand.StartBettingRound();
        CheckAround(hand);
        // 改航轮不能直接开始下注，必须先结束改航。
        Assert.Throws<InvalidOperationException>(() => hand.StartBettingRound());
    }

    [Fact]
    public void BetsMergeIntoContributionsOnlyWhenTheRoundCloses()
    {
        var stacks = new[] { 100, 100, 100 };
        var hand = NewHand(stacks);
        hand.CollectAntes();
        hand.StartBettingRound();

        hand.Act(0, BettingAction.Raise, 2);
        // 进行中的投入单独记账，不提前并入累计投入。
        Assert.Equal(1, hand.Contributions[0]);
        Assert.Equal(2, hand.CommittedThisRound(0));
        Assert.Equal(97, hand.StackAt(0));
        AssertConserved(hand, stacks);

        hand.Act(1, BettingAction.Call);
        Assert.True(hand.Act(2, BettingAction.Call));

        Assert.Equal(new[] { 3, 3, 3 }, hand.Contributions);
        Assert.Equal(0, hand.CommittedThisRound(0));
        Assert.Equal(9, hand.RegularPotTotal);
        AssertConserved(hand, stacks);
    }

    [Fact]
    public void UncalledExcessFromAShortAllInCallIsRefundedWhenTheRoundCloses()
    {
        // 座位 0 开注 20，座位 1 只有 11（交完底注剩 10），全下跟不全。
        var stacks = new[] { 100, 11 };
        var hand = NewHand(stacks);
        hand.CollectAntes();
        hand.StartBettingRound();

        hand.Act(0, BettingAction.Raise, 2);
        hand.Act(1, BettingAction.Raise, 6);
        hand.Act(0, BettingAction.Raise, 20);
        Assert.True(hand.Act(1, BettingAction.AllIn));

        // 座位 1 累计 11、座位 0 累计 21：多出的 10 无人能跟，退回座位 0（规则 4.5）。
        Assert.Equal(new[] { 11, 11 }, hand.Contributions);
        Assert.Equal(89, hand.StackAt(0));
        AssertConserved(hand, stacks);
        // 退款后顶层至少两人持有，划分出的奖池里没有“一人独得”的层。
        var pots = PotLayering.SplitRegularPots(hand.Contributions, hand.FoldedSnapshot());
        Assert.All(pots, pot => Assert.True(pot.EligibleSeats.Count >= 2));
    }

    [Fact]
    public void RefundComparesCumulativeContributionsNotThisRoundOnly()
    {
        // 交完底注后池为 3，余额 [99, 4, 99]。
        var stacks = new[] { 100, 5, 100 };
        var hand = NewHand(stacks);
        hand.CollectAntes();
        hand.StartBettingRound();
        hand.Act(0, BettingAction.Raise, 2);
        hand.Act(1, BettingAction.AllIn);       // 只剩 4，全下到 4
        hand.Act(2, BettingAction.Raise, 17);   // 底池上限：池 9＋2×跟注 4＝本次最多新增 17
        Assert.True(hand.Act(0, BettingAction.Fold));   // 座位 0 弃牌，只剩座位 2 能行动

        // 累计投入 [3, 5, 18]：座位 2 相对第二高的 5 多出 13，退回；弃牌者的 3 留在池里。
        Assert.False(hand.IsBetting);
        Assert.Equal(HandStage.SecondBet, hand.CurrentStage);
        Assert.Equal(new[] { 3, 5, 5 }, hand.Contributions);
        Assert.Equal(95, hand.StackAt(2));
        AssertConserved(hand, stacks);
    }

    [Fact]
    public void FoldedChipsStayInThePotAndAreNotRefunded()
    {
        var stacks = new[] { 100, 100, 100 };
        var hand = NewHand(stacks);
        hand.CollectAntes();
        hand.StartBettingRound();

        hand.Act(0, BettingAction.Raise, 3);
        hand.Act(1, BettingAction.Raise, 9);
        hand.Act(2, BettingAction.Fold);
        Assert.True(hand.Act(0, BettingAction.Call));

        Assert.Equal(new[] { 10, 10, 1 }, hand.Contributions);
        Assert.True(hand.HasFolded(2));
        Assert.Equal(99, hand.StackAt(2));
        AssertConserved(hand, stacks);
    }

    [Fact]
    public void WhenEveryoneElseFoldsTheHandEndsImmediatelyAndTheBetIsReturned()
    {
        var stacks = new[] { 100, 100, 100 };
        var hand = NewHand(stacks);
        hand.CollectAntes();
        hand.StartBettingRound();

        hand.Act(0, BettingAction.Raise, 3);
        hand.Act(1, BettingAction.Fold);
        Assert.True(hand.Act(2, BettingAction.Fold));

        // 只剩一名未弃牌者：直接结束，不再揭牌；无人跟的开注退回本人。
        Assert.True(hand.IsComplete);
        Assert.Equal(HandStage.Showdown, hand.CurrentStage);
        Assert.Single(hand.CommunityCards);
        Assert.Equal(new[] { 1, 1, 1 }, hand.Contributions);
        Assert.Equal(99, hand.StackAt(0));
        AssertConserved(hand, stacks);
        Assert.Throws<InvalidOperationException>(() => hand.StartBettingRound());
    }

    [Fact]
    public void BettingIsSkippedWhenFewerThanTwoCanActButCardsAreStillRevealed()
    {
        // 两人的底注就让其中一人全下：没人能下注，但三张公共牌照常揭开，最后摊牌。
        var hand = NewHand(new[] { 100, 1 });
        hand.CollectAntes();

        Assert.False(hand.StartBettingRound());
        Assert.Equal(HandStage.SecondBet, hand.CurrentStage);
        Assert.False(hand.StartBettingRound());
        hand.SkipReroute();
        Assert.False(hand.StartBettingRound());

        Assert.Equal(3, hand.CommunityCards.Count);
        Assert.Equal(HandStage.Showdown, hand.CurrentStage);
        // 底注阶段同样适用退款：座位 0 只被跟到 1。
        Assert.Equal(new[] { 1, 1 }, hand.Contributions);
    }

    [Fact]
    public void UncalledAnteExcessIsRefundedEvenWhenBettingIsSkipped()
    {
        // 回归（随机测试发现）：底注 2，座位 1 只有 1 枚。没人能下注，一注被跳过；
        // 以前跳过的一轮不退款，留下座位 0 多出的 1 枚无人匹配。
        var hand = new Hand(2, Deck.CreateStandard(), new[] { 1, 0 }, 2, new[] { 95, 1 });
        hand.CollectAntes();

        Assert.False(hand.StartBettingRound());

        Assert.Equal(new[] { 1, 1 }, hand.Contributions);
        Assert.Equal(94, hand.StackAt(0));
    }

    [Fact]
    public void OutOfTurnActionIsRejectedWithoutChangingState()
    {
        var hand = NewHand(new[] { 100, 100, 100 });
        hand.CollectAntes();
        hand.StartBettingRound();

        var error = Assert.Throws<RuleRejectedException>(() => hand.Act(1, BettingAction.Check));

        Assert.Equal(RejectionReason.NotYourTurn, error.Reason);
        Assert.Equal(0, hand.ActingSeat);
        Assert.Equal(0, hand.CommittedThisRound(1));
    }

    [Fact]
    public void IllegalActionIsRejectedWithItsOwnReason()
    {
        var hand = NewHand(new[] { 100, 100, 100 });
        hand.CollectAntes();
        hand.StartBettingRound();
        hand.Act(0, BettingAction.Raise, 2);

        var check = Assert.Throws<RuleRejectedException>(() => hand.Act(1, BettingAction.Check));
        var tooBig = Assert.Throws<RuleRejectedException>(() => hand.Act(1, BettingAction.Raise, 1000));
        var unknown = Assert.Throws<RuleRejectedException>(() => hand.Act(1, (BettingAction)99));

        Assert.Equal(RejectionReason.IllegalAction, check.Reason);
        Assert.Equal(RejectionReason.IllegalAction, tooBig.Reason);
        Assert.Equal(RejectionReason.IllegalAction, unknown.Reason);
        Assert.Equal(1, hand.ActingSeat);
    }

    [Fact]
    public void ActingWithoutAnOpenRoundIsAStageRejection()
    {
        var hand = NewHand(new[] { 100, 100 });
        hand.CollectAntes();

        var error = Assert.Throws<RuleRejectedException>(() => hand.Act(0, BettingAction.Check));

        // 两轮之间来的下注是正常的状态竞争，以业务拒绝回应，而不是当成服务端缺陷。
        Assert.Equal(RejectionReason.StageNotAllowed, error.Reason);
    }

    [Fact]
    public void ActionOrderFollowsParticipantOrderAndSkipsFoldedPlayersInLaterRounds()
    {
        var hand = new Hand(5, Deck.CreateStandard(), new[] { 3, 1, 4 }, 1, new[] { 0, 100, 0, 100, 100 });
        hand.CollectAntes();
        hand.StartBettingRound();

        Assert.Equal(3, hand.ActingSeat);
        hand.Act(3, BettingAction.Raise, 2);
        hand.Act(1, BettingAction.Fold);
        Assert.True(hand.Act(4, BettingAction.Call));

        hand.StartBettingRound();
        // 座位 1 已弃牌，第二轮从 3 开始后直接轮到 4。
        Assert.Equal(3, hand.ActingSeat);
        hand.Act(3, BettingAction.Check);
        Assert.Equal(4, hand.ActingSeat);
    }

    [Fact]
    public void ChipsAreConservedThroughAFullHandWithSidePots()
    {
        var stacks = new[] { 100, 8, 30, 100 };
        var hand = NewHand(stacks, ante: 2);
        hand.CollectAntes();

        hand.StartBettingRound();
        hand.Act(0, BettingAction.Raise, 4);
        hand.Act(1, BettingAction.AllIn);            // 剩 6，全下到 6
        hand.Act(2, BettingAction.Call);
        hand.Act(3, BettingAction.Raise, 20);
        hand.Act(0, BettingAction.Call);
        hand.Act(2, BettingAction.AllIn);            // 剩 22，全下到 28
        hand.Act(3, BettingAction.Call);
        Assert.True(hand.Act(0, BettingAction.Call));
        AssertConserved(hand, stacks);

        hand.StartBettingRound();
        hand.Act(0, BettingAction.Raise, 10);
        Assert.True(hand.Act(3, BettingAction.Fold));
        // 座位 3 弃牌后，座位 0 的 10 无人能跟，退回；座位 3 投入的 30 留在池里。
        Assert.Equal(new[] { 30, 8, 30, 30 }, hand.Contributions);
        AssertConserved(hand, stacks);

        hand.SkipReroute();
        Assert.False(hand.StartBettingRound());   // 只剩座位 0 有筹码
        Assert.Equal(HandStage.Showdown, hand.CurrentStage);

        var pots = PotLayering.SplitRegularPots(hand.Contributions, hand.FoldedSnapshot());
        Assert.Equal(hand.RegularPotTotal, PotLayering.TotalAmount(pots));
        Assert.Equal(98, hand.RegularPotTotal);
    }

    [Fact]
    public void ContributionsAreALiveView()
    {
        var hand = NewHand(new[] { 100, 100, 100 }, ante: 2);
        var view = hand.Contributions;

        hand.CollectAntes();

        // 读取属性时不再分配新集合，拿到的是同一个活视图，会反映随后的变化。
        Assert.Same(view, hand.Contributions);
        Assert.Equal(new[] { 2, 2, 2 }, view);
        Assert.False(view is int[]);
    }

    /// <summary>当前一轮所有人依次过牌，直到本轮结束。</summary>
    private static void CheckAround(Hand hand)
    {
        while (hand.IsBetting)
        {
            hand.Act(hand.ActingSeat, BettingAction.Check);
        }
    }
}
