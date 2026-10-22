using Microsoft.Extensions.Options;

namespace DarkTide.Server;

/// <summary>把 <see cref="GuestSessionOptions.Validate"/> 接入选项系统；配合 ValidateOnStart，没配密钥时进程无法启动。</summary>
internal sealed class GuestSessionOptionsValidator : IValidateOptions<GuestSessionOptions>
{
    public ValidateOptionsResult Validate(string? name, GuestSessionOptions options)
    {
        var errors = options.Validate();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
