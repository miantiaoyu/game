using 暗潮航线.规则;

namespace 暗潮航线.规则.测试;

public class 牌测试
{
    [Theory]
    [InlineData(花色.红, 1, 1)]
    [InlineData(花色.红, 7, 7)]
    [InlineData(花色.红, 9, 9)]
    [InlineData(花色.蓝, 1, 10)]
    [InlineData(花色.绿, 9, 27)]
    public void 合法牌可以还原花色点数和编号(花色 预期花色, int 预期点数, int 预期编号)
    {
        var 当前牌 = new 牌(预期花色, 预期点数);

        Assert.Equal(预期花色, 当前牌.花色);
        Assert.Equal(预期点数, 当前牌.点数);
        Assert.Equal((byte)预期编号, 当前牌.编号);
        Assert.True(当前牌.是否有效);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(int.MaxValue)]
    public void 拒绝范围外的点数(int 非法点数)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new 牌(花色.红, 非法点数));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(255)]
    public void 拒绝未定义的花色(int 非法花色)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new 牌((花色)非法花色, 1));
    }

    [Fact]
    public void 相同花色和点数的牌按值相等()
    {
        Assert.Equal(new 牌(花色.红, 7), new 牌(花色.红, 7));
        Assert.NotEqual(new 牌(花色.红, 7), new 牌(花色.蓝, 7));
        Assert.NotEqual(new 牌(花色.红, 7), new 牌(花色.红, 8));
    }

    [Fact]
    public void 默认值是未初始化的牌且不冒充任何真牌()
    {
        var 未初始化 = default(牌);

        Assert.False(未初始化.是否有效);
        Assert.DoesNotContain(未初始化, 牌库.创建标准牌库());
        Assert.Throws<InvalidOperationException>(() => 未初始化.花色);
        Assert.Throws<InvalidOperationException>(() => 未初始化.点数);
        Assert.Equal("未初始化的牌", 未初始化.ToString());
    }
}
