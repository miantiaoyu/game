namespace DarkTide.Rules;

/// <summary>一个奖池的分配结果：赢家按下注方向排列，Shares 与 Winners 一一对应。</summary>
public sealed record PotResult(Chips Total, IReadOnlyList<int> EligibleSeats, IReadOnlyList<int> Winners, IReadOnlyList<int> Shares);

/// <summary>
/// 把奖池分给赢家（规则第 7 节）。纯函数：输入是奖池、各座位牌力与下注方向，不读牌、不改任何状态。
///
/// 每个池只在自己的有资格座位之间比较；只有一人有资格时直接归他，不需要牌力。
/// 并列赢家平分；分不开的余数从起始位开始沿下注方向，在该池的并列赢家里依次每人补一枚。
/// 例：池 7、三人并列 → 每人 2，余 1 给三人中下注顺序最靠前的那位。
/// </summary>
public static class PotAward
{
    /// <param name="pots">普通奖池，资格名单相同的层应已合并（<see cref="PotLayering"/> 已保证）。</param>
    /// <param name="handValues">按座位号索引的牌力；只需为有资格争夺多人池的座位提供，其余可为 null。</param>
    /// <param name="bettingOrder">本局下注方向，第一个是起始位；决定赢家排列与余数归属。</param>
    public static IReadOnlyList<PotResult> Distribute(
        IReadOnlyList<Pot> pots,
        IReadOnlyList<HandValue?> handValues,
        IReadOnlyList<int> bettingOrder)
    {
        ArgumentNullException.ThrowIfNull(pots);
        ArgumentNullException.ThrowIfNull(handValues);
        ArgumentNullException.ThrowIfNull(bettingOrder);

        var results = new List<PotResult>(pots.Count);
        foreach (var pot in pots)
        {
            if (pot.EligibleSeats.Count == 0)
            {
                // 每一层都至少有一名未弃牌的出资人：最后加注的人一定还在局里，退款又保证顶层至少两人持有。
                // 出现无人有资格的池说明上游算错了，钱不能凭空消失，作为故障暴露。
                throw new InvalidOperationException($"奖池 {pot.Total} 没有任何有资格的座位。");
            }

            // 按下注方向排列有资格者：之后的“依次补一枚”直接按这个顺序。
            var eligibleInOrder = bettingOrder.Where(pot.EligibleSeats.Contains).ToArray();
            if (eligibleInOrder.Length != pot.EligibleSeats.Count)
            {
                throw new ArgumentException("奖池里有不在下注顺序中的座位。", nameof(bettingOrder));
            }

            int[] winners;
            if (eligibleInOrder.Length == 1)
            {
                winners = eligibleInOrder;
            }
            else
            {
                var values = eligibleInOrder
                    .Select(seat => handValues[seat] ?? throw new ArgumentException($"座位 {seat} 要比牌，但没有牌力。", nameof(handValues)))
                    .ToArray();
                var best = values.Max();
                winners = eligibleInOrder.Where((_, index) => values[index] == best).ToArray();
            }

            int share = Math.DivRem(pot.Total.Amount, winners.Length, out int remainder);
            var shares = winners.Select((_, index) => share + (index < remainder ? 1 : 0)).ToArray();
            results.Add(new PotResult(pot.Total, pot.EligibleSeats, Array.AsReadOnly(winners), Array.AsReadOnly(shares)));
        }
        return results;
    }
}
