namespace DarkTide.Rules;

/// <summary>
/// 改航名单中的一人：实际顺位、冻结报价、E，以及他付的费用可以被谁赢走。
/// </summary>
/// <param name="Position">按实际改航先后排的顺位，从 1 开始。</param>
/// <param name="Fee">冻结报价；轮到时余额不足就只能放弃，但仍占这个顺位。</param>
/// <param name="Exposure">规则 5.2 的 E：他有资格争夺、且至少两名未弃牌者竞争的普通池之和。</param>
/// <param name="BonusEligibleSeats">他若付费，费用进入的改航附加池的资格名单（规则 5.3），窗口开始时冻结。</param>
public sealed record RerouteQuote(int Seat, int Position, int Fee, int Exposure, IReadOnlyList<int> BonusEligibleSeats);

/// <summary>
/// 改航窗口的名单与报价（规则 5.1～5.3）。纯函数：输入是二注结束、退款之后的局面，输出整个窗口的冻结报价。
///
/// 名单：未弃牌、有可用筹码、且有自己可争夺的普通池的人，按下注顺序反向排列。
/// 费率：名单 m 人、本人排第 r 位，m≥2 时为 5%＋5%×(r−1)/(m−1)，m=1 时为 10%。
/// 报价＝max（底注，E×费率向上取整）。先按精确比例乘 E 再取整，不提前把比例四舍五入。
///
/// 名单、顺位和报价在窗口开始时一次算好：前人放弃、付费或换牌都不改变后人的价格。
/// </summary>
public static class RerouteWindow
{
    // 费率以万分比表示，整数运算，不经过浮点数。
    public const int FirstRateBps = 500;
    public const int LastRateBps = 1000;
    public const int SoleRateBps = 1000;
    private const int BpsDenominator = 10_000;

    /// <param name="contributions">按座位号索引的累计普通投入（二注已结束并退款）。</param>
    /// <param name="folded">按座位号索引的弃牌状态。</param>
    /// <param name="stacks">按座位号索引的可用筹码。</param>
    /// <param name="bettingOrder">本局下注顺序；改航按它的反向进行。</param>
    public static IReadOnlyList<RerouteQuote> Open(
        IReadOnlyList<int> contributions,
        IReadOnlyList<bool> folded,
        IReadOnlyList<int> stacks,
        IReadOnlyList<int> bettingOrder,
        int ante)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        ArgumentNullException.ThrowIfNull(folded);
        ArgumentNullException.ThrowIfNull(stacks);
        ArgumentNullException.ThrowIfNull(bettingOrder);
        ArgumentOutOfRangeException.ThrowIfLessThan(ante, 1);
        if (stacks.Count != contributions.Count)
        {
            throw new ArgumentException("余额数组与投入数组的长度必须一致。", nameof(stacks));
        }

        // 只有至少两名未弃牌者竞争的池才算“可争夺”：独得的池不计入 E，也不能收纳改航费。
        var contested = PotLayering.SplitRegularPots(contributions, folded)
            .Where(pot => pot.EligibleSeats.Count >= 2)
            .ToArray();

        var members = bettingOrder
            .Reverse()
            .Where(seat => !folded[seat] && stacks[seat] > 0 && contested.Any(pot => pot.EligibleSeats.Contains(seat)))
            .ToArray();

        int count = members.Length;
        var quotes = new RerouteQuote[count];
        for (int index = 0; index < count; index++)
        {
            int seat = members[index];
            int position = index + 1;
            var mine = contested.Where(pot => pot.EligibleSeats.Contains(seat)).ToArray();
            int exposure = mine.Sum(pot => pot.Total.Amount);
            // 池按投入层从低到高排列；附加池挂在他能参与的最高一层，资格名单就是那一层的。
            var bonusEligible = mine[^1].EligibleSeats;
            quotes[index] = new RerouteQuote(seat, position, Quote(exposure, position, count, ante), exposure, bonusEligible);
        }
        return quotes;
    }

    /// <summary>单人报价：max（底注，E×费率向上取整）。</summary>
    public static int Quote(int exposure, int position, int windowSize, int ante)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(exposure);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, windowSize);

        long numerator, denominator;
        if (windowSize == 1)
        {
            numerator = SoleRateBps;
            denominator = BpsDenominator;
        }
        else
        {
            // 5%＋5%×(r−1)/(m−1) 通分：[5%×(m−1)＋5%×(r−1)] / (m−1)。分母保留 (m−1)，避免 1/3 这类比例被截断。
            numerator = (long)FirstRateBps * (windowSize - 1) + (long)(LastRateBps - FirstRateBps) * (position - 1);
            denominator = (long)BpsDenominator * (windowSize - 1);
        }
        // 用长整型相乘：E 最大是全桌筹码，乘万分比后可能超出 int。
        long fee = (exposure * numerator + denominator - 1) / denominator;
        return (int)Math.Max(ante, fee);
    }
}
