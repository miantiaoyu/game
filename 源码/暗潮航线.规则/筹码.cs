namespace 暗潮航线.规则;

/// <summary>
/// 筹码数量。最小单位 1 枚，只能是非负数，扣减不能扣成负数。
/// 用值类型而不是裸 int：让“不能为负”这件事由类型保证，而不是靠每处调用者自己记得检查。
/// </summary>
public readonly struct 筹码 : IEquatable<筹码>, IComparable<筹码>
{
    public int 数量 { get; }

    public 筹码(int 数量)
    {
        if (数量 < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(数量), 数量, "筹码数量不能为负数。");
        }

        this.数量 = 数量;
    }

    public static 筹码 零 => default;

    public static 筹码 加(筹码 左, 筹码 右) => new(左.数量 + 右.数量);

    /// <summary>扣减超过现有数量时抛出：钱不会凭空变成负数，出现这种情况说明账算错了。</summary>
    public static 筹码 减(筹码 左, 筹码 右) => 左.数量 < 右.数量
        ? throw new InvalidOperationException($"筹码不足：现有 {左.数量}，需要扣除 {右.数量}。")
        : new 筹码(左.数量 - 右.数量);

    public static 筹码 取较小(筹码 左, 筹码 右) => 左.数量 <= 右.数量 ? 左 : 右;

    /// <summary>按比例向上取整，用于改航费：宁可多收一枚，也不因为取整少收。</summary>
    public static 筹码 向上取整比例(筹码 基准, int 千分比)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(千分比);
        // 先用长整型相乘，避免基准较大时中间结果溢出。
        long 乘积 = (long)基准.数量 * 千分比;
        return new 筹码((int)((乘积 + 999) / 1000));
    }

    public bool Equals(筹码 其他) => 数量 == 其他.数量;
    public override bool Equals(object? 其他) => 其他 is 筹码 另一个 && Equals(另一个);
    public override int GetHashCode() => 数量;
    public int CompareTo(筹码 其他) => 数量.CompareTo(其他.数量);

    public static bool operator ==(筹码 左, 筹码 右) => 左.数量 == 右.数量;
    public static bool operator !=(筹码 左, 筹码 右) => 左.数量 != 右.数量;
    public static bool operator <(筹码 左, 筹码 右) => 左.数量 < 右.数量;
    public static bool operator >(筹码 左, 筹码 右) => 左.数量 > 右.数量;
    public static bool operator <=(筹码 左, 筹码 右) => 左.数量 <= 右.数量;
    public static bool operator >=(筹码 左, 筹码 右) => 左.数量 >= 右.数量;

    public override string ToString() => 数量.ToString();
}
