using DarkTide.Rules;

namespace DarkTide.Rules.Tests;

public class BettingRoundTests
{
    /// <summary>默认：五人按 0→1→2→3→4 行动，底注 1，进入本轮时每人剩 100。</summary>
    /// <summary>
    /// 默认局面：五人按 0→1→2→3→4 行动，底注 1，进入本轮时每人剩 100。
    /// 起始池不指定时按“本局参与者都交过底注”计算——真实流程里底注在下注轮之前收取，
    /// 池为空会让任何人都开不了注（最高开注额等于池）。
    /// </summary>
    private static BettingRound New(
        int Ante = 1,
        int? StartingPot = null,
        int seatCount = 5,
        int[]? balance = null,
        IReadOnlyList<int>? actionOrder = null)
    {
        actionOrder ??= Enumerable.Range(0, seatCount).ToArray();
        // 余额按行动顺序逐人给；不指定时每人 100，长度与行动顺序一致。
        balance ??= Enumerable.Repeat(100, actionOrder.Count).ToArray();
        return new BettingRound(Ante, StartingPot ?? Ante * actionOrder.Count, seatCount, actionOrder, balance);
    }

    [Fact]
    public void WithoutBetCanCheckOrOpenButNotFold()
    {
        var round = New();

        var legal = round.GetLegalActions();

        Assert.True(legal.CanCheck);
        Assert.False(legal.CanCall);
        Assert.Equal(0, legal.CallAmount);
        Assert.True(legal.CanRaise);
        Assert.Equal(2, legal.MinRaiseTarget);   // 首次开注至少 2 倍底注
        Assert.False(legal.CanFold);
    }

    [Fact]
    public void FacingBetMinimumRaiseAddsFullIncrement()
    {
        var round = New(seatCount: 3);
        round.RaiseTo(2);   // 座位 0 开注 2

        var legal = round.GetLegalActions();   // 座位 1

        Assert.False(legal.CanCheck);
        Assert.Equal(2, legal.CallAmount);
        Assert.Equal(4, legal.MinRaiseTarget);
    }

    [Fact]
    public void OpeningAmountSetsMinimumRaiseIncrement()
    {
        // 文档例：先开注 6，下一次完整加注至少加到本轮累计 12。
        var round = New(StartingPot: 23, seatCount: 3);
        round.RaiseTo(6);

        Assert.Equal(12, round.GetLegalActions().MinRaiseTarget);
    }

    [Fact]
    public void MaxOpeningBetEqualsPotWhenUnopened()
    {
        // 规则：无人下注时 C＝0，最高开注额就是 P。
        // 如果写成 P＋2C，池为空时会算出 0，任何开注都会被拒绝。
        var round = New(StartingPot: 22, seatCount: 2);

        var legal = round.GetLegalActions();

        Assert.Equal(0, legal.CallAmount);
        Assert.Equal(2, legal.MinRaiseTarget);      // 至少 2 倍底注
        Assert.Equal(22, legal.MaxRaiseTarget);     // 无人下注时最高开注额就是 P
    }

    [Fact]
    public void PotLimitCapsAtPotPlusTwiceCall()
    {
        // 文档 4.3 的例子：行动前池 P＝20，需要跟 C＝6，本次最多新增 32。
        // 两人各交底注 1，本轮之前累计 18，则开注前池为 20。
        // 文档 4.3：行动前池为 20（此时已含座位 0 待响应的 6）。
        // 两人各交底注 1，本轮之前累计 12，则开注前池为 14，开注 6 之后正好 20。
        var round = New(StartingPot: 14, seatCount: 2, balance: new[] { 100, 100 });
        round.RaiseTo(6);   // 座位 0 开注 6，池变 20

        var legal = round.GetLegalActions();   // 座位 1：跟 6，本次最多新增 20＋12＝32

        Assert.Equal(6, legal.CallAmount);
        Assert.Equal(32, legal.MaxRaiseTarget);
    }

    [Fact]
    public void ShortAllInDoesNotUpdateIncrementOrGrantRaiseRight()
    {
        // 文档 4.4 例：A 开注 10，B 全下到 15（只增加 5）；此后没有其他加价时 A 不能再次加注。
        // 三人桌：B 全下后还有 C 可以响应，所以本轮不会因为只剩一人而立刻结束。
        var round = New(StartingPot: 12, seatCount: 3, balance: new[] { 100, 15, 100 });
        round.RaiseTo(10);   // 座位 0 开注 10，最小完整加价幅度定为 10
        round.AllIn();       // 座位 1 只有 15，跟 10 后剩 5，不足额加价
        round.Call();       // 座位 2 跟到 15

        Assert.True(round.IsAllIn(1));
        Assert.Equal(15, round.Target);
        Assert.Equal(10, round.MinRaiseIncrement);   // 不足额全下不更新这一档

        var legal = round.GetLegalActions();   // 回到座位 0
        Assert.Equal(5, legal.CallAmount);
        Assert.False(round.HasRaiseRight(0));   // 只增加了 5，不到一个完整幅度
        Assert.False(legal.CanRaise);
        Assert.Throws<RuleRejectedException>(() => round.RaiseTo(20));
    }

