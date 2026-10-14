namespace DarkTide.Room.Tests;

internal static class TestOptions
{
    /// <summary>以默认配置为基础，只改测试关心的几项。</summary>
    public static RoomOptions With(
        int commands = RoomOptions.DefaultCommandQueueCapacity,
        int queries = RoomOptions.DefaultQueryQueueCapacity,
        TimeSpan? shutdownTimeout = null,
        int receipts = CommandReceiptStore.DefaultCapacity) => new()
    {
        CommandQueueCapacity = commands,
        QueryQueueCapacity = queries,
        ReceiptCacheCapacity = receipts,
        ShutdownTimeout = shutdownTimeout ?? RoomOptions.DefaultShutdownTimeout
    };
}
