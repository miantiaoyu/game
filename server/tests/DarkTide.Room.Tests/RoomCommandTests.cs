using DarkTide.Rules;

namespace DarkTide.Room.Tests;

public class RoomCommandTests
{
    private static readonly Guid alice = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid bob = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public void ResendingSuccessfulCommandReturnsSameReceiptAndAppliesOnce()
    {
        var currentRoom = new Room(Guid.NewGuid(), 8);
        var command = new TakeSeatCommand(Guid.NewGuid(), alice, 3);

        var first = currentRoom.Execute(command);
        // 模拟回执丢失后客户端用同一编号、同一内容重发；新对象也算同一命令。
        var second = currentRoom.Execute(new TakeSeatCommand(command.CommandId, alice, 3));

        Assert.Equal(first, second);
        Assert.Equal(1, currentRoom.StateVersion);
        Assert.Equal(1, currentRoom.SeatedCount);
    }

    [Fact]
    public void ResendAfterVersionAdvancedReturnsOriginalSuccess()
    {
        var currentRoom = new Room(Guid.NewGuid(), 8);
        var command = new TakeSeatCommand(Guid.NewGuid(), alice, 3, expectedVersion: 0);

        var originalReceipt = currentRoom.Execute(command);
        currentRoom.Execute(new TakeSeatCommand(Guid.NewGuid(), bob, 4));

        Assert.Equal(originalReceipt, currentRoom.Execute(command));
    }

    [Fact]
    public void ResendingRejectedCommandReturnsSameRejection()
    {
        var currentRoom = new Room(Guid.NewGuid(), 8);
        currentRoom.Execute(new TakeSeatCommand(Guid.NewGuid(), alice, 3));
        var grabSeat = new TakeSeatCommand(Guid.NewGuid(), bob, 3);

        Assert.Equal(RejectionReason.SeatTaken, Assert.Throws<RuleRejectedException>(() => currentRoom.Execute(grabSeat)).Reason);
        currentRoom.Execute(new LeaveSeatCommand(Guid.NewGuid(), alice));

        // 座位已经空出来，但这是“旧点击”的重发，不能在玩家不知情时变成成功。
        Assert.Equal(RejectionReason.SeatTaken, Assert.Throws<RuleRejectedException>(() => currentRoom.Execute(grabSeat)).Reason);
        Assert.Null(currentRoom.FindSeat(bob));
    }

    [Fact]
    public void SameIdDifferentContentIsRejectedWithoutOverwriting()
    {
        var currentRoom = new Room(Guid.NewGuid(), 8);
        var Id = Guid.NewGuid();
        var originalReceipt = currentRoom.Execute(new TakeSeatCommand(Id, alice, 3));

        var conflict = Assert.Throws<RuleRejectedException>(() => currentRoom.Execute(new TakeSeatCommand(Id, alice, 4)));

        Assert.Equal(RejectionReason.CommandIdConflict, conflict.Reason);
        Assert.Equal(originalReceipt, currentRoom.Execute(new TakeSeatCommand(Id, alice, 3)));
        Assert.Equal<int?>(3, currentRoom.FindSeat(alice));
    }

    [Fact]
    public void StaleExpectedVersionIsRejectedWithoutStateChange()
    {
        var currentRoom = new Room(Guid.NewGuid(), 8);
        currentRoom.Execute(new TakeSeatCommand(Guid.NewGuid(), alice, 3, expectedVersion: 0));

        var stale = Assert.Throws<RuleRejectedException>(() =>
            currentRoom.Execute(new TakeSeatCommand(Guid.NewGuid(), bob, 4, expectedVersion: 0)));

        Assert.Equal(RejectionReason.VersionMismatch, stale.Reason);
        Assert.Equal(1, currentRoom.StateVersion);
        Assert.Null(currentRoom.FindSeat(bob));
        Assert.Equal(2, currentRoom.Execute(new TakeSeatCommand(Guid.NewGuid(), bob, 4, expectedVersion: 1)).CommittedVersion);
    }

    [Fact]
    public void DealCommandEqualityComparesContent()
    {
        var Id = Guid.NewGuid();
        var drawOrder = Deck.CreateStandard();

        var firstCopy = new DealCommand(Id, drawOrder, new[] { 0, 1 });
        var secondCopy = new DealCommand(Id, drawOrder.ToArray(), new[] { 0, 1 });
        var differentOrder = new DealCommand(Id, drawOrder, new[] { 1, 0 });

        Assert.Equal(firstCopy, secondCopy);
        Assert.Equal(firstCopy.GetHashCode(), secondCopy.GetHashCode());
        Assert.NotEqual(firstCopy, differentOrder);
    }

    [Fact]
    public void DealCommandCopiesInput()
    {
        var drawOrder = Deck.CreateStandard().ToArray();
        int[] order = { 6, 0 };
        var command = new DealCommand(Guid.NewGuid(), drawOrder, order);

        Array.Reverse(drawOrder);
        Array.Reverse(order);

        Assert.Equal(new[] { 6, 0 }, command.ParticipantOrder);
        Assert.Equal(new Card(Suit.Red, 1), command.DrawOrder[0]);
        Assert.False(command.DrawOrder is Card[]);
    }

    [Fact]
    public void CommandConstructorValidatesFormat()
    {
        Assert.Throws<ArgumentException>(() => new TakeSeatCommand(Guid.Empty, alice, 0));
        Assert.Throws<ArgumentException>(() => new TakeSeatCommand(Guid.NewGuid(), Guid.Empty, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LeaveSeatCommand(Guid.NewGuid(), alice, -1));
        Assert.Throws<ArgumentException>(() => new DealCommand(Guid.NewGuid(), new Card[28], new[] { 0, 1 }));
        Assert.Throws<ArgumentException>(() => new DealCommand(Guid.NewGuid(), Deck.CreateStandard(), new int[9]));
        Assert.Throws<ArgumentNullException>(() => new DealCommand(Guid.NewGuid(), null!, new[] { 0, 1 }));
    }

    [Fact]
    public void ReceiptStoreEvictsOldestWhenFull()
    {
        var store = new CommandReceiptStore(capacity: 2);
        var commands = Enumerable.Range(0, 3).Select(seat => new TakeSeatCommand(Guid.NewGuid(), alice, seat)).ToArray();

        foreach (var command in commands)
        {
            store.RecordSuccess(command, new CommandReceipt(command.CommandId, 1));
        }

        Assert.Equal(2, store.Count);
        Assert.False(store.TryGet(commands[0].CommandId, out _));
        Assert.True(store.TryGet(commands[1].CommandId, out _));
        Assert.True(store.TryGet(commands[2].CommandId, out _));
    }
}
