using System.Diagnostics;
using System.Text;
using DarkTide.Bots;
using DarkTide.Rules;

// 用法：dotnet run -c Release --project server/tools/DarkTide.EquityTables -- <输出目录>
// 生成 equity-tables.bin.gz（程序使用）与 preflop-equity.csv（人读的起手牌表）。
// 同一份代码、同一个种子，生成的文件逐字节相同。
string outputDirectory = args.Length > 0 ? args[0] : "server/src/DarkTide.Bots/Data";
Directory.CreateDirectory(outputDirectory);
string binaryPath = Path.Combine(outputDirectory, "equity-tables.bin.gz");

var total = Stopwatch.StartNew();
await using (var file = File.Create(binaryPath))
{
    EquityTableBuilder.Write(file, EquityTableBuilder.DefaultSeed, EquityTableBuilder.DefaultSamples, Console.WriteLine);
}
Console.WriteLine($"写入 {binaryPath}（{new FileInfo(binaryPath).Length / 1024.0:F0} KiB），共 {total.Elapsed.TotalSeconds:F1} s");

EquityTable table;
await using (var file = File.OpenRead(binaryPath))
{
    table = EquityTable.Load(file);
}

// 起手牌 81 类：每类注明形态与原始组合数，按单挑胜率从高到低排列。
var csv = new StringBuilder();
csv.AppendLine("底牌,形态,组合数," + string.Join(",", Enumerable.Range(1, EquityTable.MaxOpponents).Select(k => $"{k}名对手")));
var rows = HandClass.Enumerate(2)
    .Select(entry => (entry.Key, entry.Weight, Cards: HandClass.Decode(entry.Key).HoleCards))
    .OrderByDescending(row => table.LookupByKey(row.Key, 1));
foreach (var (key, weight, cards) in rows)
{
    int high = Math.Max(cards[0].Rank, cards[1].Rank), low = Math.Min(cards[0].Rank, cards[1].Rank);
    string shape = high == low ? "对子" : cards[0].Suit == cards[1].Suit ? "同花" : "杂色";
    csv.Append($"{high}-{low},{shape},{weight}");
    for (int k = 1; k <= EquityTable.MaxOpponents; k++)
    {
        csv.Append($",{table.LookupByKey(key, k):F4}");
    }
    csv.AppendLine();
}
string csvPath = Path.Combine(outputDirectory, "preflop-equity.csv");
await File.WriteAllTextAsync(csvPath, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
Console.WriteLine($"写入 {csvPath}");
