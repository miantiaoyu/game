namespace DarkTide.Rules;

/// <summary>一张创建后不能改变的牌；相同花色、点数的牌具有相同的值。</summary>
public readonly record struct Card
{
    public const int MinRank = 1;
    public const int MaxRank = 9;
    public const int RanksPerSuit = MaxRank - MinRank + 1;

    // 只保存编号。花色和点数由编号计算，避免三份数据互相矛盾。
    // 编号从 1 开始：default(牌) 的编号为 0，是“未初始化”而不是某张真牌，
    // 这样忘记赋值的数组元素能被发现，而不会冒充“红1”混进牌局。
    public byte Id { get; }

    public bool IsValid => Id != 0;

    public Suit Suit => (Suit)(_ordinal() / RanksPerSuit);
    public int Rank => _ordinal() % RanksPerSuit + MinRank;

    public Card(Suit suit, int rank)
    {
        if (!Enum.IsDefined(suit))
        {
            throw new ArgumentOutOfRangeException(
                nameof(suit), suit, "花色只能是红、蓝、绿。");
        }

        if (rank < MinRank || rank > MaxRank)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rank), rank, $"点数必须在 {MinRank} 到 {MaxRank} 之间。");
        }

        // 红牌占 1～9，蓝牌占 10～18，绿牌占 19～27。
        Id = (byte)((int)suit * RanksPerSuit + rank - MinRank + 1);
    }

    public override string ToString() => IsValid ? $"{Suit}{Rank}" : "未初始化的牌";

    // 读取未初始化的牌是程序缺陷，不是玩家能触发的业务拒绝，所以抛普通异常，让房间按故障处理。
    private int _ordinal() => IsValid
        ? Id - 1
        : throw new InvalidOperationException("读取了未初始化的牌。");
}
