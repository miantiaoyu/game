namespace DarkTide.Room;

/// <summary>命令成功的结果。重发同一命令得到的是同一份回执，版本号不会再次增加。</summary>
public sealed record CommandReceipt(Guid CommandId, long CommittedVersion);
