using System.Diagnostics;
using DarkTide.Bots;

/// <summary>门槛人机与每种对照策略单挑，以及与它们混坐 5 人桌，报告整场胜率与吞吐。</summary>
internal static class Compare
{
public static void Run(int matches, ThresholdBotParameters parameters)
{
var table = EquityTable.Standard;

IBot Threshold() => new ThresholdBot("门槛", parameters, table);
var opponents = new (string Name, Func<ulong, IBot> Create)[]
{
    ("永远跟注", _ => new CallingStationBot()),
    ("永远加注", _ => new ManiacBot()),
    ("随机", seed => new RandomBot(seed)),
    ("门槛（自己）", _ => Threshold()),
};

Console.WriteLine($"== 单挑，每组 {matches} 场（交换座位抵消先后）");
foreach (var (name, create) in opponents)
{
    var watch = Stopwatch.StartNew();
    int wins = 0, ties = 0;
    long hands = 0;
    var gate = new Lock();
    Parallel.For(1, matches + 1, index =>
    {
        ulong seed = (ulong)index;
        bool swap = index % 2 == 0;
        var bots = swap ? new[] { create(seed), Threshold() } : new[] { Threshold(), create(seed) };
        var result = SelfPlayArena.PlayMatch(bots, seed);
        int me = swap ? 1 : 0;
        lock (gate)
        {
            hands += result.HandsPlayed;
            if (result.FinalChips[me] > result.FinalChips[1 - me]) wins++;
            else if (result.FinalChips[me] == result.FinalChips[1 - me]) ties++;
        }
    });
    double rate = (wins + ties * 0.5) / matches;
    double se = Math.Sqrt(rate * (1 - rate) / matches);
    Console.WriteLine($"  对 {name,-8} 胜率 {rate:P1} ±{1.96 * se:P1}   {matches / watch.Elapsed.TotalSeconds,6:F0} 场/秒，平均 {(double)hands / matches:F1} 局/场");
}

Console.WriteLine($"== 5 人桌：门槛 A、门槛 B 两个相同的人机 + 跟注、加注、随机各 1，每组 {matches} 场，整桌轮转 5 个位置（相对座次不变）");
{
    var watch = Stopwatch.StartNew();
    var winsBySeatKind = new double[5];
    var gate = new Lock();
    Parallel.For(1, matches + 1, index =>
    {
        ulong seed = (ulong)index;
        var lineup = new IBot[] { Threshold(), new CallingStationBot(), new ManiacBot(), new RandomBot(seed), Threshold() };
        int rotate = index % 5;
        var seated = Enumerable.Range(0, 5).Select(seat => lineup[(seat + rotate) % 5]).ToArray();
        var result = SelfPlayArena.PlayMatch(seated, seed);
        lock (gate)
        {
            foreach (int winner in result.Winners)
            {
                winsBySeatKind[(winner + rotate) % 5] += 1.0 / result.Winners.Count;
            }
        }
    });
    string[] names = ["门槛 A", "永远跟注", "永远加注", "随机", "门槛 B"];
    for (int kind = 0; kind < 5; kind++)
    {
        Console.WriteLine($"  {names[kind],-8} 夺冠率 {winsBySeatKind[kind] / matches:P1}（公平值 20%）");
    }
    Console.WriteLine($"  {matches / watch.Elapsed.TotalSeconds:F0} 场/秒");
}
}
}
