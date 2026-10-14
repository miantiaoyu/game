using DarkTide.Rules;

namespace DarkTide.Room.Tests;

public class RoomTests
{
    private static readonly Guid alice = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid bob = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid carol = Guid.Parse("00000000-0000-0000-0000-000000000003");

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void SeatCapacityIsSeparateFromSeatedCount(int capacity)
    {
        var currentRoom = new Room(Guid.NewGuid(), capacity);
        currentRoom.TakeSeat(alice, capacity - 1);
        currentRoom.TakeSeat(bob, 0);
        currentRoom.LeaveSeat(bob);

        Assert.Equal(capacity, currentRoom.SeatCount);
        Assert.Equal(1, currentRoom.SeatedCount);
        Assert.Equal<int?>(capacity - 1, currentRoom.FindSeat(alice));
        Assert.Null(currentRoom.FindSeat(bob));
        currentRoom.TakeSeat(carol, 0);
        Assert.Equal<int?>(0, currentRoom.FindSeat(carol));
    }

    [Fact]
    public void SeatAndDuplicateIdentityConflictsAreRejected()
    {
        var currentRoom = new Room(Guid.NewGuid(), 8);
        currentRoom.TakeSeat(alice, 6);

        AssertRejected(RejectionReason.SeatTaken, () => currentRoom.TakeSeat(bob, 6));
        AssertRejected(RejectionReason.AlreadySeated, () => currentRoom.TakeSeat(alice, 0));
        AssertRejected(RejectionReason.AlreadySeated, () => currentRoom.TakeSeat(alice, 6));
        Assert.Equal(1, currentRoom.SeatedCount);
        Assert.Equal<int?>(6, currentRoom.FindSeat(alice));
        Assert.Null(currentRoom.FindSeat(bob));
    }

    [Fact]
    public void PlayerIdMapsToSeatAndOwnHoleCards()
    {
        var currentRoom = NewThreeSeatRoom();
        currentRoom.Deal(Deck.CreateStandard(), new[] { 6, 0, 3 });

        var aliceView = currentRoom.CreatePlayerView(alice);
        var bobView = currentRoom.CreatePlayerView(bob);
        Assert.Equal(6, aliceView.Seat);
        Assert.Equal(0, bobView.Seat);
        Assert.Equal(new[] { new Card(Suit.Red, 1), new Card(Suit.Red, 4) }, aliceView.HoleCards);
        Assert.Equal(new[] { new Card(Suit.Red, 2), new Card(Suit.Red, 5) }, bobView.HoleCards);
        Assert.Equal(7, aliceView.OtherSeats.Count);
    }

    [Fact]
    public void SamePlayerIdMapsToSameSeat()
    {
        var currentRoom = NewThreeSeatRoom();
        currentRoom.Deal(Deck.CreateStandard(), new[] { 6, 0, 3 });
        var reparsedId = Guid.Parse(alice.ToString());

        Assert.Equal<int?>(6, currentRoom.FindSeat(reparsedId));
        Assert.Equal(currentRoom.CreatePlayerView(alice).HoleCards.ToArray(),
            currentRoom.CreatePlayerView(reparsedId).HoleCards.ToArray());
    }

    [Fact]
    public void SeatedButNotParticipatingPlayerGetsNoHoleCards()
    {
        var currentRoom = NewThreeSeatRoom();
        currentRoom.Deal(Deck.CreateStandard(), new[] { 6, 0 });

        var view = currentRoom.CreatePlayerView(carol);
        Assert.Equal(3, currentRoom.SeatedCount);
        Assert.Equal(3, view.Seat);
        Assert.Empty(view.HoleCards);
        Assert.Equal(2, view.OtherSeats.Count(overview => overview.CardCount == 2));
    }

    [Fact]
    public void NonSeatedAndDepartedPlayersCannotView()
    {
        var currentRoom = NewThreeSeatRoom();
        currentRoom.LeaveSeat(carol);
        currentRoom.Deal(Deck.CreateStandard(), new[] { 6, 0 });

        AssertRejected(RejectionReason.NotSeated, () => currentRoom.CreatePlayerView(carol));
        AssertRejected(RejectionReason.NotSeated, () => currentRoom.CreatePlayerView(Guid.NewGuid()));
        Assert.Equal<int?>(6, currentRoom.FindSeat(alice));
    }

    [Fact]
    public void RoomMembershipsAreIsolated()
    {
        var firstRoom = new Room(Guid.NewGuid(), 8);
        var secondRoom = new Room(Guid.NewGuid(), 8);
        firstRoom.TakeSeat(alice, 0);
        firstRoom.TakeSeat(bob, 1);
        secondRoom.TakeSeat(carol, 0);
        firstRoom.Deal(Deck.CreateStandard(), new[] { 0, 1 });

        Assert.Null(secondRoom.FindSeat(alice));
        AssertRejected(RejectionReason.NotSeated, () => firstRoom.CreatePlayerView(carol));
        Assert.Equal<int?>(0, secondRoom.FindSeat(carol));
    }

    [Fact]
    public void NoHoleCardViewBeforeDealing()
    {
        var currentRoom = NewThreeSeatRoom();

        Assert.Equal(RoomStage.WaitingForSeats, currentRoom.Stage);
        AssertRejected(RejectionReason.StageNotAllowed, () => currentRoom.CreatePlayerView(alice));
    }

