using DarkTide.Rules;

namespace DarkTide.Room;

/// <summary>仅供可信开局流程使用。构造时复制输入，之后外部修改原数组不影响命令。</summary>
public sealed record DealCommand : RoomCommand
{
    private readonly Card[] _deckCopy;
    private readonly int[] _orderCopy;

    public IReadOnlyList<Card> DrawOrder { get; }
    public IReadOnlyList<int> ParticipantOrder { get; }

    public DealCommand(Guid commandId, IReadOnlyList<Card> drawOrder, IReadOnlyList<int> participantOrder, long? expectedVersion = null)
        : base(commandId, expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(drawOrder);
        ArgumentNullException.ThrowIfNull(participantOrder);
        // 只挡住过大或明显错误的载荷；内容（重复牌、座位范围）由单局完整检查。
        if (drawOrder.Count != Deck.TotalCards)
        {
            throw new ArgumentException($"必须提供 {Deck.TotalCards} 张牌的牌序。", nameof(drawOrder));
        }
        if (participantOrder.Count < TableLimits.MinParticipants || participantOrder.Count > TableLimits.MaxSeats)
        {
            throw new ArgumentException(
                $"参与人数必须在 {TableLimits.MinParticipants}～{TableLimits.MaxSeats} 之间。", nameof(participantOrder));
        }

        _deckCopy = drawOrder.ToArray();
        _orderCopy = participantOrder.ToArray();
        this.DrawOrder = Array.AsReadOnly(_deckCopy);
        this.ParticipantOrder = Array.AsReadOnly(_orderCopy);
    }

    // 记录默认按引用比较集合成员；去重需要按内容判断“是不是同一个命令”，所以自定义相等。
    public bool Equals(DealCommand? other) =>
        other is not null
        && base.Equals(other)
        && _deckCopy.AsSpan().SequenceEqual(other._deckCopy)
        && _orderCopy.AsSpan().SequenceEqual(other._orderCopy);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(base.GetHashCode());
        foreach (var card in _deckCopy)
        {
            hash.Add(card);
        }
        foreach (int seat in _orderCopy)
        {
            hash.Add(seat);
        }
        return hash.ToHashCode();
    }
}
