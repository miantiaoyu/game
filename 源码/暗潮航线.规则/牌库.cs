namespace 暗潮航线.规则;

/// <summary>按固定顺序生成标准规则使用的完整牌库。</summary>
public static class 牌库
{
    public const int 标准牌数 = 27;

    public static IReadOnlyList<牌> 创建标准牌库()
    {
        var 牌组 = new List<牌>(标准牌数);

        // 外层每次选一种花色，内层为它生成 1～9。
        foreach (var 当前花色 in Enum.GetValues<花色>())
        {
            for (int 当前点数 = 牌.最小点数; 当前点数 <= 牌.最大点数; 当前点数++)
            {
                牌组.Add(new 牌(当前花色, 当前点数));
            }
        }

        // 只把只读视图交给调用者，原始列表留在方法内部。
        return 牌组.AsReadOnly();
    }
}
