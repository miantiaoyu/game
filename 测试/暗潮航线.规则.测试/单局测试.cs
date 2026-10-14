using 暗潮航线.规则;

namespace 暗潮航线.规则.测试;

public class 单局测试
{
    public static IEnumerable<object[]> 座位人数配置()
    {
        for (int 总座位 = 2; 总座位 <= 8; 总座位++)
        {
            for (int 参与人数 = 2; 参与人数 <= 总座位; 参与人数++)
            {
                yield return new object[] { 总座位, 参与人数 };
            }
        }
    }

    [Theory]
    [MemberData(nameof(座位人数配置))]
    public void 不同容量与参与人数均只给参与座位发牌(int 总座位, int 参与人数)
    {
        var 牌序 = 牌库.创建标准牌库();
        var 参与顺序 = Enumerable.Range(0, 总座位).Reverse().Take(参与人数).ToArray();
        var 局面 = new 单局(总座位, 牌序, 参与顺序);
        var 已发牌 = new List<牌>();

        Assert.Equal(总座位, 局面.座位总数);
        Assert.Equal(参与人数, 局面.本局参与人数);
        Assert.Equal(27 - 2 * 参与人数, 局面.剩余张数);
        Assert.Equal(参与顺序, 局面.参与座位顺序);

        for (int 座位 = 0; 座位 < 总座位; 座位++)
        {
            var 视图 = 局面.生成玩家视图(座位);
            Assert.Equal(参与顺序.Contains(座位) ? 2 : 0, 视图.本人底牌.Count);
            Assert.Equal(Enumerable.Range(0, 总座位).Where(编号 => 编号 != 座位),
                视图.其他座位.Select(概况 => 概况.座位));
            Assert.All(视图.其他座位, 概况 =>
                Assert.Equal(参与顺序.Contains(概况.座位) ? 2 : 0, 概况.底牌张数));
            已发牌.AddRange(视图.本人底牌);
        }

        Assert.Equal(2 * 参与人数, 已发牌.Distinct().Count());
        Assert.True(牌序.Take(2 * 参与人数).ToHashSet().SetEquals(已发牌));
    }

    [Theory]
    [InlineData(6, 1, 4)]
    [InlineData(0, 2, 5)]
    [InlineData(3, 3, 6)]
    public void 八座三人按指定的不连续座位顺序发牌(int 座位, int 第一张编号, int 第二张编号)
    {
        var 局面 = new 单局(8, 牌库.创建标准牌库(), new[] { 6, 0, 3 });

        Assert.Equal(new[] { 第一张编号, 第二张编号 },
            局面.生成玩家视图(座位).本人底牌.Select(当前牌 => (int)当前牌.编号));
        Assert.Equal(21, 局面.剩余张数);
    }

    [Theory]
    [InlineData(0, 4, 9)]
    [InlineData(1, 5, 10)]
    [InlineData(2, 1, 6)]
    [InlineData(3, 2, 7)]
    [InlineData(4, 3, 8)]
    public void 从指定座位顺序轮流发放两轮底牌(int 座位, int 第一张编号, int 第二张编号)
    {
        var 局面 = new 单局(5, 牌库.创建标准牌库(), new[] { 2, 3, 4, 0, 1 });

        var 视图 = 局面.生成玩家视图(座位);

        Assert.Equal(座位, 视图.本人座位);
        Assert.Equal(new[] { 第一张编号, 第二张编号 }, 视图.本人底牌.Select(当前牌 => (int)当前牌.编号));
    }

    [Fact]
    public void 五人各两张且恰好消耗牌序前十张()
    {
        var 牌序 = 牌库.创建标准牌库().Reverse().ToArray();
        var 局面 = new 单局(5, 牌序, new[] { 0, 1, 2, 3, 4 });
        var 所有已发牌 = new List<牌>();

        for (int 座位 = 0; 座位 < 5; 座位++)
        {
            var 视图 = 局面.生成玩家视图(座位);
            Assert.Equal(2, 视图.本人底牌.Count);
            所有已发牌.AddRange(视图.本人底牌);
        }

        Assert.Equal(10, 所有已发牌.Distinct().Count());
        Assert.True(牌序.Take(10).ToHashSet().SetEquals(所有已发牌));
        Assert.Equal(17, 局面.剩余张数);
    }

