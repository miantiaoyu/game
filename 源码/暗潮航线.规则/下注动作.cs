namespace 暗潮航线.规则;

/// <summary>一轮下注中玩家可以选择的动作。金额一律使用整数筹码。</summary>
public enum 下注动作
{
    过牌 = 1,
    跟注 = 2,
    /// <summary>首次开注与后续加注是同一个动作，靠目标额区分。</summary>
    加注 = 3,
    弃牌 = 4,
    /// <summary>把本人剩余筹码全部投入。可能是不足额跟注，也可能是不足完整幅度的加注。</summary>
    全下 = 5
}

/// <summary>
/// 轮到某名玩家时，规则允许的所有选择。客户端据此显示按钮，房间据此校验请求。
/// 金额是本轮累计目标额，不是本次新增额。
/// </summary>
public readonly record struct 合法动作(
    bool 可过牌,
    bool 可跟注,
    int 跟注额,
    bool 可加注,
    int 最小加注目标额,
    int 最大加注目标额,
    bool 可弃牌,
    bool 可全下)
{
    public static 合法动作 无 => default;
}
