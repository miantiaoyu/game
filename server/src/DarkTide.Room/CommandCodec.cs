using System.Text.Json;
using System.Text.Json.Nodes;
using DarkTide.Rules;

namespace DarkTide.Room;

/// <summary>
/// 命令与持久格式之间的转换。格式是版本化的 JSON，字段名稳定，与 C# 属性名解耦：
/// 改名重构不会让旧日志读不出来。类型名同样是固定字符串，不是 CLR 类型名。
///
/// 发牌命令里有整副牌序。日志由服务端独占，不对玩家开放；但它仍是敏感数据，导出与备份要按受限数据处理。
/// </summary>
public static class CommandCodec
{
    public const int FormatVersion = 1;

    public static (string Kind, string Payload) Encode(RoomCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var payload = new JsonObject
        {
            ["v"] = FormatVersion,
            ["commandId"] = command.CommandId,
            ["expectedVersion"] = command.ExpectedVersion
        };

        string kind;
        switch (command)
        {
            case TakeSeatCommand takeSeat:
                kind = "take_seat";
                payload["playerId"] = takeSeat.PlayerId;
                payload["seat"] = takeSeat.Seat;
                break;
            case LeaveSeatCommand leaveSeat:
                kind = "leave_seat";
                payload["playerId"] = leaveSeat.PlayerId;
                break;
            case DealCommand deal:
                kind = "deal";
                payload["drawOrder"] = new JsonArray(deal.DrawOrder.Select(card => (JsonNode)card.Id).ToArray());
                payload["participantOrder"] = new JsonArray(deal.ParticipantOrder.Select(seat => (JsonNode)seat).ToArray());
                break;
            default:
                // 新增命令却忘了在这里登记：写不进日志就不能执行，作为故障暴露。
                throw new InvalidOperationException($"未登记持久格式的命令类型：{command.GetType().Name}。");
        }

        return (kind, payload.ToJsonString());
    }

    public static RoomCommand Decode(string kind, string payload)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(payload);
        var root = JsonNode.Parse(payload)?.AsObject()
            ?? throw new JsonException("命令载荷不是 JSON 对象。");
        int version = root["v"]?.GetValue<int>() ?? throw new JsonException("命令载荷缺少版本号。");
        if (version != FormatVersion)
        {
            throw new JsonException($"不支持的命令格式版本 {version}。");
        }

        var commandId = Required<Guid>(root, "commandId");
        long? expectedVersion = root["expectedVersion"]?.GetValue<long>();
        return kind switch
        {
            "take_seat" => new TakeSeatCommand(commandId, Required<Guid>(root, "playerId"), Required<int>(root, "seat"), expectedVersion),
            "leave_seat" => new LeaveSeatCommand(commandId, Required<Guid>(root, "playerId"), expectedVersion),
            "deal" => new DealCommand(
                commandId,
                RequiredArray(root, "drawOrder").Select(node => Card.FromId(node!.GetValue<int>())).ToArray(),
                RequiredArray(root, "participantOrder").Select(node => node!.GetValue<int>()).ToArray(),
                expectedVersion),
            _ => throw new JsonException($"未知的命令类型 {kind}。")
        };
    }

    private static T Required<T>(JsonObject root, string name) =>
        root[name] is { } node ? node.GetValue<T>() : throw new JsonException($"命令载荷缺少字段 {name}。");

    private static JsonArray RequiredArray(JsonObject root, string name) =>
        root[name] as JsonArray ?? throw new JsonException($"命令载荷缺少数组 {name}。");
}
