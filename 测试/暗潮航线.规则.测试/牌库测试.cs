using 暗潮航线.规则;

namespace 暗潮航线.规则.测试;

public class 牌库测试
{
    [Fact]
    public void 标准牌库完整且没有重复牌()
    {
        var 全部牌 = 牌库.创建标准牌库();

        Assert.Equal(27, 全部牌.Count);
        Assert.Equal(27, 全部牌.Distinct().Count());

        foreach (var 当前花色 in Enum.GetValues<花色>())
        {
            Assert.Equal(9, 全部牌.Count(当前牌 => 当前牌.花色 == 当前花色));
        }

        foreach (var 当前点数 in Enumerable.Range(1, 9))
        {
            Assert.Equal(3, 全部牌.Count(当前牌 => 当前牌.点数 == 当前点数));
        }
    }
}
