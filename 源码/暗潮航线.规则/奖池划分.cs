namespace 暗潮航线.规则;

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
public static class 奖池划分
{
    /// <param name="累计投入">按座位号索引，每个座位在本局的累计普通投入（含底注与各轮下注）。</param>
    /// <param name="已弃牌">按座位号索引，该座位本局是否已弃牌。</param>
    public static IReadOnlyList<奖池> 划分普通池(IReadOnlyList<int> 累计投入, IReadOnlyList<bool> 已弃牌)
    {
        ArgumentNullException.ThrowIfNull(累计投入);
        ArgumentNullException.ThrowIfNull(已弃牌);
        if (累计投入.Count != 已弃牌.Count)
        {
            throw new ArgumentException("投入数组与弃牌数组的长度必须一致。", nameof(已弃牌));
        }

        int 座位数 = 累计投入.Count;
        for (int 座位 = 0; 座位 < 座位数; 座位++)
        {
            if (累计投入[座位] < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(累计投入), 累计投入[座位], "累计投入不能为负数。");
            }
        }

        var 分层 = new List<(int 金额, List<int> 资格)>();
        int 上一层 = 0;
        foreach (int 本层 in 累计投入.Where(金额 => 金额 > 0).Distinct().Order())
        {
            var 出钱者 = new List<int>();
            for (int 座位 = 0; 座位 < 座位数; 座位++)
            {
                if (累计投入[座位] >= 本层)
                {
                    出钱者.Add(座位);
                }
            }

            int 金额 = (本层 - 上一层) * 出钱者.Count;
            上一层 = 本层;

            var 资格 = 出钱者.Where(座位 => !已弃牌[座位]).ToList();
            // 与上一层资格完全相同时合并：同一批人争的钱应当是一个池。
            if (分层.Count > 0 && 分层[^1].资格.SequenceEqual(资格))
            {
                分层[^1] = (分层[^1].金额 + 金额, 资格);
            }
            else
            {
                分层.Add((金额, 资格));
            }
        }

        return 分层
            .Select(层 => new 奖池(new 筹码(层.金额), Array.AsReadOnly(层.资格.ToArray())))
            .ToArray();
    }

    /// <summary>所有层金额之和，应等于参与划分的投入总额（未获跟注的退款已在此前完成）。</summary>
    public static int 合计金额(IReadOnlyList<奖池> 奖池们)
    {
        ArgumentNullException.ThrowIfNull(奖池们);
        return 奖池们.Sum(池 => 池.金额.数量);
    }
}
