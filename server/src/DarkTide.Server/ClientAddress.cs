using System.Net;
using System.Net.Sockets;

namespace DarkTide.Server;

/// <summary>
/// 限流用的来源地址键。IPv6 按 /64 前缀归并：一个家庭宽带或云主机通常分到整个 /64（2^64 个地址），
/// 按完整地址限流时攻击者换个地址就绕过了，而且每个地址一个桶会把内存耗尽。
/// 在反向代理后面时，RemoteIpAddress 由 ForwardedHeaders 中间件按受信代理改写；没有配置受信代理时它是代理自己的地址。
/// </summary>
internal static class ClientAddress
{
    public static string PartitionKey(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            // 没有网络地址（例如进程内测试宿主）：归入同一个桶，不能因此绕过限流。
            return "unknown";
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Span<byte> bytes = stackalloc byte[16];
            address.TryWriteBytes(bytes, out _);
            bytes[8..].Clear();
            return new IPAddress(bytes).ToString() + "/64";
        }
        return address.ToString();
    }
}
