using System.Diagnostics;
using DarkTide.Bots;

/// <summary>
/// 用自我对战定门槛基准。
///
/// 场地：标准 5 人整场。候选人机＋两个“当前最好”的门槛人机＋永远跟注＋永远加注，每场按种子随机打乱座次。
/// 两个当前最好代表认真打的对手，两个固定策略代表极端打法，防止调出只会对付自己的参数。
/// 指标：候选人机的整场夺冠份额（并列平分）。
///
/// 比较方法：同一轮里所有候选值用同一批种子（同样的发牌），逐场配对比较，抵消发牌运气。
/// 只有配对提升超过 2 倍标准误才采纳，否则保留原值——宁可少改，也不追噪声。
///
/// 第一阶段：每轮一个弃牌倍数、一个加注倍数（门槛＝倍数÷(对手数＋1)），加上加注额与换牌余量。
/// 第二阶段：在第一阶段的结果上逐格微调（每轮 × 1～4 名对手 × 弃牌／加注）。5 人桌最多 4 名对手；
/// 5～7 名对手的格子只出现在 6～8 人桌，保持第一阶段的倍数。
/// </summary>
internal static class Tuner
{
    private const int Seats = 5;
    private const int Streets = 3;
    private const int MaxTunedOpponents = Seats - 1;
    private static readonly EquityTable Table = EquityTable.Standard;

    public static void Run(string outputPath)
    {
        var total = Stopwatch.StartNew();
        var start = new Knobs();
        var current = start.Clone();

        Log($"起点：{current}");
        ulong seedBase = 1_000_000;
        for (int round = 1; round <= 3; round++)
        {
            var field = current.Clone();
            Log($"== 第一阶段 第 {round} 轮（场上对手＝上一轮结果）");
            for (int street = 0; street < Streets; street++)
            {
                int s = street;
                current = Coordinate($"弃牌倍数[{StreetName(s)}]", current, field, seedBase += 100_000, 4000,
                    Options(Scaled(current.FoldFactor[s], [0.6, 0.8, 1.25, 1.5]), (k, v) => k.FoldFactor[s] = v));
                current = Coordinate($"加注倍数[{StreetName(s)}]", current, field, seedBase += 100_000, 4000,
                    Options(Scaled(current.RaiseFactor[s], [0.75, 0.9, 1.1, 1.3]), (k, v) => k.RaiseFactor[s] = v));
            }
            double raisePot = current.RaisePot, margin = current.RerouteMargin;
            current = Coordinate("加注额（池的比例）", current, field, seedBase += 100_000, 4000,
                Options(new[] { 0.25, 0.5, 0.75, 1.0 }.Where(v => v != raisePot), (k, v) => k.RaisePot = v));
            current = Coordinate("换牌余量", current, field, seedBase += 100_000, 4000,
                Options(new[] { 0.0, 0.01, 0.03, 0.06, 0.10 }.Where(v => v != margin), (k, v) => k.RerouteMargin = v));
            Log($"第 {round} 轮结束：{current}");
        }

        for (int round = 1; round <= 2; round++)
        {
            var field = current.Clone();
            Log($"== 第二阶段 第 {round} 轮（逐格微调，±0.03、±0.06）");
            for (int street = 0; street < Streets; street++)
            for (int k = 1; k <= MaxTunedOpponents; k++)
            {
                int s = street, opp = k;
                current = Coordinate($"弃牌[{StreetName(s)},{opp}人]", current, field, seedBase += 100_000, 4000,
                    Options([-0.06, -0.03, 0.03, 0.06], (k, d) => k.FoldCell[s, opp] += d));
                current = Coordinate($"加注[{StreetName(s)},{opp}人]", current, field, seedBase += 100_000, 4000,
                    Options([-0.06, -0.03, 0.03, 0.06], (k, d) => k.RaiseCell[s, opp] += d));
            }
            Log($"第二阶段第 {round} 轮结束");
        }

        // 留出验证：用从未参与调参的种子，让最终结果与起点直接对打。
        var final = current.ToParameters();
        Log("== 留出验证（新种子 20000 场）");
        var vsStart = Shares(current, start, 20_000, 9_000_000_000);
        var startVsStart = Shares(start, start, 20_000, 9_000_000_000);
        var (diff, se) = Paired(vsStart, startVsStart);
        Log($"  场上为起点参数时：最终参数夺冠份额 {vsStart.Average():P2}，起点参数 {startVsStart.Average():P2}，差 {diff:+0.00%;-0.00%} ±{2 * se:P2}");
        var vsFinal = Shares(current, current, 20_000, 9_100_000_000);
        var startVsFinal = Shares(start, current, 20_000, 9_100_000_000);
        (diff, se) = Paired(vsFinal, startVsFinal);
        Log($"  场上为最终参数时：最终参数夺冠份额 {vsFinal.Average():P2}，起点参数 {startVsFinal.Average():P2}，差 {diff:+0.00%;-0.00%} ±{2 * se:P2}");

        double ratio = MeasureAdjustment(final);
        // 门槛在“不调整”的前提下调出；性格范围取默认值，比值取刚测出的平衡值。
        final = final with
        {
            AdjustUpMin = ThresholdBotParameters.DefaultAdjustUpMin,
            AdjustUpMax = ThresholdBotParameters.DefaultAdjustUpMax,
            AdjustRatio = ratio,
        };

        File.WriteAllText(outputPath, ThresholdBaseline.Save(final));
        Log($"写入 {outputPath}，共用时 {total.Elapsed.TotalMinutes:F1} 分钟");
        PrintTable(final);
    }

