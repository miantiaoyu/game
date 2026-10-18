using DarkTide.Rules;

namespace DarkTide.Rules.Tests;

public class RerouteWindowTests
{
    [Theory]
    // 规则 5.2 的例子：底注 1，五人 E 都是 40 → 2、3、3、4、4；E 都是 80 → 4、5、6、7、8。
    [InlineData(40, new[] { 2, 3, 3, 4, 4 })]
    [InlineData(80, new[] { 4, 5, 6, 7, 8 })]
    public void RuleExampleFeesForFivePlayers(int exposure, int[] expected)
    {
        var fees = Enumerable.Range(1, 5).Select(position => RerouteWindow.Quote(exposure, position, 5, ante: 1));
        Assert.Equal(expected, fees);
    }

    [Fact]
    public void RatesFollowTheRuleTableExactly()
    {
        // E = 1200 时各比例都乘得出整数，可以直接对照规则表：4 人 5%、6⅔%、8⅓%、10%。
        Assert.Equal(new[] { 60, 80, 100, 120 }, Enumerable.Range(1, 4).Select(position => RerouteWindow.Quote(1200, position, 4, 1)));
        Assert.Equal(new[] { 60, 90, 120 }, Enumerable.Range(1, 3).Select(position => RerouteWindow.Quote(1200, position, 3, 1)));
        Assert.Equal(new[] { 60, 120 }, Enumerable.Range(1, 2).Select(position => RerouteWindow.Quote(1200, position, 2, 1)));
        // 只有一人可操作时取 10%。
        Assert.Equal(120, RerouteWindow.Quote(1200, 1, 1, 1));
    }

    [Fact]
    public void FeeRoundsUpOnTheExactRatioAndNeverFallsBelowTheAnte()
    {
        // 4 人第 2 位是 6⅔%：E=10 时精确值 0.666…，向上取整为 1；不能先把比例四舍五入成 6.67%。
        Assert.Equal(1, RerouteWindow.Quote(10, 2, 4, ante: 1));
        Assert.Equal(7, RerouteWindow.Quote(100, 2, 4, ante: 1));
        // 底注 2 时最低收 2。
        Assert.Equal(2, RerouteWindow.Quote(10, 1, 5, ante: 2));
    }

    [Fact]
    public void RuleFiveThreeExampleListAndBonusEligibility()
    {
        // 规则 5.3：A、B 普通投入各 40 且仍有余额，C 投入 10 后全下。下注顺序 A→B→C，改航顺序 B→A。
        // 主池 30（A/B/C），边池 60（A/B）。B 的 E = 90，报价 90×5% 向上取整 = 5；附加池只有 A/B 能赢。
        const int A = 0, B = 1, C = 2;
        var quotes = RerouteWindow.Open(
            contributions: [40, 40, 10],
            folded: [false, false, false],
            stacks: [60, 60, 0],
            bettingOrder: [A, B, C],
            ante: 1);

        Assert.Equal(new[] { B, A }, quotes.Select(quote => quote.Seat));
        Assert.Equal(90, quotes[0].Exposure);
        Assert.Equal(5, quotes[0].Fee);
        Assert.Equal(9, quotes[1].Fee);              // 第 2 位、共 2 人：10%
        Assert.Equal(new[] { A, B }, quotes[0].BonusEligibleSeats);
    }

    [Fact]
    public void FoldedAndAllInPlayersAreNotOnTheList()
    {
        var quotes = RerouteWindow.Open(
            contributions: [20, 20, 20, 5],
            folded: [false, true, false, false],
            stacks: [50, 50, 50, 0],
            bettingOrder: [3, 0, 1, 2],
            ante: 1);

        // 下注顺序 3→0→1→2，反过来是 2→1→0→3；座位 1 弃牌、座位 3 全下，都不占顺位。
        Assert.Equal(new[] { 2, 0 }, quotes.Select(quote => quote.Seat));
        Assert.Equal(new[] { 1, 2 }, quotes.Select(quote => quote.Position));
    }

    [Fact]
    public void ExposureCountsOnlyContestedPotsTheSeatCanWin()
    {
        // 座位 0、1 投入 40 且有余额，座位 2 弃牌在 40，座位 3 全下在 10。
        // 主池 40（0/1/3 争），边池 90（0/1 争，座位 2 的钱留在池里）。两人的 E 都是 130。
        var quotes = RerouteWindow.Open(
            contributions: [40, 40, 40, 10],
            folded: [false, false, true, false],
            stacks: [10, 10, 10, 0],
            bettingOrder: [0, 1, 2, 3],
            ante: 1);

        Assert.All(quotes, quote => Assert.Equal(130, quote.Exposure));
        Assert.All(quotes, quote => Assert.Equal(new[] { 0, 1 }, quote.BonusEligibleSeats));
    }

    [Fact]
    public void SoleChipHolderStillGetsAQuoteAgainstAllInOpponents()
    {
        // 规则 5.2 末段：只有一人尚有余额、其他人均全下时，只要仍有共同争夺的池，仍可按 10% 报价改航。
        // 座位 0 的超额已退回到 30，座位 2 弃牌在 30：主池 30（0/1 争），上一层 40（只有座位 0 有资格，独得）。
        var quotes = RerouteWindow.Open(
            contributions: [30, 10, 30],
            folded: [false, false, true],
            stacks: [70, 0, 70],
            bettingOrder: [0, 1, 2],
            ante: 1);

        var only = Assert.Single(quotes);
        Assert.Equal(0, only.Seat);
        Assert.Equal(30, only.Exposure);             // 独得的那层不计入 E
        Assert.Equal(3, only.Fee);
        Assert.Equal(new[] { 0, 1 }, only.BonusEligibleSeats);
    }
}
