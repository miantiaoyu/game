using DarkTide.Rules;

namespace DarkTide.Room;

/// <summary>
/// 某一名观看者此刻能看到的全部内容：公开概况，加上他自己的座位与底牌。
/// 别人的底牌只在摊牌时出现在概况的结算里（规则第 3 节），这里从不包含。
/// </summary>
/// <param name="Seat">观看者的座位；没有入座为空。</param>
/// <param name="HoleCards">观看者自己的底牌；没有入座、本局未参与或还没发牌为空。</param>
public sealed record ViewerSnapshot(RoomOverview Overview, int? Seat, IReadOnlyList<Card>? HoleCards)
{
    // record 的默认 ToString 会打印底牌：快照可能出现在日志或异常里。
    public override string ToString() =>
        $"{nameof(ViewerSnapshot)} {{ Version = {Overview.StateVersion}, {nameof(Seat)} = {Seat?.ToString() ?? "none"}, {nameof(HoleCards)} = {HoleCards?.Count ?? 0} cards }}";
}