    /// <summary>
    /// 在基准处测“弃了却本该最大”与“打到底却输”的频率，得到升降步长的比值：
    /// 下调步长 × P(弃了却本该最大) ＝ 上调步长 × P(打到底却输)，门槛的期望漂移为零，基准就是平衡点。
    /// </summary>
    private static double MeasureAdjustment(ThresholdBotParameters baseline)
    {
        Log("== 动态调整的步长比值（基准参数，5 人桌全是基准人机，20000 场）");
        long hands = 0, foldBest = 0, playLose = 0, foldUnknown = 0, folds = 0;
        var gate = new Lock();
        Parallel.For(0, 20_000, i =>
        {
            var bots = Enumerable.Range(0, Seats).Select(_ => new CountingBot(new ThresholdBot("基准", baseline, Table))).ToArray();
            SelfPlayArena.PlayMatch(bots, 7_000_000_000 + (ulong)i);
            lock (gate)
            {
                foreach (var bot in bots)
                {
                    hands += bot.Hands;
                    foldBest += bot.FoldBest;
                    playLose += bot.PlayLose;
                    foldUnknown += bot.FoldUnknown;
                    folds += bot.Folds;
                }
            }
        });
        double pDown = (double)foldBest / hands, pUp = (double)playLose / hands;
        Log($"  每局：弃牌 {(double)folds / hands:P1}（其中无法判断 {(double)foldUnknown / hands:P1}），弃了却本该最大 {pDown:P2}，打到底却输 {pUp:P2}");
        Log($"  下调步长 ÷ 上调步长 ＝ {pUp / pDown:F2}");
        return pUp / pDown;
    }

    private static Knobs Coordinate(string name, Knobs current, Knobs field, ulong seedBase, int matches,
        IEnumerable<(Action<Knobs> Apply, double Label)> alternatives)
    {
        var watch = Stopwatch.StartNew();
        var baseline = Shares(current, field, matches, seedBase);
        Knobs best = current;
        double bestDiff = 0, bestSe = 0;
        string bestLabel = "不变";
        var report = new List<string>();
        foreach (var (apply, label) in alternatives)
        {
            var candidate = current.Clone();
            apply(candidate);
            var shares = Shares(candidate, field, matches, seedBase);
            var (diff, se) = Paired(shares, baseline);
            // 先舍入再格式化：-0.0004 直接按分段格式输出会显示成“-+0.0%”。
            report.Add($"{label:0.###}:{Math.Round(diff, 3):+0.0%;-0.0%;0.0%}");
            if (diff > 2 * se && diff > bestDiff)
            {
                best = candidate;
                bestDiff = diff;
                bestSe = se;
                bestLabel = label.ToString("0.###");
            }
        }
        Log($"  {name,-14} 现值份额 {baseline.Average():P1}  [{string.Join("  ", report)}]  → {bestLabel}{(bestDiff > 0 ? $"（{bestDiff:+0.0%} ±{2 * bestSe:P1}）" : "")}  {watch.Elapsed.TotalSeconds:F0}s");
        return best;
    }

    // 两个用同一批种子跑出来的结果逐场相减：同一场发牌相同，运气大部分抵消，差的标准误比各自的标准误小得多。
    private static (double Mean, double StandardError) Paired(double[] a, double[] b)
    {
        int n = a.Length;
        double mean = 0;
        for (int i = 0; i < n; i++) mean += a[i] - b[i];
        mean /= n;
        double variance = 0;
        for (int i = 0; i < n; i++)
        {
            double d = a[i] - b[i] - mean;
            variance += d * d;
        }
        return (mean, Math.Sqrt(variance / (n - 1) / n));
    }

    /// <summary>每场候选人机的夺冠份额。结果写进按场次编号的数组，与线程调度无关，可复现。</summary>
    private static double[] Shares(Knobs candidate, Knobs field, int matches, ulong seedBase)
    {
        var candidateParameters = candidate.ToParameters();
        var fieldParameters = field.ToParameters();
        var shares = new double[matches];
        Parallel.For(0, matches, i =>
        {
            ulong seed = seedBase + (ulong)i;
            var lineup = new IBot[]
            {
                new ThresholdBot("候选", candidateParameters, Table),
                new ThresholdBot("场上", fieldParameters, Table),
                new ThresholdBot("场上", fieldParameters, Table),
                new CallingStationBot(),
                new ManiacBot(),
            };
            var seatOf = Permutation(seed);
            var seated = new IBot[Seats];
            for (int member = 0; member < Seats; member++)
            {
                seated[seatOf[member]] = lineup[member];
            }
            var result = SelfPlayArena.PlayMatch(seated, seed);
            shares[i] = result.Winners.Contains(seatOf[0]) ? 1.0 / result.Winners.Count : 0;
        });
        return shares;
    }

