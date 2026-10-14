namespace DarkTide.Rules;

/// <summary>
/// 一轮下注的状态机。只管规则：谁该行动、能做什么、每个动作改变什么。
/// 不负责发牌与结算，也不直接改整场筹码；本轮的投入只记在这里，结算时再处理。
/// 术语（见 docs/standard-rules.md 第 4 节）：
/// 目标额＝本轮每人应投到的累计金额；跟注额＝本人距离目标额还差多少。
/// 池 P＝本轮开始前已计入普通池的总额（含底注与之前各轮），不含改航附加池。
/// </summary>
public sealed class BettingRound
{
    private readonly int[] _actionOrder;      // 本局在座的座位，按行动方向排列
    private readonly int[] _committed;      // 每人在本轮已投入的累计金额
    private readonly int[] _stack;      // 每人还能投入多少（进入本轮时的余额）
    private readonly bool[] _folded;
    private readonly bool[] _allIn;
    private readonly bool[] _mustAct;       // 本人是否还需要响应本轮

    // 加价权（规则 4.4）：尚未行动、或此前只过牌且尚未面对下注的人保留加价权；
    // 已经下注或跟注的人，要等目标额在自己上次行动后累计增加够一个完整加价幅度，才重新获得。
    private readonly bool[] _hasNotActed;
    private readonly bool[] _lastActionWasCheck;
    private readonly int[] _targetAtLastAction;
    private readonly bool[] _hasRaiseRight;

    private int _actingIndex = -1;

    public int Ante { get; }
    public int StartingPot { get; }
    /// <summary>本轮累计目标额。</summary>
    public int Target { get; private set; }
    /// <summary>本次完整加价所需的最小增额；每轮重新从 2 倍底注开始。</summary>
    public int MinRaiseIncrement { get; private set; }
    /// <summary>本轮各座位投入之和。</summary>
    public int TotalCommitted { get; private set; }

    public bool IsClosed => _actingIndex < 0;
    public int ActingSeat => IsClosed ? -1 : _actionOrder[_actingIndex];

    /// <summary>
    /// 开始一轮下注。
    /// </summary>
    /// <param name="actionOrder">本轮的行动方向；只包含本局仍在座的人。</param>
    /// <param name="stackBySeat">按 <b>行动顺序</b> 逐人给出的余额，长度必须等于行动顺序的长度。</param>
    public BettingRound(int ante, int startingPot, int seatCount, IReadOnlyList<int> actionOrder, IReadOnlyList<int> stackBySeat)
    {
        ArgumentNullException.ThrowIfNull(actionOrder);
        ArgumentNullException.ThrowIfNull(stackBySeat);
        ArgumentOutOfRangeException.ThrowIfNegative(Ante);
        ArgumentOutOfRangeException.ThrowIfNegative(StartingPot);
        if (seatCount < TableLimits.MinSeats || seatCount > TableLimits.MaxSeats)
        {
            throw new ArgumentOutOfRangeException(nameof(seatCount), seatCount, "座位总数必须在 2～8 之间。");
        }
        if (actionOrder.Count < 2)
        {
            throw new ArgumentException("一轮下注至少需要两名玩家。", nameof(actionOrder));
        }
        if (stackBySeat.Count != actionOrder.Count)
        {
            throw new ArgumentException("余额数量必须等于行动顺序的人数。", nameof(stackBySeat));
        }

        this.Ante = ante;
        this.StartingPot = startingPot;
        MinRaiseIncrement = ante * 2;
        _committed = new int[seatCount];
        _stack = new int[seatCount];
        _folded = new bool[seatCount];
        _allIn = new bool[seatCount];
        _mustAct = new bool[seatCount];
        _hasNotActed = new bool[seatCount];
        _lastActionWasCheck = new bool[seatCount];
        _targetAtLastAction = new int[seatCount];
        _hasRaiseRight = new bool[seatCount];
        this._actionOrder = new int[actionOrder.Count];

        var seen = new bool[seatCount];
        for (int index = 0; index < actionOrder.Count; index++)
        {
            int seat = actionOrder[index];
            if (seat < 0 || seat >= seatCount || seen[seat])
            {
                throw new ArgumentException("行动顺序中的座位必须有效且不重复。", nameof(actionOrder));
            }
            if (stackBySeat[index] < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(stackBySeat), "余额不能为负数。");
            }

            seen[seat] = true;
            this._actionOrder[index] = seat;
            _stack[seat] = stackBySeat[index];
            // 一开始就没有筹码的人等同全下，不会成为行动对象。
            _allIn[seat] = _stack[seat] == 0;
            _hasRaiseRight[seat] = true;
            _hasNotActed[seat] = true;
            _mustAct[seat] = true;
        }

