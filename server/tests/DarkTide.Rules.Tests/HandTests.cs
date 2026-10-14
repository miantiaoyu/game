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
        var hand = new Hand(totalSeats, drawOrder, participantOrder);
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
        var hand = new Hand(8, Deck.CreateStandard(), new[] { 6, 0, 3 });

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
        var hand = new Hand(5, Deck.CreateStandard(), new[] { 2, 3, 4, 0, 1 });

        var view = hand.CreatePlayerView(seat);

        Assert.Equal(seat, view.Seat);
        Assert.Equal(new[] { firstId, secondId }, view.HoleCards.Select(card => (int)card.Id));
    }

    [Fact]
    public void FivePlayersUseFirstTenCards()
    {
        var drawOrder = Deck.CreateStandard().Reverse().ToArray();
        var hand = new Hand(5, drawOrder, new[] { 0, 1, 2, 3, 4 });
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
        var hand = new Hand(5, inputDeck, inputSeats);

        Array.Reverse(inputDeck);
        Array.Reverse(inputSeats);
        _ = new Hand(5, inputDeck, inputSeats);

        Assert.Equal(new[] { new Card(Suit.Red, 1), new Card(Suit.Red, 6) }, hand.CreatePlayerView(0).HoleCards);
        Assert.Equal(17, hand.RemainingCount);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, hand.ParticipantOrder);
    }

    [Fact]
    public void ParticipantOrderIsReadOnlyAndKeepsGivenOrder()
    {
        var hand = new Hand(8, Deck.CreateStandard(), new[] { 6, 0, 3 });

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
            Assert.Throws<ArgumentException>(() => new Hand(5, Deck.CreateStandard(), order));
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
            new Hand(invalidCapacity, Deck.CreateStandard(), new[] { 0, 1 }));
    }

    [Fact]
    public void NullInputsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new Hand(5, null!, new[] { 0, 1, 2, 3, 4 }));
        Assert.Throws<ArgumentNullException>(() => new Hand(5, Deck.CreateStandard(), null!));
    }

    [Fact]
    public void IncompleteOrDuplicateDeckIsRejected()
    {
        var inputDeck = Deck.CreateStandard().ToArray();
        int[] order = { 0, 1, 2, 3, 4 };

        Assert.Throws<ArgumentException>(() => new Hand(5, inputDeck.Take(26).ToArray(), order));

        inputDeck[26] = inputDeck[0];
        Assert.Throws<ArgumentException>(() => new Hand(5, inputDeck, order));
    }

    // 以下为本步新增：底注、累计投入、公共牌、操作轮次。

    [Fact]
    public void NewHandAwaitsAnteWithNoContributionsOrCommunityCards()
    {
        var hand = new Hand(5, Deck.CreateStandard(), new[] { 0, 1, 2, 3, 4 });

        Assert.Equal(HandStage.Ante, hand.CurrentStage);
        Assert.Equal(0, hand.RegularPotTotal);
        Assert.All(Enumerable.Range(0, 5), seat => Assert.Equal(0, hand.Contributions[seat]));
        Assert.Empty(hand.CommunityCards);
        Assert.All(Enumerable.Range(0, 5), seat =>
        {
            Assert.False(hand.HasFolded(seat));
            Assert.False(hand.IsAllIn(seat));
        });
    }

    [Fact]
    public void CollectAntesRecordsContributionsAndReturnsPaidAmounts()
    {
        var hand = new Hand(5, Deck.CreateStandard(), new[] { 0, 1, 2, 3, 4 });
        var Chips = Enumerable.Repeat(100, 5).ToArray();

        var paid = hand.CollectAntes(1, Chips);

        Assert.All(Enumerable.Range(0, 5), seat => Assert.Equal(1, paid[seat]));
        Assert.Equal(5, hand.RegularPotTotal);
        // 收完底注即进入第一轮下注。
        Assert.Equal(HandStage.FirstBet, hand.CurrentStage);
        Assert.All(Enumerable.Range(0, 5), seat => Assert.False(hand.IsAllIn(seat)));
    }

    [Fact]
    public void ShortAntePaysEverythingAndCountsAsAllIn()
    {
        var hand = new Hand(3, Deck.CreateStandard(), new[] { 0, 1, 2 });
        var Chips = new[] { 100, 1, 100 };

        var paid = hand.CollectAntes(2, Chips);

        Assert.Equal(new[] { 2, 1, 2 }, paid);
        Assert.Equal(5, hand.RegularPotTotal);
        // 只交出 1 枚的座位等同全下，但仍发牌、不淘汰。
        Assert.True(hand.IsAllIn(1));
        Assert.Equal(2, hand.CreatePlayerView(1).HoleCards.Count);
    }

    [Fact]
    public void NonParticipantsPayNoAnte()
    {
        var hand = new Hand(5, Deck.CreateStandard(), new[] { 6 % 5, 0 });
        var Chips = Enumerable.Repeat(100, 5).ToArray();

        var paid = hand.CollectAntes(2, Chips);

        Assert.Equal(0, paid[2]);
        Assert.Equal(0, hand.Contributions[2]);
        Assert.Equal(4, hand.RegularPotTotal);
    }

    [Fact]
    public void CollectingAntesTwiceOrTooLateIsRejected()
    {
        var hand = new Hand(3, Deck.CreateStandard(), new[] { 0, 1, 2 });
        var Chips = Enumerable.Repeat(100, 3).ToArray();
        hand.CollectAntes(1, Chips);

        // 收完底注已进入一注轮，不能再收一次。
        Assert.Throws<InvalidOperationException>(() => hand.CollectAntes(1, Chips));
        Assert.Equal(3, hand.RegularPotTotal);
    }

    [Fact]
    public void InvalidAnteArgumentsLeaveStateUntouched()
    {
        var hand = new Hand(3, Deck.CreateStandard(), new[] { 0, 1, 2 });

        Assert.Throws<ArgumentOutOfRangeException>(() => hand.CollectAntes(-1, Enumerable.Repeat(100, 3).ToArray()));
        Assert.Throws<ArgumentException>(() => hand.CollectAntes(1, new[] { 100, 100 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => hand.CollectAntes(1, new[] { 100, -1, 100 }));

        Assert.Equal(0, hand.RegularPotTotal);
        Assert.Equal(HandStage.Ante, hand.CurrentStage);
    }

    [Fact]
    public void CommunityCardsAreRevealedInDeckOrder()
    {
        var drawOrder = Deck.CreateStandard();
        var hand = new Hand(5, drawOrder, new[] { 0, 1, 2, 3, 4 });

        // 五人各两张底牌用掉前十张，公共牌从第十一张开始。
        var first = hand.RevealCommunityCard();
        var second = hand.RevealCommunityCard();
        var third = hand.RevealCommunityCard();

        Assert.Equal(drawOrder[10], first);
        Assert.Equal(drawOrder[11], second);
        Assert.Equal(drawOrder[12], third);
        Assert.Equal(new[] { drawOrder[10], drawOrder[11], drawOrder[12] }, hand.CommunityCards);
        Assert.Equal(27 - 10 - 3, hand.RemainingCount);
    }

    [Fact]
    public void CannotRevealMoreThanThreeCommunityCards()
    {
        var hand = new Hand(2, Deck.CreateStandard(), new[] { 0, 1 });
        for (int times = 0; times < Hand.CommunityCardCount; times++)
        {
            hand.RevealCommunityCard();
        }

        Assert.Equal(Hand.CommunityCardCount, hand.CommunityCards.Count);
        Assert.Throws<InvalidOperationException>(() => hand.RevealCommunityCard());
    }

    [Fact]
    public void CommunityCardsAreReadOnly()
    {
        var hand = new Hand(2, Deck.CreateStandard(), new[] { 0, 1 });
        hand.RevealCommunityCard();

        Assert.False(hand.CommunityCards is Card[]);
        Assert.Single(hand.CommunityCards);
    }

    [Fact]
    public void HandStageOnlyAdvancesForward()
    {
        var hand = new Hand(3, Deck.CreateStandard(), new[] { 0, 1, 2 });

        Assert.Equal(HandStage.Ante, hand.CurrentStage);
        hand.AdvanceTo(HandStage.FirstBet);
        hand.AdvanceTo(HandStage.SecondBet);
        hand.AdvanceTo(HandStage.Reroute);
        hand.AdvanceTo(HandStage.ThirdBet);
        hand.AdvanceTo(HandStage.Showdown);

        Assert.Equal(HandStage.Showdown, hand.CurrentStage);
        Assert.Throws<InvalidOperationException>(() => hand.AdvanceTo(HandStage.SecondBet));
        Assert.Throws<InvalidOperationException>(() => hand.AdvanceTo(HandStage.Showdown));
    }

    [Fact]
    public void FoldAndAllInFlagsOnlyGrow()
    {
        var hand = new Hand(3, Deck.CreateStandard(), new[] { 0, 1, 2 });

        hand.RecordSeatState(1, Fold: true, AllIn: false);
        hand.RecordSeatState(2, Fold: false, AllIn: true);
        // 后续轮次传入相反的值也不会把状态改回去。
        hand.RecordSeatState(1, Fold: false, AllIn: false);

        Assert.True(hand.HasFolded(1));
        Assert.False(hand.HasFolded(2));
        Assert.True(hand.IsAllIn(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => hand.RecordSeatState(9, Fold: true, AllIn: false));
    }

    [Fact]
    public void ContributionsAreALiveView()
    {
        var hand = new Hand(3, Deck.CreateStandard(), new[] { 0, 1, 2 });
        var view = hand.Contributions;

        hand.CollectAntes(2, Enumerable.Repeat(100, 3).ToArray());

        // 读取属性时不再分配新集合，拿到的是同一个活视图，会反映随后的变化。
        Assert.Same(view, hand.Contributions);
        Assert.Equal(new[] { 2, 2, 2 }, view);
        Assert.False(view is int[]);
    }
}
