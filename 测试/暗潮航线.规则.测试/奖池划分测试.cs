using 暗潮航线.规则;

namespace 暗潮航线.规则.测试;

public class 奖池划分测试
{
    private static IReadOnlyList<奖池> 划分(int[] 投入, params int[] 弃牌座位)
    {
        var 已弃牌 = new bool[投入.Length];
        foreach (int 座位 in 弃牌座位)
        {
            已弃牌[座位] = true;
        }
        return 奖池划分.划分普通池(投入, 已弃牌);
    }

    [Fact]
    public void 投入相同时只有一个主池()
    {
        var 池 = 划分(new[] { 30, 30, 30 });

        var 唯一 = Assert.Single(池);
        Assert.Equal(90, 唯一.金额.数量);
        Assert.Equal(new[] { 0, 1, 2 }, 唯一.有资格座位);
    }

    [Fact]
    public void 全下形成主池与边池()
    {
        // 规则第 6 节例：A 全下 10，B、C 各投入 30 → 主池 30（三人）、边池 40（仅 B、C）。
        var 池 = 划分(new[] { 10, 30, 30 });

        Assert.Equal(2, 池.Count);
        Assert.Equal(30, 池[0].金额.数量);
        Assert.Equal(new[] { 0, 1, 2 }, 池[0].有资格座位);
        Assert.Equal(40, 池[1].金额.数量);
        Assert.Equal(new[] { 1, 2 }, 池[1].有资格座位);
        Assert.Equal(70, 奖池划分.合计金额(池));
    }

    [Fact]
    public void 底注不足也可以形成边池()
    {
        // 规则例：底注 2，三人实际只能交 1/2/2 → 主池 3（三人）、边池 2（仅足额两人）。
        var 池 = 划分(new[] { 1, 2, 2 });

        Assert.Equal(2, 池.Count);
        Assert.Equal(3, 池[0].金额.数量);
        Assert.Equal(new[] { 0, 1, 2 }, 池[0].有资格座位);
        Assert.Equal(2, 池[1].金额.数量);
        Assert.Equal(new[] { 1, 2 }, 池[1].有资格座位);
    }

    [Fact]
    public void 多层全下逐层建池且总额守恒()
    {
        var 投入 = new[] { 5, 10, 20, 30 };
        var 池 = 划分(投入);

        Assert.Equal(65, 奖池划分.合计金额(池));
        Assert.Equal(投入.Sum(), 奖池划分.合计金额(池));
        Assert.Equal(new[] { 20, 15, 20, 10 }, 池.Select(当前 => 当前.金额.数量));
        Assert.Equal(new[] { 4, 3, 2, 1 }, 池.Select(当前 => 当前.有资格座位.Count));
    }

    [Fact]
    public void 弃牌者出过的钱留在池里但本人失去资格()
    {
        // 全下 10 的座位 1 是出了钱的普通出资人；两人各投 30，其中 1 号出资 30 后退到弃牌。
        var 池 = 划分(new[] { 10, 30, 30 }, 弃牌座位: 1);

        // 两层资格不同（{0,2} 与 {2}），不能合并：
        // 主池 30 由 0、2 争夺；边池 40 只有 2 有资格，直接归他。
        Assert.Equal(2, 池.Count);
        Assert.Equal(30, 池[0].金额.数量);
        Assert.Equal(new[] { 0, 2 }, 池[0].有资格座位);
        Assert.Equal(40, 池[1].金额.数量);
        Assert.Equal(new[] { 2 }, 池[1].有资格座位);
        // 弃牌者出的 30 仍然在池里。
        Assert.Equal(70, 奖池划分.合计金额(池));
    }

    [Fact]
    public void 资格名单相同的相邻层合并()
    {
        // 0 投 10，1、2 各投 20。第一层三人都有资格，第二层只有 1、2 —— 资格不同，不合并。
        // 若把 0 也算进第二层就会错误合并，这里正是检查资格是按“出到本层的人”算的。
        var 池 = 划分(new[] { 10, 20, 20 });

        Assert.Equal(2, 池.Count);
        Assert.Equal(30, 池[0].金额.数量);
        Assert.Equal(new[] { 0, 1, 2 }, 池[0].有资格座位);
        Assert.Equal(20, 池[1].金额.数量);
        Assert.Equal(new[] { 1, 2 }, 池[1].有资格座位);
    }

    [Fact]
    public void 弃牌者使资格名单相同时相邻层合并()
    {
        // 0 投 10，1 投 20，2 投 20：第一层资格 {0,1,2}，第二层 {1,2} —— 不同，不合并。
        // 但若 0 弃牌，第一层资格变成 {1,2}，与第二层相同，则合并成一池 50。
        var 池 = 划分(new[] { 10, 20, 20 }, 弃牌座位: 0);

        var 唯一 = Assert.Single(池);
        Assert.Equal(50, 唯一.金额.数量);
        Assert.Equal(new[] { 1, 2 }, 唯一.有资格座位);
    }

    [Fact]
    public void 只有一人投入时该池只有他有资格()
    {
        var 池 = 划分(new[] { 7, 0, 0 });

        var 唯一 = Assert.Single(池);
        Assert.Equal(7, 唯一.金额.数量);
        Assert.Equal(new[] { 0 }, 唯一.有资格座位);
    }

    [Fact]
    public void 无人投入时没有奖池()
    {
        Assert.Empty(划分(new[] { 0, 0 }));
    }