    [Fact]
    public void AfterDealingMemberChangesAndRedealAreRejected()
    {
        var currentRoom = NewThreeSeatRoom();
        currentRoom.Deal(Deck.CreateStandard(), new[] { 6, 0, 3 });
        var originalHoleCards = currentRoom.CreatePlayerView(alice).HoleCards.ToArray();

        AssertRejected(RejectionReason.StageNotAllowed, () => currentRoom.LeaveSeat(alice));
        AssertRejected(RejectionReason.StageNotAllowed, () => currentRoom.TakeSeat(Guid.NewGuid(), 7));
        AssertRejected(RejectionReason.StageNotAllowed, () => currentRoom.Deal(
            Deck.CreateStandard().Reverse().ToArray(), new[] { 6, 0, 3 }));
        Assert.Equal(3, currentRoom.SeatedCount);
        Assert.Equal(originalHoleCards, currentRoom.CreatePlayerView(alice).HoleCards.ToArray());
    }

    [Fact]
    public void OnlySuccessfulChangesAdvanceStateVersion()
    {
        var currentRoom = new Room(Guid.NewGuid(), 8);
        Assert.Equal(0, currentRoom.StateVersion);

        currentRoom.TakeSeat(alice, 6);
        currentRoom.TakeSeat(bob, 0);
        Assert.Equal(2, currentRoom.StateVersion);

        // 被拒绝的修改与查询都不改变版本。
        AssertRejected(RejectionReason.SeatTaken, () => currentRoom.TakeSeat(carol, 6));
        Assert.Throws<ArgumentException>(() => currentRoom.Deal(Deck.CreateStandard(), new[] { 6, 6 }));
        _ = currentRoom.FindSeat(alice);
        Assert.Equal(2, currentRoom.StateVersion);

        currentRoom.LeaveSeat(bob);
        currentRoom.TakeSeat(bob, 0);
        currentRoom.Deal(Deck.CreateStandard(), new[] { 6, 0 });
        _ = currentRoom.CreatePlayerView(alice);
        Assert.Equal(5, currentRoom.StateVersion);
        Assert.Equal(RoomStage.CardsDealt, currentRoom.Stage);
    }

    [Fact]
    public void EmptySeatCannotBeDealtAndRoomStaysUsable()
    {
        var currentRoom = NewThreeSeatRoom();

        AssertRejected(RejectionReason.EmptySeatInLineup, () => currentRoom.Deal(Deck.CreateStandard(), new[] { 6, 7 }));
        Assert.Equal(RoomStage.WaitingForSeats, currentRoom.Stage);
        currentRoom.Deal(Deck.CreateStandard(), new[] { 6, 0 });
        Assert.Equal(RoomStage.CardsDealt, currentRoom.Stage);
        Assert.Equal(new Card(Suit.Red, 1), currentRoom.CreatePlayerView(alice).HoleCards[0]);
    }

    [Fact]
    public void InvalidLineupOrDeckLeavesRoomUndealt()
    {
        var currentRoom = NewThreeSeatRoom();
        int[][] invalidLineups = { Array.Empty<int>(), new[] { 6 }, new[] { 6, 6 }, new[] { 6, -1 }, new[] { 6, 8 } };
        foreach (var lineup in invalidLineups)
        {
            Assert.Throws<ArgumentException>(() => currentRoom.Deal(Deck.CreateStandard(), lineup));
            Assert.Equal(RoomStage.WaitingForSeats, currentRoom.Stage);
        }
        Assert.Throws<ArgumentException>(() => currentRoom.Deal(Array.Empty<Card>(), new[] { 6, 0 }));
        Assert.Throws<ArgumentNullException>(() => currentRoom.Deal(null!, new[] { 6, 0 }));
        Assert.Throws<ArgumentNullException>(() => currentRoom.Deal(Deck.CreateStandard(), null!));
        Assert.Equal(RoomStage.WaitingForSeats, currentRoom.Stage);
        currentRoom.LeaveSeat(carol);
        currentRoom.Deal(Deck.CreateStandard(), new[] { 6, 0 });
        Assert.Equal(2, currentRoom.SeatedCount);
    }

    [Fact]
    public void EmptyPlayerIdAndLeaveByNonSeatedAreRejected()
    {
        var currentRoom = NewThreeSeatRoom();

        Assert.Throws<ArgumentException>(() => currentRoom.TakeSeat(Guid.Empty, 7));
        Assert.Throws<ArgumentException>(() => currentRoom.FindSeat(Guid.Empty));
        Assert.Throws<ArgumentException>(() => currentRoom.LeaveSeat(Guid.Empty));
        Assert.Throws<ArgumentException>(() => currentRoom.CreatePlayerView(Guid.Empty));
        AssertRejected(RejectionReason.NotSeated, () => currentRoom.LeaveSeat(Guid.NewGuid()));
        Assert.Equal(3, currentRoom.SeatedCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void OutOfRangeSeatIsRejected(int seat)
    {
        var currentRoom = new Room(Guid.NewGuid(), 8);

        AssertRejected(RejectionReason.SeatOutOfRange, () => currentRoom.TakeSeat(alice, seat));
        Assert.Equal(0, currentRoom.SeatedCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void OutOfRangeCapacityIsRejected(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Room(Guid.NewGuid(), capacity));
    }

    [Fact]
    public void EmptyRoomIdIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new Room(Guid.Empty, 8));
    }

    private static void AssertRejected(RejectionReason expectedReason, Action action)
    {
        var error = Assert.Throws<RuleRejectedException>(action);
        Assert.Equal(expectedReason, error.Reason);
    }

    private static Room NewThreeSeatRoom()
    {
        var currentRoom = new Room(Guid.NewGuid(), 8);
        currentRoom.TakeSeat(alice, 6);
        currentRoom.TakeSeat(bob, 0);
        currentRoom.TakeSeat(carol, 3);
        return currentRoom;
    }
}
