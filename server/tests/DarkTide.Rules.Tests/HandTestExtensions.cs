using DarkTide.Rules;

namespace DarkTide.Rules.Tests;

internal static class HandTestExtensions
{
    /// <summary>改航窗口里所有人依次放弃。窗口为空时什么也不做。</summary>
    public static void PassAllReroutes(this Hand hand)
    {
        while (hand.CurrentStage == HandStage.Reroute)
        {
            hand.PassReroute(hand.RerouteActingSeat);
        }
    }
}
