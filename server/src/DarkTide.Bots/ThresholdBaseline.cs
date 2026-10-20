using System.Text.Json;
using System.Text.Json.Nodes;

namespace DarkTide.Bots;

/// <summary>
/// 门槛基准的文件格式（JSON）：自我对战调出来的结果写进仓库，人机加载它作为基准。
/// 门槛按 [轮次][对手数] 存成二维数组，对手数下标 0 不用，保持与 <see cref="ThresholdBotParameters"/> 一致，读的时候不用换算。
/// </summary>
public static class ThresholdBaseline
{
    public const int FormatVersion = 1;
    private const string ResourceName = "DarkTide.Bots.Data.threshold-baseline.json";

    private static readonly Lazy<ThresholdBotParameters> LazyStandard = new(() =>
    {
        using var stream = typeof(ThresholdBaseline).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"缺少嵌入的门槛基准 {ResourceName}，请运行 server/tools/DarkTide.SelfPlay tune 生成。");
        using var reader = new StreamReader(stream);
        return Load(reader.ReadToEnd());
    });

    /// <summary>随程序发布的门槛基准。返回的是共享实例，不要修改其中的数组。</summary>
    public static ThresholdBotParameters Standard => LazyStandard.Value;

    public static string Save(ThresholdBotParameters parameters)
    {
        var root = new JsonObject
        {
            ["version"] = FormatVersion,
            ["note"] = "门槛是胜率（0～1）。fold/raise 按 [一注, 二注, 三注][对手数 0～7] 排列，对手数 0 不用。由 tools/DarkTide.SelfPlay tune 生成。",
            ["fold"] = Grid(parameters.Fold),
            ["raise"] = Grid(parameters.Raise),
            ["raisePotFraction"] = parameters.RaisePotFraction,
            ["rerouteMargin"] = parameters.RerouteMargin,
            ["adjustDown"] = parameters.AdjustDown,
            ["adjustUp"] = parameters.AdjustUp,
            ["adjustLimit"] = parameters.AdjustLimit,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public static ThresholdBotParameters Load(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("门槛基准文件为空。");
        int version = root["version"]?.GetValue<int>() ?? 0;
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"不支持的门槛基准格式版本 {version}。");
        }
        return new ThresholdBotParameters
        {
            Fold = ReadGrid(root["fold"]),
            Raise = ReadGrid(root["raise"]),
            RaisePotFraction = root["raisePotFraction"]!.GetValue<double>(),
            RerouteMargin = root["rerouteMargin"]!.GetValue<double>(),
            AdjustDown = root["adjustDown"]?.GetValue<double>() ?? 0,
            AdjustUp = root["adjustUp"]?.GetValue<double>() ?? 0,
            AdjustLimit = root["adjustLimit"]?.GetValue<double>() ?? 0.10,
        };
    }

    private static JsonArray Grid(double[,] grid)
    {
        var rows = new JsonArray();
        for (int s = 0; s < grid.GetLength(0); s++)
        {
            var row = new JsonArray();
            for (int k = 0; k < grid.GetLength(1); k++)
            {
                row.Add(Math.Round(grid[s, k], 4));
            }
            rows.Add(row);
        }
        return rows;
    }

    private static double[,] ReadGrid(JsonNode? node)
    {
        var rows = node?.AsArray() ?? throw new InvalidDataException("缺少门槛表。");
        var grid = new double[3, EquityTable.MaxOpponents + 1];
        if (rows.Count != 3)
        {
            throw new InvalidDataException("门槛表必须有 3 行（三轮下注）。");
        }
        for (int s = 0; s < 3; s++)
        {
            var row = rows[s]!.AsArray();
            if (row.Count != EquityTable.MaxOpponents + 1)
            {
                throw new InvalidDataException($"门槛表每行必须有 {EquityTable.MaxOpponents + 1} 列。");
            }
            for (int k = 0; k <= EquityTable.MaxOpponents; k++)
            {
                double value = row[k]!.GetValue<double>();
                if (value is < 0 or > 1 || double.IsNaN(value))
                {
                    throw new InvalidDataException($"门槛必须在 0～1 之间，第 {s} 行第 {k} 列是 {value}。");
                }
                grid[s, k] = value;
            }
        }
        return grid;
    }
}