    [Fact]
    public void 修改输入或创建其他局面不会改变已有底牌()
    {
        var 输入牌序 = 牌库.创建标准牌库().ToArray();
        int[] 输入顺序 = { 0, 1, 2, 3, 4 };
        var 局面 = new 单局(5, 输入牌序, 输入顺序);

        Array.Reverse(输入牌序);
        Array.Reverse(输入顺序);
        _ = new 单局(5, 输入牌序, 输入顺序);

        Assert.Equal(new[] { new 牌(花色.红, 1), new 牌(花色.红, 6) }, 局面.生成玩家视图(0).本人底牌);
        Assert.Equal(17, 局面.剩余张数);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, 局面.参与座位顺序);
    }

    [Fact]
    public void 参与顺序保留原始不连续顺序且不能强转回数组修改()
    {
        var 局面 = new 单局(8, 牌库.创建标准牌库(), new[] { 6, 0, 3 });

        Assert.Equal(new[] { 6, 0, 3 }, 局面.参与座位顺序);
        Assert.Equal(3, 局面.本局参与人数);
        // 只读接口若底层就是数组，调用方强转回 int[] 即可改写局面。
        Assert.False(局面.参与座位顺序 is int[]);
        Assert.Same(局面.参与座位顺序, 局面.参与座位顺序);
    }

    [Fact]
    public void 拒绝不足两人重复越界或超过容量的参与名单()
    {
        int[][] 非法顺序 =
        {
            Array.Empty<int>(),
            new[] { 0 },
            new[] { 0, 1, 2, 3, 4, 0 },
            new[] { 0, 1, 2, 3, 3 },
            new[] { -1, 1, 2, 3, 4 },
            new[] { 0, 1, 2, 3, 5 }
        };

        foreach (var 顺序 in 非法顺序)
        {
            Assert.Throws<ArgumentException>(() => new 单局(5, 牌库.创建标准牌库(), 顺序));
        }
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(int.MaxValue)]
    public void 拒绝不支持的牌桌容量(int 非法容量)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new 单局(非法容量, 牌库.创建标准牌库(), new[] { 0, 1 }));
    }

    [Fact]
    public void 拒绝空引用输入()
    {
        Assert.Throws<ArgumentNullException>(() => new 单局(5, null!, new[] { 0, 1, 2, 3, 4 }));
        Assert.Throws<ArgumentNullException>(() => new 单局(5, 牌库.创建标准牌库(), null!));
    }

    [Fact]
    public void 发底牌前拒绝不完整或重复的牌序()
    {
        var 输入牌序 = 牌库.创建标准牌库().ToArray();
        int[] 顺序 = { 0, 1, 2, 3, 4 };

        Assert.Throws<ArgumentException>(() => new 单局(5, 输入牌序.Take(26).ToArray(), 顺序));

        输入牌序[26] = 输入牌序[0];
        Assert.Throws<ArgumentException>(() => new 单局(5, 输入牌序, 顺序));
    }

    // 以下为本步新增：底注、累计投入、公共牌、操作轮次。

    [Fact]
    public void 新局没有投入也没有公共牌且停在底注轮()
    {
        var 局面 = new 单局(5, 牌库.创建标准牌库(), new[] { 0, 1, 2, 3, 4 });

        Assert.Equal(操作轮.底注, 局面.当前轮次);
        Assert.Equal(0, 局面.普通池总额);
        Assert.All(Enumerable.Range(0, 5), 座位 => Assert.Equal(0, 局面.各座位累计投入[座位]));
        Assert.Empty(局面.公共牌);
        Assert.All(Enumerable.Range(0, 5), 座位 =>
        {
            Assert.False(局面.已弃牌座位(座位));
            Assert.False(局面.已全下座位(座位));
        });
    }

    [Fact]
    public void 收取底注记入累计投入并回到各人实付()
    {
        var 局面 = new 单局(5, 牌库.创建标准牌库(), new[] { 0, 1, 2, 3, 4 });
        var 筹码 = Enumerable.Repeat(100, 5).ToArray();

        var 实付 = 局面.收取底注(1, 筹码);

        Assert.All(Enumerable.Range(0, 5), 座位 => Assert.Equal(1, 实付[座位]));
        Assert.Equal(5, 局面.普通池总额);
        // 收完底注即进入第一轮下注。
        Assert.Equal(操作轮.一注, 局面.当前轮次);
        Assert.All(Enumerable.Range(0, 5), 座位 => Assert.False(局面.已全下座位(座位)));
    }

    [Fact]
    public void 筹码不足底注时全部交出并记为全下()
    {
        var 局面 = new 单局(3, 牌库.创建标准牌库(), new[] { 0, 1, 2 });
        var 筹码 = new[] { 100, 1, 100 };

        var 实付 = 局面.收取底注(2, 筹码);

        Assert.Equal(new[] { 2, 1, 2 }, 实付);
        Assert.Equal(5, 局面.普通池总额);
        // 只交出 1 枚的座位等同全下，但仍发牌、不淘汰。
        Assert.True(局面.已全下座位(1));
        Assert.Equal(2, 局面.生成玩家视图(1).本人底牌.Count);
    }

    [Fact]
    public void 未参与本局的座位不交底注也没有投入()
    {
        var 局面 = new 单局(5, 牌库.创建标准牌库(), new[] { 6 % 5, 0 });
        var 筹码 = Enumerable.Repeat(100, 5).ToArray();

        var 实付 = 局面.收取底注(2, 筹码);

        Assert.Equal(0, 实付[2]);
        Assert.Equal(0, 局面.各座位累计投入[2]);
        Assert.Equal(4, 局面.普通池总额);
    }

    [Fact]
    public void 重复收取底注或过晚收取会被拒绝()
    {
        var 局面 = new 单局(3, 牌库.创建标准牌库(), new[] { 0, 1, 2 });
        var 筹码 = Enumerable.Repeat(100, 3).ToArray();
        局面.收取底注(1, 筹码);

        // 收完底注已进入一注轮，不能再收一次。
        Assert.Throws<InvalidOperationException>(() => 局面.收取底注(1, 筹码));
        Assert.Equal(3, 局面.普通池总额);
    }

    [Fact]
    public void 底注参数非法时状态不被改动()
    {
        var 局面 = new 单局(3, 牌库.创建标准牌库(), new[] { 0, 1, 2 });

        Assert.Throws<ArgumentOutOfRangeException>(() => 局面.收取底注(-1, Enumerable.Repeat(100, 3).ToArray()));
        Assert.Throws<ArgumentException>(() => 局面.收取底注(1, new[] { 100, 100 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => 局面.收取底注(1, new[] { 100, -1, 100 }));

        Assert.Equal(0, 局面.普通池总额);
        Assert.Equal(操作轮.底注, 局面.当前轮次);
    }

    [Fact]
    public void 按牌库顺序逐张揭开公共牌()
    {
        var 牌序 = 牌库.创建标准牌库();
        var 局面 = new 单局(5, 牌序, new[] { 0, 1, 2, 3, 4 });

        // 五人各两张底牌用掉前十张，公共牌从第十一张开始。
        var 第一张 = 局面.揭公共牌();
        var 第二张 = 局面.揭公共牌();
        var 第三张 = 局面.揭公共牌();

        Assert.Equal(牌序[10], 第一张);
        Assert.Equal(牌序[11], 第二张);
        Assert.Equal(牌序[12], 第三张);
        Assert.Equal(new[] { 牌序[10], 牌序[11], 牌序[12] }, 局面.公共牌);
        Assert.Equal(27 - 10 - 3, 局面.剩余张数);
    }

    [Fact]
    public void 三张公共牌揭完后不能再揭()
    {
        var 局面 = new 单局(2, 牌库.创建标准牌库(), new[] { 0, 1 });
        for (int 次数 = 0; 次数 < 单局.公共牌张数; 次数++)
        {
            局面.揭公共牌();
        }

        Assert.Equal(单局.公共牌张数, 局面.公共牌.Count);
        Assert.Throws<InvalidOperationException>(() => 局面.揭公共牌());
    }

    [Fact]
    public void 公共牌是只读视图且外部改不了内部牌堆()
    {
        var 局面 = new 单局(2, 牌库.创建标准牌库(), new[] { 0, 1 });
        局面.揭公共牌();

        Assert.False(局面.公共牌 is 牌[]);
        Assert.Single(局面.公共牌);
    }

    [Fact]
    public void 操作轮次只能往前推进()
    {
        var 局面 = new 单局(3, 牌库.创建标准牌库(), new[] { 0, 1, 2 });

        Assert.Equal(操作轮.底注, 局面.当前轮次);
        局面.推进到(操作轮.一注);
        局面.推进到(操作轮.二注);
        局面.推进到(操作轮.改航);
        局面.推进到(操作轮.三注);
        局面.推进到(操作轮.摊牌);

        Assert.Equal(操作轮.摊牌, 局面.当前轮次);
        Assert.Throws<InvalidOperationException>(() => 局面.推进到(操作轮.二注));
        Assert.Throws<InvalidOperationException>(() => 局面.推进到(操作轮.摊牌));
    }

    [Fact]
    public void 弃牌与全下状态只增不减()
    {
        var 局面 = new 单局(3, 牌库.创建标准牌库(), new[] { 0, 1, 2 });

        局面.记录本轮状态(1, 弃牌: true, 全下: false);
        局面.记录本轮状态(2, 弃牌: false, 全下: true);
        // 后续轮次传入相反的值也不会把状态改回去。
        局面.记录本轮状态(1, 弃牌: false, 全下: false);

        Assert.True(局面.已弃牌座位(1));
        Assert.False(局面.已弃牌座位(2));
        Assert.True(局面.已全下座位(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => 局面.记录本轮状态(9, 弃牌: true, 全下: false));
    }

    [Fact]
    public void 累计投入是活视图而非快照()
    {
        var 局面 = new 单局(3, 牌库.创建标准牌库(), new[] { 0, 1, 2 });
        var 视图 = 局面.各座位累计投入;

        局面.收取底注(2, Enumerable.Repeat(100, 3).ToArray());

        // 读取属性时不再分配新集合，拿到的是同一个活视图，会反映随后的变化。
        Assert.Same(视图, 局面.各座位累计投入);
        Assert.Equal(new[] { 2, 2, 2 }, 视图);
        Assert.False(视图 is int[]);
    }
}
