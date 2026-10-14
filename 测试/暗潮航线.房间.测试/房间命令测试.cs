using 暗潮航线.规则;

namespace 暗潮航线.房间.测试;

public class 房间命令测试
{
    private static readonly Guid 玩家甲 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid 玩家乙 = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public void 重发成功命令返回原回执且只生效一次()
    {
        var 当前房间 = new 房间(Guid.NewGuid(), 8);
        var 命令 = new 入座命令(Guid.NewGuid(), 玩家甲, 3);

        var 第一次 = 当前房间.执行(命令);
        // 模拟回执丢失后客户端用同一编号、同一内容重发；新对象也算同一命令。
        var 第二次 = 当前房间.执行(new 入座命令(命令.命令编号, 玩家甲, 3));

        Assert.Equal(第一次, 第二次);
        Assert.Equal(1, 当前房间.状态版本);
        Assert.Equal(1, 当前房间.已入座人数);
    }

    [Fact]
    public void 重发时即使版本已前进也返回原成功而不是版本不符()
    {
        var 当前房间 = new 房间(Guid.NewGuid(), 8);
        var 命令 = new 入座命令(Guid.NewGuid(), 玩家甲, 3, 预期版本: 0);

        var 原回执 = 当前房间.执行(命令);
        当前房间.执行(new 入座命令(Guid.NewGuid(), 玩家乙, 4));

        Assert.Equal(原回执, 当前房间.执行(命令));
    }

    [Fact]
    public void 重发被拒绝的命令得到同样的拒绝即使状态已允许()
    {
        var 当前房间 = new 房间(Guid.NewGuid(), 8);
        当前房间.执行(new 入座命令(Guid.NewGuid(), 玩家甲, 3));
        var 抢座 = new 入座命令(Guid.NewGuid(), 玩家乙, 3);

        Assert.Equal(拒绝原因.座位已有玩家, Assert.Throws<规则拒绝异常>(() => 当前房间.执行(抢座)).原因);
        当前房间.执行(new 离座命令(Guid.NewGuid(), 玩家甲));

        // 座位已经空出来，但这是“旧点击”的重发，不能在玩家不知情时变成成功。
        Assert.Equal(拒绝原因.座位已有玩家, Assert.Throws<规则拒绝异常>(() => 当前房间.执行(抢座)).原因);
        Assert.Null(当前房间.查找座位(玩家乙));
    }

    [Fact]
    public void 同一编号不同内容被拒绝且不覆盖原结果()
    {
        var 当前房间 = new 房间(Guid.NewGuid(), 8);
        var 编号 = Guid.NewGuid();
        var 原回执 = 当前房间.执行(new 入座命令(编号, 玩家甲, 3));

        var 冲突 = Assert.Throws<规则拒绝异常>(() => 当前房间.执行(new 入座命令(编号, 玩家甲, 4)));

        Assert.Equal(拒绝原因.命令编号冲突, 冲突.原因);
        Assert.Equal(原回执, 当前房间.执行(new 入座命令(编号, 玩家甲, 3)));
        Assert.Equal<int?>(3, 当前房间.查找座位(玩家甲));
    }

    [Fact]
    public void 预期版本过时则拒绝且不改变状态()
    {
        var 当前房间 = new 房间(Guid.NewGuid(), 8);
        当前房间.执行(new 入座命令(Guid.NewGuid(), 玩家甲, 3, 预期版本: 0));

        var 过时 = Assert.Throws<规则拒绝异常>(() =>
            当前房间.执行(new 入座命令(Guid.NewGuid(), 玩家乙, 4, 预期版本: 0)));

        Assert.Equal(拒绝原因.版本不符, 过时.原因);
        Assert.Equal(1, 当前房间.状态版本);
        Assert.Null(当前房间.查找座位(玩家乙));
        Assert.Equal(2, 当前房间.执行(new 入座命令(Guid.NewGuid(), 玩家乙, 4, 预期版本: 1)).提交后版本);
    }

    [Fact]
    public void 发底牌命令按内容判断是否相同()
    {
        var 编号 = Guid.NewGuid();
        var 牌序 = 牌库.创建标准牌库();

        var 第一份 = new 发底牌命令(编号, 牌序, new[] { 0, 1 });
        var 第二份 = new 发底牌命令(编号, 牌序.ToArray(), new[] { 0, 1 });
        var 顺序不同 = new 发底牌命令(编号, 牌序, new[] { 1, 0 });

        Assert.Equal(第一份, 第二份);
        Assert.Equal(第一份.GetHashCode(), 第二份.GetHashCode());
        Assert.NotEqual(第一份, 顺序不同);
    }

    [Fact]
    public void 发底牌命令复制输入()
    {
        var 牌序 = 牌库.创建标准牌库().ToArray();
        int[] 顺序 = { 6, 0 };
        var 命令 = new 发底牌命令(Guid.NewGuid(), 牌序, 顺序);

        Array.Reverse(牌序);
        Array.Reverse(顺序);

        Assert.Equal(new[] { 6, 0 }, 命令.参与座位顺序);
        Assert.Equal(new 牌(花色.红, 1), 命令.牌序[0]);
        Assert.False(命令.牌序 is 牌[]);
    }

    [Fact]
    public void 命令构造时检查格式()
    {
        Assert.Throws<ArgumentException>(() => new 入座命令(Guid.Empty, 玩家甲, 0));
        Assert.Throws<ArgumentException>(() => new 入座命令(Guid.NewGuid(), Guid.Empty, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new 离座命令(Guid.NewGuid(), 玩家甲, -1));
        Assert.Throws<ArgumentException>(() => new 发底牌命令(Guid.NewGuid(), new 牌[28], new[] { 0, 1 }));
        Assert.Throws<ArgumentException>(() => new 发底牌命令(Guid.NewGuid(), 牌库.创建标准牌库(), new int[9]));
        Assert.Throws<ArgumentNullException>(() => new 发底牌命令(Guid.NewGuid(), null!, new[] { 0, 1 }));
    }

    [Fact]
    public void 命令记录表满后淘汰最早的记录()
    {
        var 记录表 = new 命令记录表(容量: 2);
        var 命令们 = Enumerable.Range(0, 3).Select(座位 => new 入座命令(Guid.NewGuid(), 玩家甲, 座位)).ToArray();

        foreach (var 命令 in 命令们)
        {
            记录表.记录成功(命令, new 命令回执(命令.命令编号, 1));
        }

        Assert.Equal(2, 记录表.条数);
        Assert.False(记录表.尝试查找(命令们[0].命令编号, out _));
        Assert.True(记录表.尝试查找(命令们[1].命令编号, out _));
        Assert.True(记录表.尝试查找(命令们[2].命令编号, out _));
    }
}
