using System.Text.Json;
using DarkTide.Rules;

namespace DarkTide.Rules.Tests;

public class PlayerHandViewTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void OtherSeatsShowOnlySeatAndCardCount(int Seat)
    {
        var hand = TestHands.Deal(5, Deck.CreateStandard(), new[] { 0, 1, 2, 3, 4 });

        var view = hand.CreatePlayerView(Seat);

        Assert.Equal(2, view.HoleCards.Count);
        Assert.Equal(Enumerable.Range(0, 5).Where(seat => seat != Seat), view.OtherSeats.Select(overview => overview.seat));
        Assert.All(view.OtherSeats, overview => Assert.Equal(2, overview.CardCount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void ChangingOpponentCardsDoesNotChangeOwnView(int Seat)
    {
        var originalOrder = Deck.CreateStandard().ToArray();
        var modifiedOrder = originalOrder.ToArray();

        int[] participantOrder = { 6, 0, 3 };
        int dealPosition = Array.IndexOf(participantOrder, Seat);
        int[] holeCardPosition = dealPosition >= 0
            ? new[] { dealPosition, dealPosition + participantOrder.Length }
            : Array.Empty<int>();

        // 参与者只保留自己的两张牌；未参与座位没有任何可见牌。
        // 把其余所有牌循环移位，仍然是一副合法牌。
        var hiddenPosition = Enumerable.Range(0, 27)
            .Except(holeCardPosition).ToArray();
        for (int index = 0; index < hiddenPosition.Length; index++)
        {
            modifiedOrder[hiddenPosition[index]] = originalOrder[hiddenPosition[(index + 1) % hiddenPosition.Length]];
        }

        var original = TestHands.Deal(8, originalOrder, participantOrder);
        var modified = TestHands.Deal(8, modifiedOrder, participantOrder);

        // 序列化只用于检查整份输出的数据边界，这里尚未定义网络协议。
        Assert.Equal(
            JsonSerializer.Serialize(original.CreatePlayerView(Seat)),
            JsonSerializer.Serialize(modified.CreatePlayerView(Seat)));
    }

    [Fact]
    public void LookingAtOtherSeatsDoesNotAdvanceDeck()
    {
        var hand = TestHands.Deal(5, Deck.CreateStandard(), new[] { 0, 1, 2, 3, 4 });
        var existingView = hand.CreatePlayerView(0);
        string originalResult = JsonSerializer.Serialize(existingView);

        for (int seat = 0; seat < 5; seat++)
        {
            _ = hand.CreatePlayerView(seat);
        }

        Assert.Equal(originalResult, JsonSerializer.Serialize(existingView));
        Assert.Equal(originalResult, JsonSerializer.Serialize(hand.CreatePlayerView(0)));
        Assert.Equal(17, hand.RemainingCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void ViewingAnInvalidSeatIsRejected(int invalidSeat)
    {
        var hand = TestHands.Deal(5, Deck.CreateStandard(), new[] { 0, 1, 2, 3, 4 });

        Assert.Throws<ArgumentOutOfRangeException>(() => hand.CreatePlayerView(invalidSeat));
        Assert.Equal(17, hand.RemainingCount);
    }
}
