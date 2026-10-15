using DarkTide.Rules;

namespace DarkTide.Room;

/// <summary>
/// 会修改房间状态的请求。命令是数据而不是代码：可以比较、记录、去重，以后也能持久化和重放。
/// 构造函数检查格式，所以拿到的命令对象一定是格式合法的；是否被当前状态允许由房间判断。
/// 属性只有 get，<c>with</c> 表达式无法绕过检查改出非法命令。
/// </summary>
public abstract record RoomCommand
{
    /// <summary>调用方生成；同一编号只会产生一次效果，重发返回原结果。</summary>
    public Guid CommandId { get; }

    /// <summary>调用方提交时看到的状态版本；为空表示不检查（仅限可信流程）。</summary>
    public long? ExpectedVersion { get; }

    /// <summary>
    /// 发出命令的玩家，用于每玩家的未完成名额与限流。为空表示可信流程发出的命令（例如发牌），不占玩家名额。
    /// 计算属性，不参与记录的相等比较，也不写入持久格式。
    /// </summary>
    public virtual Guid? ActorId => null;

    // private protected：只有本程序集能派生，命令种类是封闭集合，房间可以穷举处理。
    private protected RoomCommand(Guid commandId, long? expectedVersion)
    {
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("命令编号不能为空编号。", nameof(commandId));
        }
        if (expectedVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion), expectedVersion, "预期版本不能为负数。");
        }

        this.CommandId = commandId;
        this.ExpectedVersion = expectedVersion;
    }

    private protected static void RequirePlayerId(Guid playerId)
    {
        if (playerId == Guid.Empty)
        {
            throw new ArgumentException("玩家编号不能为空编号。", nameof(playerId));
        }
    }
}
