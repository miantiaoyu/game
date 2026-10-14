using DarkTide.Rules;

namespace DarkTide.Room.Tests;

internal static class TestHands
{
    /// <summary>
    /// 只关心发牌时用：底注 1，参与座位各 100 枚，非参与座位 0 枚。
    /// 参数顺序与 <see cref="Hand"/> 构造函数前三个参数相同，旧的发牌测试可以原样调用。
    /// </summary>
    public static Hand Deal(int seatCount, IReadOnlyList<Card> drawOrder, IReadOnlyList<int> participantOrder, int ante = 1, int chips = 100)
    {
        // 非法容量也要走到 Hand 的构造函数，让它给出真正的拒绝；这里只把数组长度限制在合理范围内，
        // 否则 int.MaxValue 这类测试值会先在分配数组时失败。
        var stacks = new int[Math.Clamp(seatCount, 0, TableLimits.MaxSeats)];
        // 越界或重复的名单交给 Hand 去拒绝；这里只给范围内的座位填筹码，不抢先抛错。
        foreach (int seat in participantOrder ?? Array.Empty<int>())
        {
            if (seat >= 0 && seat < seatCount)
            {
                stacks[seat] = chips;
            }
        }
        return new Hand(seatCount, drawOrder, participantOrder!, ante, stacks);
    }
}
