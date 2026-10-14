namespace DarkTide.Rules;

/// <summary>从确定的标准牌序中依次抽牌。同一个实例由单个执行者顺序调用。</summary>
public sealed class DrawPile
{
    private readonly Card[] _drawOrder;
    private int _nextIndex;

    public int RemainingCount => _drawOrder.Length - _nextIndex;

    public DrawPile(IReadOnlyList<Card> orderedCards)
    {
        ArgumentNullException.ThrowIfNull(orderedCards);

        // 自己保存一份副本，外部随后修改传入的数组也不会影响抽牌。
        _drawOrder = orderedCards.ToArray();

        // 必须单独检查有效性：一张未初始化的牌与所有真牌都不相等，只查重复会放它通过。
        if (_drawOrder.Length != Deck.TotalCards || _drawOrder.Any(card => !card.IsValid)
            || _drawOrder.Distinct().Count() != Deck.TotalCards)
        {
            throw new ArgumentException($"牌序必须包含完整且不重复的 {Deck.TotalCards} 张标准牌。", nameof(orderedCards));
        }
    }

    /// <summary>返回下一张牌；抽完后返回 null，位置不再前进。</summary>
    public Card? DrawNext()
    {
        if (_nextIndex >= _drawOrder.Length)
        {
            return null;
        }

        var card = _drawOrder[_nextIndex];
        _nextIndex++;
        return card;
    }
}
