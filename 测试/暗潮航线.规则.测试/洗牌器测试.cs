using 暗潮航线.规则;

namespace 暗潮航线.规则.测试;

public class 洗牌器测试
{
    [Fact]
    public void 洗牌结果包含且仅包含完整标准牌库()
    {
        var 洗牌结果 = 洗牌器.洗标准牌库();
        var 标准牌库 = 牌库.创建标准牌库();

        Assert.Equal(27, 洗牌结果.Count);
        Assert.Equal(27, 洗牌结果.Distinct().Count());
        Assert.True(标准牌库.ToHashSet().SetEquals(洗牌结果));
    }

    [Fact]
    public void 洗牌结果可以按顺序抽完且没有重复牌()
    {
        var 洗牌结果 = 洗牌器.洗标准牌库();
        var 当前牌堆 = new 抽牌堆(洗牌结果);
        var 已抽出的牌 = new List<牌>();

        while (当前牌堆.抽下一张() is 牌 当前牌)
        {
            已抽出的牌.Add(当前牌);
        }

        Assert.Equal(洗牌结果.ToArray(), 已抽出的牌.ToArray());
        Assert.Equal(27, 已抽出的牌.Distinct().Count());
        Assert.Equal(0, 当前牌堆.剩余张数);
    }
}
