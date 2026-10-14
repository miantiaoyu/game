namespace DarkTide.Rules;

/// <summary>
/// 把各座位的累计投入切成主池与边池（规则第 6 节）。
/// 这是纯函数：输入是累计投入和弃牌状态，不读牌、不读整场、不改任何状态。
///
/// 算法：按不同的投入层从低到高逐层建池。
/// 第 i 层的金额＝（本层高度 − 上一层高度）× 投入达到本层的人数；
/// 有资格座位＝出到本层且未弃牌的人。弃牌者的钱仍在池里，只是失去资格。
/// 资格名单相同的相邻层再合并（规则第 7 节），避免同一批人争的钱被拆成几笔。
///
/// 全下者是普通出资人：他只在“自己投入覆盖到的层”里有资格，所以能形成边池。
/// </summary>
public static class PotLayering
{
    /// <param name="累计投入">按座位号索引，每个座位在本局的累计普通投入（含底注与各轮下注）。</param>
    /// <param name="已弃牌">按座位号索引，该座位本局是否已弃牌。</param>
    public static IReadOnlyList<Pot> SplitRegularPots(IReadOnlyList<int> contributions, IReadOnlyList<bool> folded)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        ArgumentNullException.ThrowIfNull(folded);
        if (contributions.Count != folded.Count)
        {
            throw new ArgumentException("投入数组与弃牌数组的长度必须一致。", nameof(folded));
        }

        int seatCount = contributions.Count;
        for (int seat = 0; seat < seatCount; seat++)
        {
            if (contributions[seat] < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(contributions), contributions[seat], "累计投入不能为负数。");
            }
        }

        var layers = new List<(int amount, List<int> eligible)>();
        int previousLevel = 0;
        foreach (int level in contributions.Where(amount => amount > 0).Distinct().Order())
        {
            var contributors = new List<int>();
            for (int seat = 0; seat < seatCount; seat++)
            {
                if (contributions[seat] >= level)
                {
                    contributors.Add(seat);
                }
            }

            int amount = (level - previousLevel) * contributors.Count;
            previousLevel = level;

            var eligible = contributors.Where(seat => !folded[seat]).ToList();
            // 与上一层资格完全相同时合并：同一批人争的钱应当是一个池。
            if (layers.Count > 0 && layers[^1].eligible.SequenceEqual(eligible))
            {
                layers[^1] = (layers[^1].amount + amount, eligible);
            }
            else
            {
                layers.Add((amount, eligible));
            }
        }

        return layers
            .Select(layer => new Pot(new Chips(layer.amount), Array.AsReadOnly(layer.eligible.ToArray())))
            .ToArray();
    }

    /// <summary>所有层金额之和，应等于参与划分的投入总额（未获跟注的退款已在此前完成）。</summary>
    public static int TotalAmount(IReadOnlyList<Pot> pots)
    {
        ArgumentNullException.ThrowIfNull(pots);
        return pots.Sum(pot => pot.Total.Amount);
    }
}
