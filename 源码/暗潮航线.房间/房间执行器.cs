using System.Threading.Channels;
using 暗潮航线.规则;

namespace 暗潮航线.房间;

/// <summary>
/// 多个调用方可以提交请求，同一房间只有一个消费循环执行请求。
/// 当前仅提供进程内队列；身份认证和持久提交尚未接入。
/// 因此任务成功只代表内存状态已改变，不代表已经保存。
/// </summary>
public sealed class 房间执行器 : IAsyncDisposable
{
    public const int 最大等待命令数 = 56;
    // 查询只读、可以随时重发，额度小；与命令分开计数，频繁刷新视图挤不掉真正的操作。
    // 超时等内部事件接入时，再按同样方式增加第三类额度（设计为 8）。
    public const int 最大等待查询数 = 8;

    private readonly 房间 内部房间;
    private readonly Channel<排队请求> 队列;
    private readonly Task 处理任务;
    private readonly int 命令容量;
    private readonly int 查询容量;
    private int 等待命令数;
    private int 等待查询数;
    private int 停止接收;
    // 消费循环先写入故障，再置位停止接收；提交方看到停止后再读故障，就能读到写入的值。
    private Exception? 故障;

    /// <summary>创建空房间并开始处理。</summary>
    public 房间执行器(Guid 房间编号, int 座位总数, int 命令队列容量 = 最大等待命令数)
        : this(new 房间(房间编号, 座位总数), 命令队列容量, Task.CompletedTask)
    {
    }

    /// <summary>
    /// 接管一个已有房间，例如以后从持久状态重建的房间。
    /// 调用后由执行器独占该对象，调用方不得再直接调用它的方法，否则会绕过队列。
    /// </summary>
    public 房间执行器(房间 初始房间, int 命令队列容量 = 最大等待命令数)
        : this(初始房间, 命令队列容量, Task.CompletedTask)
    {
    }

