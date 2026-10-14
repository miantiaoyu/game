using System.Diagnostics.CodeAnalysis;
using 暗潮航线.规则;

namespace 暗潮航线.房间;

/// <summary>
/// 记住最近处理过的命令及其唯一结果，用于识别重发。
/// 只保留有限条数：重试通常发生在几秒内，不需要永久记忆；内存按房间数线性增长，必须有上限。
/// 接入数据库后，回执与状态在同一事务保存，内存表只作为缓存。
/// 只由房间的单个消费循环访问，不需要加锁。
/// </summary>
internal sealed class 命令记录表
{
    // 一整场 12 局的命令约数百条，而重发通常在几秒内；128 条足够覆盖重试窗口。
    public const int 默认容量 = 128;

    private readonly int 容量;
    private readonly Dictionary<Guid, 记录> 按编号;
    // 按到达顺序排列的编号，满了淘汰最早的。字典本身不保证顺序，所以另存一份。
    private readonly Queue<Guid> 到达顺序;

    public 命令记录表(int 容量 = 默认容量)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(容量, 1);
        this.容量 = 容量;
        // 不按容量预分配：空闲房间很多，预分配会让每个房间都先占满内存；按需扩容的成本只发生几次。
        按编号 = new Dictionary<Guid, 记录>();
        到达顺序 = new Queue<Guid>();
    }

    public int 条数 => 按编号.Count;

    public bool 尝试查找(Guid 命令编号, [MaybeNullWhen(false)] out 记录 已有记录) =>
        按编号.TryGetValue(命令编号, out 已有记录);

    public void 记录成功(房间命令 命令, 命令回执 回执) => 加入(new 记录(命令, 回执, default, null));

    public void 记录拒绝(房间命令 命令, 规则拒绝异常 拒绝) => 加入(new 记录(命令, null, 拒绝.原因, 拒绝.Message));

    private void 加入(记录 新记录)
    {
        if (按编号.Count == 容量)
        {
            按编号.Remove(到达顺序.Dequeue());
        }
        按编号.Add(新记录.命令.命令编号, 新记录);
        到达顺序.Enqueue(新记录.命令.命令编号);
    }

    /// <summary>只保存拒绝原因和说明，不保存异常对象：同一异常实例被多次抛出会互相覆盖堆栈信息。</summary>
    internal sealed record 记录(房间命令 命令, 命令回执? 回执, 拒绝原因 原因, string? 说明)
    {
        public 命令回执 重现结果() => 回执 ?? throw new 规则拒绝异常(原因, 说明!);
    }
}
