using 暗潮航线.规则;

namespace 暗潮航线.房间.测试;

public class 房间测试
{
    private static readonly Guid 玩家甲 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid 玩家乙 = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid 玩家丙 = Guid.Parse("00000000-0000-0000-0000-000000000003");

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void 容量与入座人数分开且离座不重排其他座位(int 容量)
    {
        var 当前房间 = new 房间(Guid.NewGuid(), 容量);
        当前房间.入座(玩家甲, 容量 - 1);
        当前房间.入座(玩家乙, 0);
        当前房间.离座(玩家乙);

        Assert.Equal(容量, 当前房间.座位总数);
        Assert.Equal(1, 当前房间.已入座人数);
        Assert.Equal<int?>(容量 - 1, 当前房间.查找座位(玩家甲));
        Assert.Null(当前房间.查找座位(玩家乙));
        当前房间.入座(玩家丙, 0);
        Assert.Equal<int?>(0, 当前房间.查找座位(玩家丙));
    }

    [Fact]
    public void 不能抢占已有座位或让同一身份占两个座位()
    {
        var 当前房间 = new 房间(Guid.NewGuid(), 8);
        当前房间.入座(玩家甲, 6);

        断言拒绝(拒绝原因.座位已有玩家, () => 当前房间.入座(玩家乙, 6));
        断言拒绝(拒绝原因.玩家已入座, () => 当前房间.入座(玩家甲, 0));
        断言拒绝(拒绝原因.玩家已入座, () => 当前房间.入座(玩家甲, 6));
        Assert.Equal(1, 当前房间.已入座人数);
        Assert.Equal<int?>(6, 当前房间.查找座位(玩家甲));
        Assert.Null(当前房间.查找座位(玩家乙));
    }

    [Fact]
    public void 根据身份映射真实座位提供各自底牌()
    {
        var 当前房间 = 创建三人房间();
        当前房间.发底牌(牌库.创建标准牌库(), new[] { 6, 0, 3 });

        var 甲视图 = 当前房间.获取本人视图(玩家甲);
        var 乙视图 = 当前房间.获取本人视图(玩家乙);
        Assert.Equal(6, 甲视图.本人座位);
        Assert.Equal(0, 乙视图.本人座位);
        Assert.Equal(new[] { new 牌(花色.红, 1), new 牌(花色.红, 4) }, 甲视图.本人底牌);
        Assert.Equal(new[] { new 牌(花色.红, 2), new 牌(花色.红, 5) }, 乙视图.本人底牌);
        Assert.Equal(7, 甲视图.其他座位.Count);
    }

    [Fact]
    public void 相同编号重新传入仍映射同一个座位()
    {
        var 当前房间 = 创建三人房间();
        当前房间.发底牌(牌库.创建标准牌库(), new[] { 6, 0, 3 });
        var 再次解析的身份 = Guid.Parse(玩家甲.ToString());

        Assert.Equal<int?>(6, 当前房间.查找座位(再次解析的身份));
        Assert.Equal(当前房间.获取本人视图(玩家甲).本人底牌.ToArray(),
            当前房间.获取本人视图(再次解析的身份).本人底牌.ToArray());
    }

    [Fact]
    public void 已入座但未参与本局的玩家没有底牌()
    {
        var 当前房间 = 创建三人房间();
        当前房间.发底牌(牌库.创建标准牌库(), new[] { 6, 0 });

        var 视图 = 当前房间.获取本人视图(玩家丙);
        Assert.Equal(3, 当前房间.已入座人数);
        Assert.Equal(3, 视图.本人座位);
        Assert.Empty(视图.本人底牌);
        Assert.Equal(2, 视图.其他座位.Count(概况 => 概况.底牌张数 == 2));
    }

    [Fact]
    public void 未入座者与已经离座者均无法查看()
    {
        var 当前房间 = 创建三人房间();
        当前房间.离座(玩家丙);
        当前房间.发底牌(牌库.创建标准牌库(), new[] { 6, 0 });

        断言拒绝(拒绝原因.玩家未入座, () => 当前房间.获取本人视图(玩家丙));
        断言拒绝(拒绝原因.玩家未入座, () => 当前房间.获取本人视图(Guid.NewGuid()));
        Assert.Equal<int?>(6, 当前房间.查找座位(玩家甲));
    }

    [Fact]
    public void 房间成员绑定彼此隔离()
    {
        var 第一间 = new 房间(Guid.NewGuid(), 8);
        var 第二间 = new 房间(Guid.NewGuid(), 8);
        第一间.入座(玩家甲, 0);
        第一间.入座(玩家乙, 1);
        第二间.入座(玩家丙, 0);
        第一间.发底牌(牌库.创建标准牌库(), new[] { 0, 1 });

        Assert.Null(第二间.查找座位(玩家甲));
        断言拒绝(拒绝原因.玩家未入座, () => 第一间.获取本人视图(玩家丙));
        Assert.Equal<int?>(0, 第二间.查找座位(玩家丙));
    }

    [Fact]
    public void 尚未发牌时成员也不能获取底牌视图()
    {
        var 当前房间 = 创建三人房间();

        Assert.Equal(房间阶段.等待入座, 当前房间.阶段);
        断言拒绝(拒绝原因.当前阶段不允许, () => 当前房间.获取本人视图(玩家甲));
    }