    [Fact]
    public void CumulativeIncreaseRestoresRaiseRight()
    {
        // 文档例：短额全下把目标累计推到 20，A 面临新增 10，重新获得加价权。
        var round = New(StartingPot: 12, seatCount: 4, balance: new[] { 100, 15, 20, 100 });
        round.RaiseTo(10);   // 座位 0 开注 10
        round.AllIn();       // 座位 1 全下到 15，只增加 5
        Assert.False(round.HasRaiseRight(0));
        round.AllIn();       // 座位 2 全下到 20，累计增加 10
        round.Call();       // 座位 3 跟到 20

        var legal = round.GetLegalActions();   // 回到座位 0

        Assert.Equal(10, legal.CallAmount);
        // 座位 0 上次行动时目标额是 10，现在 20，累计增加 10，等于完整加价幅度，重新获得加价权。
        Assert.True(legal.CanRaise);
    }

    [Fact]
    public void RoundClosesWhenAllMatchedAndPotCountsAllCommitted()
    {
        var round = New(Ante: 1, seatCount: 3);
        round.RaiseTo(3);
        round.Call();
        round.Call();

        Assert.True(round.IsClosed);
        // 起始池 3（三份底注）+ 本轮各投 3。
        Assert.Equal(12, round.CurrentPot);
        Assert.Equal(3, round.CountInHand());
    }

    [Fact]
    public void FoldedPlayerLeavesHandButChipsStayInPot()
    {
        var round = New(StartingPot: 23, seatCount: 3);
        round.RaiseTo(5);
        round.Call();
        round.Fold();   // 座位 2 面对 5 弃牌

        Assert.True(round.IsClosed);
        Assert.Equal(2, round.CountInHand());
        Assert.True(round.HasFolded(2));
        Assert.Equal(33, round.CurrentPot);   // 起始 23 + 5 + 5
    }

    [Fact]
    public void InsufficientChipsGoAllInInsteadOfNegativeBalance()
    {
        var round = New(StartingPot: 10, seatCount: 2, balance: new[] { 100, 3 });
        round.RaiseTo(10);

        var legal = round.GetLegalActions();   // 座位 1 只有 3

        Assert.Equal(3, legal.CallAmount);
        Assert.True(legal.CanGoAllIn);
        round.AllIn();

        Assert.Equal(0, round.StackAt(1));
        Assert.True(round.IsAllIn(1));
        Assert.Equal(3, round.CommittedAt(1));
    }

    [Fact]
    public void RoundEndsWhenOnlyOneCanAct()
    {
        // 座位 1 没有筹码，等同全下；只剩座位 0 有筹码，不能继续向无人能跟的池下注。
        var round = New(seatCount: 2, balance: new[] { 100, 0 });

        Assert.True(round.IsClosed);
        Assert.Equal(-1, round.ActingSeat);
    }

    [Fact]
    public void RoundEndsAfterShortCallAllIn()
    {
        // 开了 30 的注，所以开注前池至少要有 30。
        var round = New(StartingPot: 30, seatCount: 2, balance: new[] { 100, 20 });
        round.RaiseTo(30);
        round.AllIn();   // 座位 1 只有 20，跟不全

        // 座位 1 已经全下，本轮只剩座位 0 一人可行动，可以直接结束。
        Assert.True(round.IsClosed);
        Assert.Equal(80, round.CurrentPot);   // 30 + 30 + 20
        Assert.Equal(20, round.CommittedAt(1));
    }

    [Fact]
    public void ActionOrderFollowsSeatListNotSeatNumbers()
    {
        // 入座顺序 3→1→4，行动应从 3 开始。
        var round = New(seatCount: 5, actionOrder: new[] { 3, 1, 4 });

        Assert.Equal(3, round.ActingSeat);
        round.Check();
        Assert.Equal(1, round.ActingSeat);
        round.Check();
        Assert.Equal(4, round.ActingSeat);
    }

    [Fact]
    public void DuplicateSeatsAndTooFewPlayersAreRejected()
    {
        Assert.Throws<ArgumentException>(() => New(seatCount: 3, actionOrder: new[] { 0, 0, 1 }));
        Assert.Throws<ArgumentException>(() => New(seatCount: 3, actionOrder: new[] { 0 }));
        Assert.Throws<ArgumentException>(() => New(seatCount: 3, actionOrder: new[] { 0, 1, 9 }));
        // 余额按行动顺序给，长度不符要拒绝。
        Assert.Throws<ArgumentException>(() => New(seatCount: 3, actionOrder: new[] { 0, 1, 2 }, balance: new[] { 1, 2 }));
    }

    [Fact]
    public void ActingAfterRoundClosedIsRejected()
    {
        var round = New(seatCount: 2);
        round.Check();
        round.Check();

        Assert.True(round.IsClosed);
        Assert.Equal(-1, round.ActingSeat);
        Assert.Throws<RuleRejectedException>(() => round.Check());
        Assert.Throws<InvalidOperationException>(() => round.GetLegalActions());
    }
}