    // 座次打乱用独立的随机流，和发牌用的种子错开。
    private static int[] Permutation(ulong seed)
    {
        var random = new SplitMix64(seed * 0x9E3779B97F4A7C15UL + 12345);
        var order = Enumerable.Range(0, Seats).ToArray();
        for (int i = Seats - 1; i > 0; i--)
        {
            int j = random.NextInt(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        return order;
    }

    // 每个候选值一份“把它写进参数”的操作；候选值同时作为日志里的标签。
    private static IEnumerable<(Action<Knobs> Apply, double Label)> Options(IEnumerable<double> values, Action<Knobs, double> set) =>
        values.Select(value => ((Action<Knobs>)(knobs => set(knobs, value)), value)).ToArray();

    private static IEnumerable<double> Scaled(double value, double[] factors) =>
        factors.Select(factor => Math.Round(value * factor, 3)).Distinct();

    private static string StreetName(int street) => street switch { 0 => "一注", 1 => "二注", _ => "三注" };

    private static void PrintTable(ThresholdBotParameters parameters)
    {
        Console.WriteLine("最终门槛（胜率），行＝轮次，列＝对手数 1～7：");
        foreach (var (name, grid) in new[] { ("弃牌", parameters.Fold), ("加注", parameters.Raise) })
        for (int s = 0; s < Streets; s++)
        {
            Console.WriteLine($"  {name} {StreetName(s)}  " + string.Join("  ", Enumerable.Range(1, EquityTable.MaxOpponents).Select(k => $"{grid[s, k]:F3}")));
        }
        Console.WriteLine($"  加注额 {parameters.RaisePotFraction:F2} 池，换牌余量 {parameters.RerouteMargin:F2}");
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    private sealed class CountingBot(IBot inner) : IBot
    {
        public long Hands, FoldBest, PlayLose, FoldUnknown, Folds;
        public string Name => inner.Name;
        public void StartMatch() => inner.StartMatch();
        public BetDecision DecideBet(BettingObservation observation) => inner.DecideBet(observation);
        public bool DecideReroute(RerouteObservation observation) => inner.DecideReroute(observation);
        public void EndHand(HandOutcome outcome)
        {
            Hands++;
            if (outcome.Folded) Folds++;
            if (outcome.Folded && outcome.WouldHaveBeenBest == true) FoldBest++;
            if (outcome.Folded && outcome.WouldHaveBeenBest is null) FoldUnknown++;
            if (!outcome.Folded && outcome.WentToShowdown && !outcome.WonAnyPot) PlayLose++;
            inner.EndHand(outcome);
        }
    }
}

/// <summary>调参用的可变参数：每轮的弃牌、加注倍数，加上逐格偏移。</summary>
internal sealed class Knobs
{
    public double[] FoldFactor = [0.8, 0.8, 0.8];
    public double[] RaiseFactor = [1.5, 1.5, 1.5];
    public double RaisePot = 0.5;
    public double RerouteMargin = 0.02;
    public double[,] FoldCell = new double[3, EquityTable.MaxOpponents + 1];
    public double[,] RaiseCell = new double[3, EquityTable.MaxOpponents + 1];

    public Knobs Clone() => new()
    {
        FoldFactor = (double[])FoldFactor.Clone(),
        RaiseFactor = (double[])RaiseFactor.Clone(),
        RaisePot = RaisePot,
        RerouteMargin = RerouteMargin,
        FoldCell = (double[,])FoldCell.Clone(),
        RaiseCell = (double[,])RaiseCell.Clone(),
    };

    public ThresholdBotParameters ToParameters()
    {
        var fold = new double[3, EquityTable.MaxOpponents + 1];
        var raise = new double[3, EquityTable.MaxOpponents + 1];
        for (int s = 0; s < 3; s++)
        for (int k = 1; k <= EquityTable.MaxOpponents; k++)
        {
            double fair = 1.0 / (k + 1);
            fold[s, k] = Math.Clamp(FoldFactor[s] * fair + FoldCell[s, k], 0, 1);
            raise[s, k] = Math.Clamp(RaiseFactor[s] * fair + RaiseCell[s, k], 0, 0.99);
        }
        return new ThresholdBotParameters { Fold = fold, Raise = raise, RaisePotFraction = RaisePot, RerouteMargin = RerouteMargin };
    }

    public override string ToString() =>
        $"弃牌倍数 [{string.Join(", ", FoldFactor.Select(v => v.ToString("F3")))}]，加注倍数 [{string.Join(", ", RaiseFactor.Select(v => v.ToString("F3")))}]，加注额 {RaisePot:F2}，换牌余量 {RerouteMargin:F2}";
}