    // 测试可以暂缓消费，以确定性验证满队列和关闭时的行为。
    // 正常构造传入已完成任务，立即开始等待请求。
    internal 房间执行器(房间 初始房间, int 命令队列容量, Task 开始处理信号, int 查询队列容量 = 最大等待查询数)
    {
        ArgumentNullException.ThrowIfNull(初始房间);
        ArgumentNullException.ThrowIfNull(开始处理信号);
        if (命令队列容量 < 1 || 命令队列容量 > 最大等待命令数)
        {
            throw new ArgumentOutOfRangeException(nameof(命令队列容量), 命令队列容量, $"命令队列容量必须在 1～{最大等待命令数} 之间。");
        }
        if (查询队列容量 < 1 || 查询队列容量 > 最大等待查询数)
        {
            throw new ArgumentOutOfRangeException(nameof(查询队列容量), 查询队列容量, $"查询队列容量必须在 1～{最大等待查询数} 之间。");
        }

        内部房间 = 初始房间;
        命令容量 = 命令队列容量;
        查询容量 = 查询队列容量;
        // 命令和查询共用一条队列，才能保证“先提交的修改先生效，后提交的查询能看到它”。
        // 名额由两个计数器分别控制；队列容量取两者之和，只作为最后一道保护。
        队列 = Channel.CreateBounded<排队请求>(new BoundedChannelOptions(命令容量 + 查询容量)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        处理任务 = 处理队列异步(开始处理信号);
    }

    /// <summary>房间编号创建后不变，可以在任意线程读取。</summary>
    public Guid 房间编号 => 内部房间.房间编号;

    /// <summary>
    /// 正常关闭或故障停止后完成，本身不会以异常结束。
    /// 目录模块等待它，再根据 故障原因 决定摘除还是重建房间。
    /// </summary>
    public Task 已停止 => 处理任务;

    /// <summary>为空表示没有发生故障（仍在运行或已正常关闭）。</summary>
    public Exception? 故障原因 => Volatile.Read(ref 故障);

    /// <summary>命令在构造时已检查格式并复制输入，所以这里可以直接入队。</summary>
    public Task<命令回执> 执行异步(房间命令 命令)
    {
        ArgumentNullException.ThrowIfNull(命令);
        return 提交(new 命令请求(命令), ref 等待命令数, 命令容量);
    }

    // 查询的参数契约不依赖房间状态，在调用方线程检查：不占名额，也不会在队列里被当成故障。
    public Task<int?> 查找座位异步(Guid 玩家编号)
    {
        检查玩家编号(玩家编号);
        return 提交(new 查询请求<int?>(当前房间 => 当前房间.查找座位(玩家编号)), ref 等待查询数, 查询容量);
    }

    public Task<玩家底牌视图> 获取本人视图异步(Guid 玩家编号)
    {
        检查玩家编号(玩家编号);
        return 提交(new 查询请求<玩家底牌视图>(当前房间 => 当前房间.获取本人视图(玩家编号)), ref 等待查询数, 查询容量);
    }

    public Task<房间概况> 获取概况异步() =>
        提交(new 查询请求<房间概况>(当前房间 => 当前房间.生成概况()), ref 等待查询数, 查询容量);

    private Task<T> 提交<T>(排队请求<T> 请求, ref int 等待数, int 上限)
    {
        if (Volatile.Read(ref 停止接收) != 0)
        {
            请求.拒绝(创建停止异常());
        }
        // 先占名额再入队。Interlocked 让多个提交方同时占位时也不会超过上限；超了就退回名额。
        else if (Interlocked.Increment(ref 等待数) > 上限)
        {
            Interlocked.Decrement(ref 等待数);
            请求.拒绝(Volatile.Read(ref 停止接收) != 0 ? 创建停止异常() : new 房间繁忙异常());
        }
        // 名额已保证队列有空位，TryWrite 只会因为队列已关闭而失败。
        else if (!队列.Writer.TryWrite(请求))
        {
            Interlocked.Decrement(ref 等待数);
            请求.拒绝(创建停止异常());
        }
        return 请求.结果;
    }

    // 让调用方区分“房间正常关闭”和“房间坏了”，两者的上层处理完全不同。
    private Exception 创建停止异常() => Volatile.Read(ref 故障) is { } 原因
        ? new 房间故障异常(原因)
        : new ObjectDisposedException(nameof(房间执行器), "房间执行器已停止接收请求。");

    private async Task 处理队列异步(Task 开始处理信号)
    {
        try
        {
            await 开始处理信号.ConfigureAwait(false);
            await foreach (var 请求 in 队列.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                // 离开队列即归还名额：额度限制的是“排队等待”的请求数。
                if (请求.是查询)
                {
                    Interlocked.Decrement(ref 等待查询数);
                }
                else
                {
                    Interlocked.Decrement(ref 等待命令数);
                }
                请求.执行(内部房间);
            }
        }
        catch (Exception 错误)
        {
            // 不再向外抛：故障通过 故障原因 公开。若让任务以异常结束而无人等待，
            // 异常只会在垃圾回收时以“未观察的任务异常”出现，目录模块无从得知。
            Volatile.Write(ref 故障, 错误);
        }
        finally
        {
            Interlocked.Exchange(ref 停止接收, 1);
            队列.Writer.TryComplete();
            // 意外故障时，使每个已接收请求都结束等待，不继续操作可疑状态。
            while (队列.Reader.TryRead(out var 请求))
            {
                请求.拒绝(创建停止异常());
            }
        }
    }

    /// <summary>停止接收，等待已接收的请求全部处理完成；可以重复调用，不会因房间故障而抛出。</summary>
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref 停止接收, 1);
        队列.Writer.TryComplete();
        return new ValueTask(处理任务);
    }

    private static void 检查玩家编号(Guid 玩家编号)
    {
        if (玩家编号 == Guid.Empty)
        {
            throw new ArgumentException("玩家编号不能为空编号。", nameof(玩家编号));
        }
    }

    private abstract class 排队请求
    {
        public abstract bool 是查询 { get; }
        public abstract void 执行(房间 当前房间);
        public abstract void 拒绝(Exception 原因);
    }

    private abstract class 排队请求<T> : 排队请求
    {
        private readonly TaskCompletionSource<T> 回信 = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<T> 结果 => 回信.Task;

        protected abstract T 运行(房间 当前房间);

        public override void 执行(房间 当前房间)
        {
            try
            {
                回信.TrySetResult(运行(当前房间));
            }
            catch (规则拒绝异常 错误)
            {
                // 规则约定：抛出拒绝前没有改动状态，所以后续请求可以继续。
                拒绝(错误);
            }
            catch (Exception 错误)
            {
                // 其他异常无法确认状态是否改了一半，必须停止这个房间。
                拒绝(错误);
                throw;
            }
        }

        public override void 拒绝(Exception 原因) => 回信.TrySetException(原因);
    }

    private sealed class 命令请求(房间命令 命令) : 排队请求<命令回执>
    {
        public override bool 是查询 => false;
        protected override 命令回执 运行(房间 当前房间) => 当前房间.执行(命令);
    }

    // 查询不改变状态，不需要去重、记录或持久化，用委托表达就够了。
    private sealed class 查询请求<T>(Func<房间, T> 查询) : 排队请求<T>
    {
        public override bool 是查询 => true;
        protected override T 运行(房间 当前房间) => 查询(当前房间);
    }
}
