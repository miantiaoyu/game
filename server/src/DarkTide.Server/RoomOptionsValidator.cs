using DarkTide.Room;
using Microsoft.Extensions.Options;

namespace DarkTide.Server;

/// <summary>把 <see cref="RoomOptions.Validate"/> 接入选项系统；配合 ValidateOnStart，配置错误时进程无法启动。</summary>
internal sealed class RoomOptionsValidator : IValidateOptions<RoomOptions>
{
    public ValidateOptionsResult Validate(string? name, RoomOptions options)
    {
        var errors = options.Validate();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
