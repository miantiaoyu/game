using System.Text.Json;
using 暗潮航线.规则;

namespace 暗潮航线.规则.测试;

public class 玩家底牌视图测试
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void 其他座位仅展示四人的编号与底牌数量(int 本人座位)
    {
        var 局面 = new 单局(5, 牌库.创建标准牌库(), new[] { 0, 1, 2, 3, 4 });

        var 视图 = 局面.生成玩家视图(本人座位);

        Assert.Equal(2, 视图.本人底牌.Count);
        Assert.Equal(Enumerable.Range(0, 5).Where(座位 => 座位 != 本人座位), 视图.其他座位.Select(概况 => 概况.座位));
        Assert.All(视图.其他座位, 概况 => Assert.Equal(2, 概况.底牌张数));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void 改变所有对手底牌与未发牌不会改变本人的可见结果(int 本人座位)
    {
        var 原牌序 = 牌库.创建标准牌库().ToArray();
        var 修改后牌序 = 原牌序.ToArray();

        int[] 参与顺序 = { 6, 0, 3 };
        int 发牌位置 = Array.IndexOf(参与顺序, 本人座位);
        int[] 本人牌位置 = 发牌位置 >= 0
            ? new[] { 发牌位置, 发牌位置 + 参与顺序.Length }
            : Array.Empty<int>();

        // 参与者只保留自己的两张牌；未参与座位没有任何可见牌。
        // 把其余所有牌循环移位，仍然是一副合法牌。
        var 隐藏位置 = Enumerable.Range(0, 27)
            .Except(本人牌位置).ToArray();
        for (int 位置 = 0; 位置 < 隐藏位置.Length; 位置++)
        {
            修改后牌序[隐藏位置[位置]] = 原牌序[隐藏位置[(位置 + 1) % 隐藏位置.Length]];
        }

        var 原局面 = new 单局(8, 原牌序, 参与顺序);
        var 修改后局面 = new 单局(8, 修改后牌序, 参与顺序);

        // 序列化只用于检查整份输出的数据边界，这里尚未定义网络协议。
        Assert.Equal(
            JsonSerializer.Serialize(原局面.生成玩家视图(本人座位)),
            JsonSerializer.Serialize(修改后局面.生成玩家视图(本人座位)));
    }

    [Fact]
    public void 查看其他座位不会影响已生成视图或推进牌堆()
    {
        var 局面 = new 单局(5, 牌库.创建标准牌库(), new[] { 0, 1, 2, 3, 4 });
        var 已有视图 = 局面.生成玩家视图(0);
        string 原结果 = JsonSerializer.Serialize(已有视图);

        for (int 座位 = 0; 座位 < 5; 座位++)
        {
            _ = 局面.生成玩家视图(座位);
        }

        Assert.Equal(原结果, JsonSerializer.Serialize(已有视图));
        Assert.Equal(原结果, JsonSerializer.Serialize(局面.生成玩家视图(0)));
        Assert.Equal(17, 局面.剩余张数);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void 拒绝不存在的查看座位(int 非法座位)
    {
        var 局面 = new 单局(5, 牌库.创建标准牌库(), new[] { 0, 1, 2, 3, 4 });

        Assert.Throws<ArgumentOutOfRangeException>(() => 局面.生成玩家视图(非法座位));
        Assert.Equal(17, 局面.剩余张数);
    }
}
