using DarkTide.Rules;

namespace DarkTide.Rules.Tests;

public class PotAwardTests
{
    private static Pot P(int amount, params int[] eligible) => new(new Chips(amount), eligible);

    private static HandValue V(params Card[] three) => HandEvaluator.EvaluateThree(three[0], three[1], three[2]);

    private static readonly HandValue Trips = V(new(Suit.Red, 2), new(Suit.Blue, 2), new(Suit.Green, 2));
    private static readonly HandValue Pair = V(new(Suit.Red, 9), new(Suit.Blue, 9), new(Suit.Green, 1));
    private static readonly HandValue High = V(new(Suit.Red, 9), new(Suit.Blue, 5), new(Suit.Green, 1));

    private static int[] Awards(IReadOnlyList<PotResult> results, int seatCount)
    {
        var awards = new int[seatCount];
        foreach (var result in results)
        for (int i = 0; i < result.Winners.Count; i++)
        {
            awards[result.Winners[i]] += result.Shares[i];
        }
        return awards;
    }

    [Fact]
    public void RuleSixExampleShortAllInWinsOnlyTheMainPot()
    {
        // 规则第 6 节：A 全下 10，B、C 各 30。主池 30（A/B/C），边池 40（B/C）。A 牌最强，也只能拿主池。
        var pots = new[] { P(30, 0, 1, 2), P(40, 1, 2) };
        var values = new HandValue?[] { Trips, Pair, High };

        var results = PotAward.Distribute(pots, values, new[] { 0, 1, 2 });

        Assert.Equal(new[] { 0 }, results[0].Winners);
        Assert.Equal(new[] { 1 }, results[1].Winners);
        Assert.Equal(new[] { 30, 40, 0 }, Awards(results, 3));
    }

    [Fact]
    public void SingleEligibleSeatTakesThePotWithoutComparing()
    {
        // 只有一人有资格时不需要牌力，可以不提供（例如别人都弃牌了，他的牌不用公开）。
        var results = PotAward.Distribute(new[] { P(12, 2) }, new HandValue?[3], new[] { 0, 1, 2 });

        Assert.Equal(new[] { 2 }, results[0].Winners);
        Assert.Equal(new[] { 12 }, results[0].Shares);
    }

    [Fact]
    public void OddChipsGoInBettingOrderStartingFromTheStartingSeat()
    {
        // 座位 1、3、4 并列，池 8：每人 2，余 2。下注顺序从座位 3 开始 → 3、4、1，余数给 3 和 4。
        var values = new HandValue?[] { null, Pair, null, Pair, Pair };
        var results = PotAward.Distribute(new[] { P(8, 1, 3, 4) }, values, new[] { 3, 4, 0, 1, 2 });

        Assert.Equal(new[] { 3, 4, 1 }, results[0].Winners);
        Assert.Equal(new[] { 3, 3, 2 }, results[0].Shares);
    }

    [Fact]
    public void OnlyTiedWinnersShareNotEveryEligibleSeat()
    {
        var values = new HandValue?[] { Pair, High, Pair };
        var results = PotAward.Distribute(new[] { P(9, 0, 1, 2) }, values, new[] { 1, 2, 0 });

        // 座位 1 输了，不参与余数分配；余数从起始位（座位 1）往后找，先到座位 2。
        Assert.Equal(new[] { 2, 0 }, results[0].Winners);
        Assert.Equal(new[] { 5, 4 }, results[0].Shares);
    }

    [Fact]
    public void EachPotIsComparedOnlyAmongItsOwnEligibleSeats()
    {
        // 座位 0 的牌最强但弃牌了，不在任何资格名单里；它的钱留在池里给别人赢。
        var pots = new[] { P(15, 1, 2), P(6, 2) };
        var values = new HandValue?[] { Trips, Pair, High };

        var results = PotAward.Distribute(pots, values, new[] { 0, 1, 2 });

        Assert.Equal(new[] { 0, 15, 6 }, Awards(results, 3));
    }

    [Fact]
    public void TotalAwardedEqualsTotalOfAllPots()
    {
        var random = new Random(7);
        for (int round = 0; round < 2000; round++)
        {
            int seats = random.Next(2, 9);
            var values = Enumerable.Range(0, seats).Select(_ => (HandValue?)(random.Next(3) switch { 0 => Trips, 1 => Pair, _ => High })).ToArray();
            var pots = Enumerable.Range(0, random.Next(1, 4)).Select(_ =>
            {
                var eligible = Enumerable.Range(0, seats).Where(_ => random.Next(2) == 0).ToArray();
                return P(random.Next(1, 200), eligible.Length == 0 ? new[] { 0 } : eligible);
            }).ToArray();
            var order = Enumerable.Range(0, seats).OrderBy(_ => random.Next()).ToArray();

            var results = PotAward.Distribute(pots, values, order);

            Assert.Equal(pots.Sum(pot => pot.Total.Amount), Awards(results, seats).Sum());
            // 并列者之间最多差一枚。
            Assert.All(results, result => Assert.InRange(result.Shares.Max() - result.Shares.Min(), 0, 1));
        }
    }

    [Fact]
    public void BrokenInputsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => PotAward.Distribute(new[] { P(5) }, new HandValue?[2], new[] { 0, 1 }));
        Assert.Throws<ArgumentException>(() => PotAward.Distribute(new[] { P(5, 0, 1) }, new HandValue?[] { Pair, null }, new[] { 0, 1 }));
        Assert.Throws<ArgumentException>(() => PotAward.Distribute(new[] { P(5, 0, 2) }, new HandValue?[] { Pair, Pair, Pair }, new[] { 0, 1 }));
    }
}