    [Fact]
    public void 发牌后拒绝换人离座与重复发牌且原牌不变()
    {
        var 当前房间 = 创建三人房间();
        当前房间.发底牌(牌库.创建标准牌库(), new[] { 6, 0, 3 });
        var 原底牌 = 当前房间.获取本人视图(玩家甲).本人底牌.ToArray();

        断言拒绝(拒绝原因.当前阶段不允许, () => 当前房间.离座(玩家甲));
        断言拒绝(拒绝原因.当前阶段不允许, () => 当前房间.入座(Guid.NewGuid(), 7));
        断言拒绝(拒绝原因.当前阶段不允许, () => 当前房间.发底牌(
            牌库.创建标准牌库().Reverse().ToArray(), new[] { 6, 0, 3 }));
        Assert.Equal(3, 当前房间.已入座人数);
        Assert.Equal(原底牌, 当前房间.获取本人视图(玩家甲).本人底牌.ToArray());
    }

    [Fact]
    public void 只有成功的修改才推进状态版本()
    {
        var 当前房间 = new 房间(Guid.NewGuid(), 8);
        Assert.Equal(0, 当前房间.状态版本);

        当前房间.入座(玩家甲, 6);
        当前房间.入座(玩家乙, 0);
        Assert.Equal(2, 当前房间.状态版本);

        // 被拒绝的修改与查询都不改变版本。
        断言拒绝(拒绝原因.座位已有玩家, () => 当前房间.入座(玩家丙, 6));
        Assert.Throws<ArgumentException>(() => 当前房间.发底牌(牌库.创建标准牌库(), new[] { 6, 6 }));
        _ = 当前房间.查找座位(玩家甲);
        Assert.Equal(2, 当前房间.状态版本);

        当前房间.离座(玩家乙);
        当前房间.入座(玩家乙, 0);
        当前房间.发底牌(牌库.创建标准牌库(), new[] { 6, 0 });
        _ = 当前房间.获取本人视图(玩家甲);
        Assert.Equal(5, 当前房间.状态版本);
        Assert.Equal(房间阶段.已发底牌, 当前房间.阶段);
    }

    [Fact]
    public void 空座位不能参与发牌且失败后仍可正常发牌()
    {
        var 当前房间 = 创建三人房间();

        断言拒绝(拒绝原因.参与座位无玩家, () => 当前房间.发底牌(牌库.创建标准牌库(), new[] { 6, 7 }));
        Assert.Equal(房间阶段.等待入座, 当前房间.阶段);
        当前房间.发底牌(牌库.创建标准牌库(), new[] { 6, 0 });
        Assert.Equal(房间阶段.已发底牌, 当前房间.阶段);
        Assert.Equal(new 牌(花色.红, 1), 当前房间.获取本人视图(玩家甲).本人底牌[0]);
    }

    [Fact]
    public void 错误名单与牌序不会让房间进入已发牌状态()
    {
        var 当前房间 = 创建三人房间();
        int[][] 错误名单 = { Array.Empty<int>(), new[] { 6 }, new[] { 6, 6 }, new[] { 6, -1 }, new[] { 6, 8 } };
        foreach (var 名单 in 错误名单)
        {
            Assert.Throws<ArgumentException>(() => 当前房间.发底牌(牌库.创建标准牌库(), 名单));
            Assert.Equal(房间阶段.等待入座, 当前房间.阶段);
        }
        Assert.Throws<ArgumentException>(() => 当前房间.发底牌(Array.Empty<牌>(), new[] { 6, 0 }));
        Assert.Throws<ArgumentNullException>(() => 当前房间.发底牌(null!, new[] { 6, 0 }));
        Assert.Throws<ArgumentNullException>(() => 当前房间.发底牌(牌库.创建标准牌库(), null!));
        Assert.Equal(房间阶段.等待入座, 当前房间.阶段);
        当前房间.离座(玩家丙);
        当前房间.发底牌(牌库.创建标准牌库(), new[] { 6, 0 });
        Assert.Equal(2, 当前房间.已入座人数);
    }

    [Fact]
    public void 不能使用空玩家编号或让未入座者离座()
    {
        var 当前房间 = 创建三人房间();

        Assert.Throws<ArgumentException>(() => 当前房间.入座(Guid.Empty, 7));
        Assert.Throws<ArgumentException>(() => 当前房间.查找座位(Guid.Empty));
        Assert.Throws<ArgumentException>(() => 当前房间.离座(Guid.Empty));
        Assert.Throws<ArgumentException>(() => 当前房间.获取本人视图(Guid.Empty));
        断言拒绝(拒绝原因.玩家未入座, () => 当前房间.离座(Guid.NewGuid()));
        Assert.Equal(3, 当前房间.已入座人数);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(8)]
    public void 拒绝范围外的座位(int 座位)
    {
        var 当前房间 = new 房间(Guid.NewGuid(), 8);

        断言拒绝(拒绝原因.座位超出范围, () => 当前房间.入座(玩家甲, 座位));
        Assert.Equal(0, 当前房间.已入座人数);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void 拒绝范围外的容量(int 容量)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new 房间(Guid.NewGuid(), 容量));
    }

    [Fact]
    public void 拒绝空房间编号()
    {
        Assert.Throws<ArgumentException>(() => new 房间(Guid.Empty, 8));
    }

    private static void 断言拒绝(拒绝原因 预期原因, Action 操作)
    {
        var 错误 = Assert.Throws<规则拒绝异常>(操作);
        Assert.Equal(预期原因, 错误.原因);
    }

    private static 房间 创建三人房间()
    {
        var 当前房间 = new 房间(Guid.NewGuid(), 8);
        当前房间.入座(玩家甲, 6);
        当前房间.入座(玩家乙, 0);
        当前房间.入座(玩家丙, 3);
        return 当前房间;
    }
}
