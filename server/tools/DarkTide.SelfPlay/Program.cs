using DarkTide.Bots;

// 用法：
//   dotnet run -c Release --project server/tools/DarkTide.SelfPlay -- compare [每组场数] [参数文件]
//   dotnet run -c Release --project server/tools/DarkTide.SelfPlay -- tune [输出参数文件]
string mode = args.Length > 0 ? args[0] : "compare";
switch (mode)
{
    case "compare":
        int matches = args.Length > 1 ? int.Parse(args[1]) : 2000;
        var parameters = args.Length > 2 ? ThresholdBaseline.Load(File.ReadAllText(args[2])) : ThresholdBotParameters.FairShareStart();
        Compare.Run(matches, parameters);
        break;
    case "tune":
        Tuner.Run(args.Length > 1 ? args[1] : "server/src/DarkTide.Bots/Data/threshold-baseline.json");
        break;
    default:
        Console.Error.WriteLine($"未知模式 {mode}，可用 compare、tune。");
        return 2;
}
return 0;
