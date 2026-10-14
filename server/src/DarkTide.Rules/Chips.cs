namespace DarkTide.Rules;

/// <summary>
/// 筹码数量。最小单位 1 枚，只能是非负数，扣减不能扣成负数。
/// 用值类型而不是裸 int：让“不能为负”这件事由类型保证，而不是靠每处调用者自己记得检查。
/// </summary>
public readonly struct Chips : IEquatable<Chips>, IComparable<Chips>
{
    public int Amount { get; }

    public Chips(int amount)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "筹码数量不能为负数。");
        }

        this.Amount = amount;
    }

    public static Chips Zero => default;

    public static Chips Add(Chips left, Chips right) => new(left.Amount + right.Amount);

    /// <summary>扣减超过现有数量时抛出：钱不会凭空变成负数，出现这种情况说明账算错了。</summary>
    public static Chips Subtract(Chips left, Chips right) => left.Amount < right.Amount
        ? throw new InvalidOperationException($"筹码不足：现有 {left.Amount}，需要扣除 {right.Amount}。")
        : new Chips(left.Amount - right.Amount);

    public static Chips Min(Chips left, Chips right) => left.Amount <= right.Amount ? left : right;

    /// <summary>按比例向上取整，用于改航费：宁可多收一枚，也不因为取整少收。</summary>
    public static Chips ScaleUp(Chips basis, int perMille)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(perMille);
        // 先用长整型相乘，避免基准较大时中间结果溢出。
        long product = (long)basis.Amount * perMille;
        return new Chips((int)((product + 999) / 1000));
    }

    public bool Equals(Chips other) => Amount == other.Amount;
    public override bool Equals(object? obj) => obj is Chips other && Equals(other);
    public override int GetHashCode() => Amount;
    public int CompareTo(Chips other) => Amount.CompareTo(other.Amount);

    public static bool operator ==(Chips left, Chips right) => left.Amount == right.Amount;
    public static bool operator !=(Chips left, Chips right) => left.Amount != right.Amount;
    public static bool operator <(Chips left, Chips right) => left.Amount < right.Amount;
    public static bool operator >(Chips left, Chips right) => left.Amount > right.Amount;
    public static bool operator <=(Chips left, Chips right) => left.Amount <= right.Amount;
    public static bool operator >=(Chips left, Chips right) => left.Amount >= right.Amount;

    public override string ToString() => Amount.ToString();
}
