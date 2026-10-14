namespace DarkTide.Server;

internal static class ServerDefaults
{
    public const string ServiceName = "darktide-server";
    public const string LiveTag = "live";
    public const string ReadyTag = "ready";

    /// <summary>配置无效时的退出码。取 78（sysexits.h 的 EX_CONFIG），systemd 等工具能识别为配置错误。</summary>
    public const int ExitCodeInvalidConfiguration = 78;

    /// <summary>宿主总时限比房间排空时限多留的余量：留给 Kestrel 关闭连接与日志落盘。</summary>
    public static readonly TimeSpan ShutdownMargin = TimeSpan.FromSeconds(5);
}
