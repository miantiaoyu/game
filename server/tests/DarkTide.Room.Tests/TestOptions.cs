namespace DarkTide.Room.Tests;

internal static class TestOptions
{
    /// <summary>以默认配置为基础，只改测试关心的几项。</summary>
    public static RoomOptions With(
        int commands = RoomOptions.DefaultCommandQueueCapacity,
        int queries = RoomOptions.DefaultQueryQueueCapacity,
        TimeSpan? shutdownTimeout = null,
        int receipts = CommandReceiptStore.DefaultCapacity,
        TimeSpan? journalTimeout = null,
        double playerCommandsPerSecond = RoomOptions.DefaultPlayerCommandsPerSecond,
        int playerCommandBurst = RoomOptions.DefaultPlayerCommandBurst,
        double playerQueriesPerSecond = RoomOptions.DefaultPlayerQueriesPerSecond,
        int playerQueryBurst = RoomOptions.DefaultPlayerQueryBurst) => new()
    {
        PlayerCommandsPerSecond = playerCommandsPerSecond,
        PlayerCommandBurst = playerCommandBurst,
        PlayerQueriesPerSecond = playerQueriesPerSecond,
        PlayerQueryBurst = playerQueryBurst,
        JournalTimeout = journalTimeout ?? RoomOptions.DefaultJournalTimeout,
        CommandQueueCapacity = commands,
        QueryQueueCapacity = queries,
        ReceiptCacheCapacity = receipts,
        ShutdownTimeout = shutdownTimeout ?? RoomOptions.DefaultShutdownTimeout
    };
}
