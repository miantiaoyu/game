using System.Diagnostics.CodeAnalysis;
using DarkTide.Rules;

namespace DarkTide.Room;

/// <summary>
/// 记住最近处理过的命令及其唯一结果，用于识别重发。
/// 只保留有限条数：重试通常发生在几秒内，不需要永久记忆；内存按房间数线性增长，必须有上限。
/// 接入数据库后，回执与状态在同一事务保存，内存表只作为缓存。
/// 只由房间的单个消费循环访问，不需要加锁。
/// </summary>
internal sealed class CommandReceiptStore
{
    // 一整场 12 局的命令约数百条，而重发通常在几秒内；128 条足够覆盖重试窗口。
    public const int DefaultCapacity = 128;

    private readonly int _capacity;
    private readonly Dictionary<Guid, Entry> _byId;
    // 按到达顺序排列的编号，满了淘汰最早的。字典本身不保证顺序，所以另存一份。
    private readonly Queue<Guid> _arrivalOrder;

    public CommandReceiptStore(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this._capacity = capacity;
        // 不按容量预分配：空闲房间很多，预分配会让每个房间都先占满内存；按需扩容的成本只发生几次。
        _byId = new Dictionary<Guid, Entry>();
        _arrivalOrder = new Queue<Guid>();
    }

    public int Count => _byId.Count;

    /// <summary>是否淘汰过记录。淘汰过时，缓存未命中不再代表“没处理过”，需要查持久日志。</summary>
    public bool HasEvicted { get; private set; }

    public bool TryGet(Guid commandId, [MaybeNullWhen(false)] out Entry existingEntry) =>
        _byId.TryGetValue(commandId, out existingEntry);

    public void RecordSuccess(RoomCommand command, CommandReceipt receipt) => Add(new Entry(command, receipt, default, null));

    public void RecordRejection(RoomCommand command, RuleRejectedException rejection) => Add(new Entry(command, null, rejection.Reason, rejection.Message));

    private void Add(Entry newEntry)
    {
        if (_byId.Count == _capacity)
        {
            _byId.Remove(_arrivalOrder.Dequeue());
            HasEvicted = true;
        }
        _byId.Add(newEntry.command.CommandId, newEntry);
        _arrivalOrder.Enqueue(newEntry.command.CommandId);
    }

    /// <summary>只保存拒绝原因和说明，不保存异常对象：同一异常实例被多次抛出会互相覆盖堆栈信息。</summary>
    internal sealed record Entry(RoomCommand command, CommandReceipt? receipt, RejectionReason reason, string? message)
    {
        public CommandReceipt ReplayResult() => receipt ?? throw new RuleRejectedException(reason, message!);
    }
}
