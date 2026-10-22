using Microsoft.AspNetCore.Http.Features;

namespace DarkTide.Server;

/// <summary>
/// 全服务的请求体上限。目前所有 HTTP 接口的输入都不到 100 字节；Kestrel 默认允许 30 MB，留着就是给人白送内存和带宽。
/// 两层：Kestrel 本身的上限（对分块传输等没有 Content-Length 的请求也生效），
/// 以及这里在读取前按 Content-Length 直接拒绝——不依赖具体服务器实现，进程内测试宿主同样生效。
/// 实时对局走 WebSocket，消息大小在那一层单独限制，不受这里影响。
/// </summary>
internal static class RequestBodyLimit
{
    public const long MaxBytes = 16 * 1024;

    public static IApplicationBuilder UseRequestBodyLimit(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Request.ContentLength > MaxBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = MaxBytes;
        }
        await next(context);
    });
}