        MoveToNextActor(from: 0);
    }

    public int CommittedAt(int seat) => _committed[RequireInHand(seat)];
    public int StackAt(int seat) => _stack[RequireInHand(seat)];
    public bool HasFolded(int seat) => _folded[RequireInHand(seat)];
    public bool IsAllIn(int seat) => _allIn[RequireInHand(seat)];
    public bool HasRaiseRight(int seat) => _hasRaiseRight[RequireInHand(seat)];

    /// <summary>还留在本局、有资格分池的座位数（含已全下者，不含已弃牌者）。</summary>
    public int CountInHand()
    {
        int count = 0;
        foreach (int seat in _actionOrder)
        {
            if (!_folded[seat])
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>还能继续行动的人数：未弃牌且还有余额。少于两人时本轮不再继续。</summary>
    public int CountAbleToAct()
    {
        int count = 0;
        foreach (int seat in _actionOrder)
        {
            if (!_folded[seat] && !_allIn[seat])
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>本轮是否完全没有发生下注。</summary>
    public bool IsUnopened => Target == 0;

    /// <summary>本轮开始前已投入的池加上本轮已投入，即当前普通池。</summary>
    public int CurrentPot => StartingPot + TotalCommitted;

    /// <summary>当前行动玩家可以做什么。本轮结束后调用会抛出。</summary>
    public LegalActions GetLegalActions()
    {
        int seat = ActingSeat;
        if (seat < 0)
        {
            throw new InvalidOperationException("本轮下注已经结束。");
        }

        int callAmount = Target - _committed[seat];
        int maxAdd = MaxAdditionalCommitment(seat, callAmount);
        int maxTarget = _committed[seat] + maxAdd;
        // 首次开注至少 2 倍底注；池小于这个数时也要保证最低开注做得到。
        int minOpenTarget = _committed[seat] + Math.Max(Ante * 2, Target + MinRaiseIncrement - _committed[seat]);
        int minRaiseTarget = Math.Min(Target + MinRaiseIncrement, maxTarget);
        if (callAmount == 0)
        {
            minRaiseTarget = Math.Min(minOpenTarget, maxTarget);
        }

        return new LegalActions(
            CanCheck: callAmount == 0,
            CanCall: callAmount > 0 && _stack[seat] > 0,
            CallAmount: Math.Min(callAmount, _stack[seat]),
            CanRaise: _hasRaiseRight[seat] && AnyoneElseCanRespond(),
            MinRaiseTarget: minRaiseTarget,
            MaxRaiseTarget: maxTarget,
            CanFold: Target > 0,
            CanGoAllIn: _stack[seat] > 0);
    }

    public void Check() => Execute(BettingAction.Check, 0);
    public void Call() => Execute(BettingAction.Call, 0);
    public void Fold() => Execute(BettingAction.Fold, 0);
    public void AllIn() => Execute(BettingAction.AllIn, 0);
    /// <summary>加注到指定累计目标额；金额必须落在合法范围内。</summary>
    public void RaiseTo(int target) => Execute(BettingAction.Raise, target);

    private void Execute(BettingAction action, int target)
    {
        int seat = ActingSeat;
        if (seat < 0)
        {
            throw new RuleRejectedException(RejectionReason.StageNotAllowed, "本轮下注已经结束。");
        }
        // 已弃牌或已全下的人不会成为当前行动座位，见 MoveToNextActor。

        int callAmount = Target - _committed[seat];

        switch (action)
        {
            case BettingAction.Check:
                Require(callAmount == 0, "有待跟注额时不能过牌。");
                break;

            case BettingAction.Call:
                Require(callAmount > 0, "没有待跟注额时不需要跟注。");
                Commit(seat, callAmount);
                break;

            case BettingAction.Fold:
                // 规则：无待跟注额时不主动弃牌。
                Require(Target > 0, "无待跟注额时不允许主动弃牌。");
                _folded[seat] = true;
                _mustAct[seat] = false;
                break;

            case BettingAction.Raise:
            {
                var legal = GetLegalActions();
                Require(legal.CanRaise, "本玩家当前没有加价权。");
                Require(target >= legal.MinRaiseTarget && target <= legal.MaxRaiseTarget,
                    $"加注目标额必须在 {legal.MinRaiseTarget}～{legal.MaxRaiseTarget} 之间。");
                ApplyRaise(seat, target);
                break;
            }

            case BettingAction.AllIn:
            {
                Require(_stack[seat] > 0, "没有可用筹码，无法全下。");
                // 全下能到多少：本人已投入 + 本次最多新增。最多新增已受余额与底池上限限制。
                int candidateTarget = _committed[seat] + MaxAdditionalCommitment(seat, callAmount);
                if (candidateTarget <= Target)
                {
                    // 连目标额都够不到：这是不足额跟注，直接全部投入，不抬高目标额，也就不需要加价权。
                    Commit(seat, callAmount);
                    break;
                }
                // 能抬高目标额：这是加注性质的全下。余额连跟齐都不够时，属于规则 4.4 的不足额情形，
                // 即使没有加价权也允许；余额够跟齐时需要有加价权。两种情形都必须有人能响应（规则 4.5）。
                Require(_hasRaiseRight[seat] || _stack[seat] < callAmount, "本玩家当前没有加价权。");
                Require(AnyoneElseCanRespond(), "没有其他玩家能响应，不能向无人能跟的池加注。");
                ApplyRaise(seat, candidateTarget);
                break;
            }

        }

        _mustAct[seat] = false;
        _hasNotActed[seat] = false;
        // 只过牌、且当时还没有人下注的人，之后面对下注仍保留加价权（规则 4.4）。
        _lastActionWasCheck[seat] = action == BettingAction.Check;
        _targetAtLastAction[seat] = Target;
        MoveToNextActor(from: _actingIndex + 1);
    }

    /// <summary>把目标额提高到指定值，并按是否达到完整幅度决定是否重开行动、刷新加价权。</summary>
    private void ApplyRaise(int seat, int newTarget)
    {
        int increase = newTarget - Target;
        if (increase <= 0)
        {
            throw new InvalidOperationException("加注必须提高目标额。");
        }

        Commit(seat, newTarget - _committed[seat]);
        Target = newTarget;

        // 只有达到完整幅度才更新最小加价幅度；不足额全下不改变这一档（规则 4.4）。
        if (increase >= MinRaiseIncrement)
        {
            MinRaiseIncrement = increase;
        }

        // 其他人是否还需要响应：没跟齐目标额的人一定要响应；已经跟齐的人只在完整加价时重新获得机会。
        bool isFullRaise = increase >= MinRaiseIncrement;
        foreach (int other in _actionOrder)
        {
            if (other == seat || _folded[other] || _allIn[other])
            {
                continue;
            }
            _mustAct[other] = _committed[other] < Target || isFullRaise;
            _hasRaiseRight[other] = _hasNotActed[other] || _lastActionWasCheck[other] || RegainedRaiseRight(other);
        }
    }

    /// <summary>
    /// 已经下注或跟注的人，要在自己上次行动后目标额累计增加够一个完整加价幅度，才重新获得加价权。
    /// </summary>
    private bool RegainedRaiseRight(int seat) =>
        Target - _targetAtLastAction[seat] >= MinRaiseIncrement;

    /// <summary>投入金额，把待行动名单交给下一位。金额不能超过本人剩余筹码。</summary>
    private void Commit(int seat, int amount)
    {
        int actual = Math.Min(amount, _stack[seat]);
        _committed[seat] += actual;
        TotalCommitted += actual;
        _stack[seat] -= actual;
        if (_stack[seat] == 0)
        {
            _allIn[seat] = true;
        }
    }

    /// <summary>
    /// 底池限注下本人本次最多还能新增投入多少（规则 4.3）。
    /// 记 P 为行动前可计入的普通下注池总额（含底注、之前各轮与本轮已投入的部分，含待响应的注），
    /// C 为本人要补齐的跟注额：先跟 C、池变 P＋C，最多再加 P＋C，所以本次最多新增 P＋2C。
    /// 无人下注时 C＝0，最高开注额即为 P。
    /// 另外保证最低开注 2a 始终做得到：池总额小于 2a 时（例如两人桌只交过 1 枚底注），
    /// 若严格按 P 封顶，就会出现“规则要求至少开 2a、又不允许开 2a”的矛盾。
    /// </summary>
    private int MaxAdditionalCommitment(int seat, int callAmount)
    {
        int pot = CurrentPot;
        int limit = pot + 2 * callAmount;
        if (callAmount == 0)
        {
            limit = Math.Max(limit, Ante * 2 - _committed[seat]);
        }
        return Math.Min(_stack[seat], limit);
    }

    /// <summary>
    /// 是否还有别人可能响应本次加注：未弃牌且还有筹码的人。
    /// 已全下者不再行动，只剩自己有时不许向无人能跟的池加注（规则 4.5）。
    /// </summary>
    private bool AnyoneElseCanRespond()
    {
        foreach (int seat in _actionOrder)
        {
            if (seat != ActingSeat && !_folded[seat] && !_allIn[seat])
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 从指定位置开始，找下一个还需要行动的人；找不到就结束本轮。
    /// 只有一人还有筹码时，不继续向无人能跟的池下注（规则 4.5），本轮直接结束。
    /// </summary>
    private void MoveToNextActor(int from)
    {
        if (CountAbleToAct() < 2)
        {
            _actingIndex = -1;
            return;
        }

        for (int step = 0; step < _actionOrder.Length; step++)
        {
            int index = (from + step) % _actionOrder.Length;
            int seat = _actionOrder[index];
            if (!_folded[seat] && !_allIn[seat] && _mustAct[seat])
            {
                _actingIndex = index;
                return;
            }
        }

        _actingIndex = -1;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new RuleRejectedException(RejectionReason.StageNotAllowed, message);
        }
    }

    private int RequireInHand(int seat)
    {
        if (seat < 0 || seat >= _folded.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(seat), seat, "座位编号超出范围。");
        }
        if (Array.IndexOf(_actionOrder, seat) < 0)
        {
            throw new RuleRejectedException(RejectionReason.StageNotAllowed, "该座位不在本轮下注名单中。");
        }
        return seat;
    }
}
