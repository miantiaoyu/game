namespace DarkTide.Server.Tests;

/// <summary>测试专用签名密钥。固定值只为可复现；生产密钥由环境变量或密钥管理注入，永远不进代码库。</summary>
internal static class TestSessionKeys
{
    public const string Id = "test-1";
    // 32 字节，Base64。
    public const string Secret = "dGVzdC1vbmx5LXNpZ25pbmcta2V5LTMyLWJ5dGVzISE=";

    public static readonly (string Key, string Value)[] Settings =
    [
        ("Session:Keys:0:Id", Id),
        ("Session:Keys:0:Secret", Secret),
    ];

    public static readonly IReadOnlyDictionary<string, string> Environment = new Dictionary<string, string>
    {
        ["Session__Keys__0__Id"] = Id,
        ["Session__Keys__0__Secret"] = Secret,
    };
}
