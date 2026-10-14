using 暗潮航线.规则;

namespace 暗潮航线.规则.测试;

public class 筹码测试
{
    [Fact]
    public void 默认值为零且拒绝负数()
    {
        Assert.Equal(0, default(筹码).数量);
        Assert.Equal(0, 筹码.零.数量);
        Assert.Throws<ArgumentOutOfRangeException>(() => new 筹码(-1));
    }

    [Fact]
    public void 比较与相等按数量()
    {
        Assert.True(new 筹码(2) == new 筹码(2));
        Assert.True(new 筹码(2) != new 筹码(3));
        Assert.True(new 筹码(2) < new 筹码(3));
        Assert.True(new 筹码(3) >= new 筹码(3));
    }

    [Fact]
    public void 扣减超过持有量时抛出而不是变负()
    {
        Assert.Equal(0, 筹码.减(new 筹码(5), new 筹码(5)).数量);
        var 错误 = Assert.Throws<InvalidOperationException>(() => 筹码.减(new 筹码(5), new 筹码(6)));
        Assert.Contains("筹码不足", 错误.Message);
    }

    [Fact]
    public void 向上取整比例()
    {
        // 改航费例：底注 1、E 为 40 的五人报价 2、3、3、4、4。
        Assert.Equal(2, 筹码.向上取整比例(new 筹码(40), 50).数量);
        Assert.Equal(3, 筹码.向上取整比例(new 筹码(40), 63).数量);
        Assert.Equal(3, 筹码.向上取整比例(new 筹码(40), 75).数量);
        Assert.Equal(4, 筹码.向上取整比例(new 筹码(40), 88).数量);
        Assert.Equal(4, 筹码.向上取整比例(new 筹码(40), 100).数量);

        // E 为 80 时对应 4、5、6、7、8。
        Assert.Equal(8, 筹码.向上取整比例(new 筹码(80), 100).数量);

        // 基准为 0 时为 0；比例必须非负。
        Assert.Equal(0, 筹码.向上取整比例(筹码.零, 50).数量);
        Assert.Throws<ArgumentOutOfRangeException>(() => 筹码.向上取整比例(new 筹码(10), -1));
    }

    [Fact]
    public void 大额基准相乘不溢出()
    {
        // 底池规模远小于此，但长整型中间结果保证不会因溢出回绕成负数。
        var 结果 = 筹码.向上取整比例(new 筹码(1_000_000_000), 1000);
        Assert.Equal(1_000_000_000, 结果.数量);
    }
}
