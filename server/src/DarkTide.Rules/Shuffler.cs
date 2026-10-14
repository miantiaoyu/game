using System.Security.Cryptography;

namespace DarkTide.Rules;

/// <summary>使用安全随机数生成标准牌库的随机排列，供可信的开局流程调用。</summary>
public static class Shuffler
{
    public static IReadOnlyList<Card> ShuffleStandard()
    {
        var drawOrder = Deck.CreateStandard().ToArray();

        // Fisher–Yates：从末尾向前，当前位置也必须有机会被选中。
        for (int position = drawOrder.Length - 1; position > 0; position--)
        {
            // 上限不包含在结果中，因此 +1 才能选到当前位置。
            int swapWith = RandomNumberGenerator.GetInt32(position + 1);
            (drawOrder[position], drawOrder[swapWith]) = (drawOrder[swapWith], drawOrder[position]);
        }

        // 数组留在内部，调用者只能读取洗牌结果。
        return Array.AsReadOnly(drawOrder);
    }
}
