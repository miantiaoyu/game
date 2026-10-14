using 暗潮航线.规则;

namespace 暗潮航线.规则.测试;

public class 整场测试
{
    [Fact]
    public void 初始每人一百枚且尚未开局()
    {
        var 比赛 = new 整场(5);

        Assert.Equal(5, 比赛.座位总数);
        Assert.Equal(0, 比赛.当前局号);
        Assert.False(比赛.全部局数已完成);
        for (int 座位 = 0; 座位 < 5; 座位++)
        {
            Assert.Equal(100, 比赛.查看筹码(座位).数量);
        }
        Assert.Throws<InvalidOperationException>(() => 比赛.计算底注());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(6, 1)]
    [InlineData(7, 2)]
    [InlineData(12, 2)]
    public void 前六局底注一后六局底注二(int 局号, int 预期底注)
    {
        var 比赛 = 新建(局数: 局号);

        Assert.Equal(局号, 比赛.当前局号);
        Assert.Equal(预期底注, 比赛.计算底注().数量);
    }

    [Fact]
    public void 交完底注不代表被淘汰()
    {
        var 比赛 = new 整场(3);
        比赛.开始下一局();
        比赛.收取底注(new[] { 0, 1, 2 });

        // 交底注只是少了一枚筹码，不构成淘汰；只有结算后为零才算。
        Assert.Equal(99, 比赛.查看筹码(0).数量);
        Assert.Empty(比赛.找出被淘汰座位());
        Assert.False(比赛.已可判定胜负);
        Assert.Equal(3, 比赛.统计仍有筹码人数());
    }

    [Fact]
    public void 只剩一人有筹码时提前结束()
    {
        var 比赛 = new 整场(3, 每人初始筹码: 0);
        比赛.增加筹码(2, new 筹码(7));

        Assert.True(比赛.已可判定胜负);
        Assert.Equal(new[] { 0, 1 }, 比赛.找出被淘汰座位());
        Assert.Equal(1, 比赛.统计仍有筹码人数());
        Assert.Equal(7, 比赛.查看筹码(2).数量);
    }

    [Fact]
    public void 局间筹码保留且不重置()
    {
        var 比赛 = new 整场(3);
        比赛.开始下一局();
        比赛.收取底注(new[] { 0, 1, 2 });
        比赛.增加筹码(1, new 筹码(3));

        Assert.Equal(99, 比赛.查看筹码(0).数量);
        Assert.Equal(102, 比赛.查看筹码(1).数量);

        比赛.开始下一局();
        比赛.收取底注(new[] { 0, 1, 2 });

        Assert.Equal(98, 比赛.查看筹码(0).数量);
        Assert.Equal(101, 比赛.查看筹码(1).数量);
    }

    [Fact]
    public void 筹码不足底注时全部交出而不是变负()
    {
        var 比赛 = 新建(局数: 7, 每人初始筹码: 1);

        // 第七局底注为 2，但每人只有 1 枚。
        比赛.收取底注(new[] { 0, 1 });

        Assert.Equal(0, 比赛.查看筹码(0).数量);
        Assert.Equal(0, 比赛.查看筹码(1).数量);
    }

    [Fact]
    public void 拒绝重复座位零筹码座位与越界座位()
    {
        var 比赛 = new 整场(3);
        比赛.开始下一局();

        Assert.Throws<ArgumentException>(() => 比赛.收取底注(new[] { 0, 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => 比赛.收取底注(new[] { 0, 3 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => 比赛.查看筹码(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => 比赛.查看筹码(3));

        var 空座位比赛 = new 整场(3, 每人初始筹码: 0);
        空座位比赛.开始下一局();
        Assert.Throws<ArgumentException>(() => 空座位比赛.收取底注(new[] { 0, 1 }));
    }

    [Fact]
    public void 完成全部局数后不能继续开局()
    {
        var 比赛 = 新建(局数: 12);

        Assert.True(比赛.全部局数已完成);
        Assert.Throws<InvalidOperationException>(() => 比赛.开始下一局());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void 拒绝不支持的座位数(int 座位数)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new 整场(座位数));
    }

    private static 整场 新建(int 局数 = 0, int 每人初始筹码 = 100)
    {
        var 比赛 = new 整场(5, 每人初始筹码);
        for (int 序号 = 0; 序号 < 局数; 序号++)
        {
            比赛.开始下一局();
        }
        return 比赛;
    }
}