    [Fact]
    public void 拒绝长度不一致与负数投入()
    {
        Assert.Throws<ArgumentException>(() => 奖池划分.划分普通池(new[] { 1, 2 }, new bool[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => 奖池划分.划分普通池(new[] { -1, 2 }, new bool[2]));
        Assert.Throws<ArgumentNullException>(() => 奖池划分.划分普通池(null!, new bool[2]));
    }

    [Fact]
    public void 随机情形与逐枚枚举的独立算法一致()
    {
        // 独立参考实现：把筹码按“每一枚”的高度逐层枚举，而不是按额度跳跃。
        // 两者结果必须完全一致，等于用另一种写法交叉验证分层逻辑。
        var 随机 = new Random(20261014);
        for (int 轮次 = 0; 轮次 < 2000; 轮次++)
        {
            int 座位数 = 随机.Next(2, 7);
            var 投入 = new int[座位数];
            var 已弃牌 = new bool[座位数];
            for (int 座位 = 0; 座位 < 座位数; 座位++)
            {
                投入[座位] = 随机.Next(0, 6) * 随机.Next(0, 3);   // 偶发 0，制造“全下到 0”这类边界
                已弃牌[座位] = 随机.Next(3) == 0;
            }

            if (投入.All(金额 => 金额 == 0))
            {
                continue;
            }

            var 实际 = 奖池划分.划分普通池(投入, 已弃牌);
            var 预期 = 逐枚枚举(投入, 已弃牌);

            // 先把每层压成可比较的文本：ValueTuple 里的 List<int> 是按引用比较的，直接比序列永远不等。
            var 预期文本 = 预期.Select(项 => $"{项.金额}:{string.Join('|', 项.资格)}").ToList();
            var 实际文本 = 实际.Select(池 => $"{池.金额.数量}:{string.Join('|', 池.有资格座位)}").ToList();

            Assert.True(预期文本.SequenceEqual(实际文本),
                $"投入=[{string.Join(',', 投入)}] 弃牌=[{string.Join(',', 已弃牌.Select(值 => 值 ? 1 : 0))}]\n" +
                $"预期={string.Join(" ", 预期文本)}\n实际={string.Join(" ", 实际文本)}");
        }
    }

    [Fact]
    public void 任意情形下金额守恒且资格合法()
    {
        var 随机 = new Random(20261015);
        for (int 轮次 = 0; 轮次 < 2000; 轮次++)
        {
            int 座位数 = 随机.Next(2, 7);
            var 投入 = new int[座位数];
            var 已弃牌 = new bool[座位数];
            for (int 座位 = 0; 座位 < 座位数; 座位++)
            {
                投入[座位] = 随机.Next(0, 8);
                已弃牌[座位] = 随机.Next(3) == 0;
            }

            var 池 = 奖池划分.划分普通池(投入, 已弃牌);
            string 现场 = $"投入=[{string.Join(',', 投入)}] 弃牌=[{string.Join(',', 已弃牌.Select(值 => 值 ? 1 : 0))}]";

            // 1. 金额守恒：所有层加起来等于全部投入，一枚不多一枚不少。
            Assert.True(投入.Sum() == 奖池划分.合计金额(池), 现场);
            // 2. 不存在金额为零的空层。
            Assert.All(池, 当前 => Assert.True(当前.金额.数量 > 0, 现场));
            // 3. 资格名单里的人必须出过钱、且没弃牌。
            Assert.All(池, 当前 => Assert.All(当前.有资格座位, 座位 =>
            {
                Assert.True(投入[座位] > 0, 现场);
                Assert.False(已弃牌[座位], 现场);
            }));
            // 4. 资格名单随层高单调缩小：后一层是前一层的子集。
            for (int 序号 = 1; 序号 < 池.Count; 序号++)
            {
                Assert.True(池[序号].有资格座位.All(座位 => 池[序号 - 1].有资格座位.Contains(座位)), 现场);
            }
            // 5. 相邻两层的资格必须不同，否则说明该合并的没合并。
            for (int 序号 = 1; 序号 < 池.Count; 序号++)
            {
                Assert.False(池[序号].有资格座位.SequenceEqual(池[序号 - 1].有资格座位), 现场);
            }
        }
    }

    /// <summary>
    /// 独立参考实现：把筹码按“每一枚的高度”逐层枚举，而不是按额度跳跃。
    /// 高度 h 上，凡是投入达到 h 的人都各出 1 枚，所以该高度贡献的人数枚筹码。
    /// </summary>
    private static List<(int 金额, List<int> 资格)> 逐枚枚举(int[] 投入, bool[] 已弃牌)
    {
        var 结果 = new List<(int 金额, List<int> 资格)>();
        int 最高 = 投入.Max();
        for (int 高度 = 1; 高度 <= 最高; 高度++)
        {
            var 出钱者 = Enumerable.Range(0, 投入.Length).Where(座位 => 投入[座位] >= 高度).ToList();
            var 资格 = 出钱者.Where(座位 => !已弃牌[座位]).ToList();
            if (结果.Count > 0 && 结果[^1].资格.SequenceEqual(资格))
            {
                // 资格不变就把这一层并进上一层；并进去的是“人数”枚，不是 1 枚。
                结果[^1] = (结果[^1].金额 + 出钱者.Count, 资格);
            }
            else
            {
                结果.Add((出钱者.Count, 资格));
            }
        }
        return 结果;
    }

    [Fact]
    public void 有资格座位是只读视图而非可变数组()
    {
        var 池 = 划分(new[] { 5, 5 });

        Assert.False(池[0].有资格座位 is int[]);
    }
}
