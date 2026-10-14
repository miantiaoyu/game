using DarkTide.Rules;

namespace DarkTide.Rules.Tests;

public class ChipsTests
{
    [Fact]
    public void DefaultIsZeroAndNegativeIsRejected()
    {
        Assert.Equal(0, default(Chips).Amount);
        Assert.Equal(0, Chips.Zero.Amount);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Chips(-1));
    }

    [Fact]
    public void ComparisonAndEqualityUseAmount()
    {
        Assert.True(new Chips(2) == new Chips(2));
        Assert.True(new Chips(2) != new Chips(3));
        Assert.True(new Chips(2) < new Chips(3));
        Assert.True(new Chips(3) >= new Chips(3));
    }

    [Fact]
    public void SubtractingMoreThanHeldThrows()
    {
        Assert.Equal(0, Chips.Subtract(new Chips(5), new Chips(5)).Amount);
        var error = Assert.Throws<InvalidOperationException>(() => Chips.Subtract(new Chips(5), new Chips(6)));
        Assert.Contains("筹码不足", error.Message);
    }

    [Fact]
    public void ScaleUpRoundsUp()
    {
        // 改航费例：底注 1、E 为 40 的五人报价 2、3、3、4、4。
        Assert.Equal(2, Chips.ScaleUp(new Chips(40), 50).Amount);
        Assert.Equal(3, Chips.ScaleUp(new Chips(40), 63).Amount);
        Assert.Equal(3, Chips.ScaleUp(new Chips(40), 75).Amount);
        Assert.Equal(4, Chips.ScaleUp(new Chips(40), 88).Amount);
        Assert.Equal(4, Chips.ScaleUp(new Chips(40), 100).Amount);

        // E 为 80 时对应 4、5、6、7、8。
        Assert.Equal(8, Chips.ScaleUp(new Chips(80), 100).Amount);

        // 基准为 0 时为 0；比例必须非负。
        Assert.Equal(0, Chips.ScaleUp(Chips.Zero, 50).Amount);
        Assert.Throws<ArgumentOutOfRangeException>(() => Chips.ScaleUp(new Chips(10), -1));
    }

    [Fact]
    public void LargeBasisDoesNotOverflow()
    {
        // 底池规模远小于此，但长整型中间结果保证不会因溢出回绕成负数。
        var result = Chips.ScaleUp(new Chips(1_000_000_000), 1000);
        Assert.Equal(1_000_000_000, result.Amount);
    }
}
