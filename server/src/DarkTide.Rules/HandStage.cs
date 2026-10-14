namespace DarkTide.Rules;

/// <summary>
/// 一局牌的操作轮次，按规则第 3 节的固定顺序推进。
/// 数值将进入协议与存储：只追加，不改名、不复用编号。
/// </summary>
public enum HandStage
{
    Ante = 1,
    FirstBet = 2,
    SecondBet = 3,
    Reroute = 4,
    ThirdBet = 5,
    Showdown = 6
}
