using 暗潮航线.规则;

namespace 暗潮航线.规则.测试;

public class 抽牌堆测试
{
    [Fact]
    public void 严格按照传入牌序抽牌并逐次减少剩余张数()
    {
        var 预期牌序 = 牌库.创建标准牌库().Reverse().ToArray();
        var 当前牌堆 = new 抽牌堆(预期牌序);

        Assert.Equal(27, 当前牌堆.剩余张数);
        for (int 位置 = 0; 位置 < 预期牌序.Length; 位置++)
        {
            Assert.Equal<牌?>(预期牌序[位置], 当前牌堆.抽下一张());
            Assert.Equal(26 - 位置, 当前牌堆.剩余张数);
        }
    }

    [Fact]
    public void 抽完后重复抽牌仍然返回空且剩余数量不变()
    {
        var 当前牌堆 = new 抽牌堆(牌库.创建标准牌库());
        for (int 次数 = 0; 次数 < 27; 次数++)
        {
            Assert.NotNull(当前牌堆.抽下一张());
        }

        for (int 次数 = 0; 次数 < 3; 次数++)
        {
            Assert.Null(当前牌堆.抽下一张());
            Assert.Equal(0, 当前牌堆.剩余张数);
        }
    }

    [Fact]
    public void 修改原始数组不会改变牌堆中的牌序()
    {
        var 输入牌序 = 牌库.创建标准牌库().ToArray();
        var 当前牌堆 = new 抽牌堆(输入牌序);

        Array.Reverse(输入牌序);

        Assert.Equal<牌?>(new 牌(花色.红, 1), 当前牌堆.抽下一张());
        Assert.Equal<牌?>(new 牌(花色.红, 2), 当前牌堆.抽下一张());
    }

    [Fact]
    public void 使用相同牌序创建的两个牌堆互不影响()
    {
        var 共同牌序 = 牌库.创建标准牌库();
        var 第一副 = new 抽牌堆(共同牌序);
        var 第二副 = new 抽牌堆(共同牌序);

        第一副.抽下一张();
        第一副.抽下一张();

        Assert.Equal(25, 第一副.剩余张数);
        Assert.Equal(27, 第二副.剩余张数);
        Assert.Equal<牌?>(new 牌(花色.红, 1), 第二副.抽下一张());
        Assert.Equal<牌?>(new 牌(花色.红, 3), 第一副.抽下一张());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(26)]
    [InlineData(28)]
    public void 拒绝数量不等于二十七的牌序(int 数量)
    {
        var 输入牌序 = 牌库.创建标准牌库()
            .Append(new 牌(花色.红, 1)).Take(数量).ToArray();

        Assert.Throws<ArgumentException>(() => new 抽牌堆(输入牌序));
    }

    [Fact]
    public void 拒绝包含重复牌的二十七张牌序()
    {
        var 输入牌序 = 牌库.创建标准牌库().ToArray();
        输入牌序[26] = 输入牌序[0];

        Assert.Throws<ArgumentException>(() => new 抽牌堆(输入牌序));
    }

    [Fact]
    public void 拒绝混入未初始化牌的二十七张牌序()
    {
        // 数量正确、也没有重复，只靠查重会放过它。
        var 输入牌序 = 牌库.创建标准牌库().ToArray();
        输入牌序[26] = default;

        Assert.Throws<ArgumentException>(() => new 抽牌堆(输入牌序));
    }

    [Fact]
    public void 拒绝空引用牌序()
    {
        Assert.Throws<ArgumentNullException>(() => new 抽牌堆(null!));
    }
}
