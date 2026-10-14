namespace DarkTide.Rules;

/// <summary>一轮下注中玩家可以选择的动作。金额一律使用整数筹码。</summary>
public enum BettingAction
{
    Check = 1,
    Call = 2,
    /// <summary>首次开注与后续加注是同一个动作，靠目标额区分。</summary>
    Raise = 3,
    Fold = 4,
    /// <summary>把本人剩余筹码全部投入。可能是不足额跟注，也可能是不足完整幅度的加注。</summary>
    AllIn = 5
}

/// <summary>
/// 轮到某名玩家时，规则允许的所有选择。客户端据此显示按钮，房间据此校验请求。
/// 金额是本轮累计目标额，不是本次新增额。
/// </summary>
public readonly record struct LegalActions(
    bool CanCheck,
    bool CanCall,
    int CallAmount,
    bool CanRaise,
    int MinRaiseTarget,
    int MaxRaiseTarget,
    bool CanFold,
    bool CanGoAllIn)
{
    public static LegalActions None => default;
}
