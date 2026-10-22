namespace DarkTide.Room.Tests;

internal static class ExecutorTestExtensions
{
    /// <summary>重放一串混合命令：玩家命令走公开入口，发牌等可信命令走内部入口。</summary>
    public static Task<CommandReceipt> ExecuteAnyAsync(this RoomExecutor executor, RoomCommand command) =>
        command.ActorId is null ? executor.ExecuteSystemAsync(command) : executor.ExecuteAsync(command);
}
