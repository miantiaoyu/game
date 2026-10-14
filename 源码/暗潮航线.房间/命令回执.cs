namespace 暗潮航线.房间;

/// <summary>命令成功的结果。重发同一命令得到的是同一份回执，版本号不会再次增加。</summary>
public sealed record 命令回执(Guid 命令编号, long 提交后版本);
