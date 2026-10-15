namespace DarkTide.Rules;

/// <summary>
/// 三张牌的牌型（规则第 2 节），数值越大越强。数值将进入协议与存储：只追加，不改名、不复用编号。
/// 注意这里三条大于同花顺：27 张牌、每种数字只有三张，凑三条比同花顺难。
/// </summary>
public enum HandCategory : byte
{
    HighCard = 0,
    Pair = 1,
    Flush = 2,
    Straight = 3,
    StraightFlush = 4,
    ThreeOfAKind = 5
}
