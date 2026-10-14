using System.Security.Cryptography;

namespace 暗潮航线.规则;

/// <summary>使用安全随机数生成标准牌库的随机排列，供可信的开局流程调用。</summary>
public static class 洗牌器
{
    public static IReadOnlyList<牌> 洗标准牌库()
    {
        var 牌序 = 牌库.创建标准牌库().ToArray();

        // Fisher–Yates：从末尾向前，当前位置也必须有机会被选中。
        for (int 当前位置 = 牌序.Length - 1; 当前位置 > 0; 当前位置--)
        {
            // 上限不包含在结果中，因此 +1 才能选到当前位置。
            int 交换位置 = RandomNumberGenerator.GetInt32(当前位置 + 1);
            (牌序[当前位置], 牌序[交换位置]) = (牌序[交换位置], 牌序[当前位置]);
        }

        // 数组留在内部，调用者只能读取洗牌结果。
        return Array.AsReadOnly(牌序);
    }
}
