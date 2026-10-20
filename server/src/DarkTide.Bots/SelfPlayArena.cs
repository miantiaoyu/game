using DarkTide.Rules;

namespace DarkTide.Bots;

/// <summary>一场整场比赛的结果。</summary>
public sealed record MatchResult(IReadOnlyList<int> FinalChips, IReadOnlyList<int> Winners, int HandsPlayed);

/// <summary>
/// 自我对战平台：持有真实的单局与整场，替人机推进流程。人机只拿到 <see cref="BotObservation"/>，看不到别人的牌。
///
/// 一场：最多 12 局，筹码跨局保留；首局起始位由种子决定，此后每局顺移到下一位仍有筹码的玩家（规则第 1 节）。
/// 洗牌用确定的伪随机数：同一种子同一批人机，整场逐步相同，实验可以复现、出问题可以重放。
/// 这里的洗牌只用于实验；正式对局用 <see cref="Shuffler"/>。
///
/// 人机给出非法动作时直接抛出：那是人机的缺陷，不能悄悄替它改成别的动作。
/// </summary>
public static class SelfPlayArena
{
    public static MatchResult PlayMatch(IReadOnlyList<IBot> bots, ulong seed, int initialChips = Match.StandardInitialChips)
    {
        ArgumentNullException.ThrowIfNull(bots);
        int seats = bots.Count;
        var match = new Match(seats, initialChips);
        var random = new SplitMix64(seed);
        foreach (var bot in bots)
        {
            bot.StartMatch();
        }

        int startSeat = random.NextInt(seats);
        while (!match.AllHandsPlayed && !match.IsDecided)
        {
            match.StartNextHand();
            var withChips = match.SeatsWithChips();
            // 起始位若已淘汰，顺时针找下一位仍有筹码的人。
            while (!withChips.Contains(startSeat))
            {
                startSeat = (startSeat + 1) % seats;
            }
            var order = Enumerable.Range(0, seats).Select(offset => (startSeat + offset) % seats).Where(withChips.Contains).ToArray();

            var hand = new Hand(seats, Shuffle(ref random), order, match.CurrentAnte().Amount, match.ChipsSnapshot());
            PlayHand(hand, bots, match.CurrentHandNumber);
            var settlement = hand.Settle();
            match.SettleHand(settlement.FinalStacks);
            NotifyOutcomes(hand, settlement, bots);

            // 结算后标记顺移到下一位仍有筹码的玩家。
            startSeat = (startSeat + 1) % seats;
        }

        var final = match.ChipsSnapshot();
        int best = final.Max();
        var winners = Enumerable.Range(0, seats).Where(seat => final[seat] == best).ToArray();
        return new MatchResult(final, winners, match.CurrentHandNumber);
    }

    private static void PlayHand(Hand hand, IReadOnlyList<IBot> bots, int handNumber)
    {
        hand.CollectAntes();
        while (!hand.IsComplete)
        {
            if (hand.CurrentStage == HandStage.Reroute)
            {
                int seat = hand.RerouteActingSeat;
                var quote = hand.CurrentRerouteQuote!;
                var observation = new RerouteObservation(Observe(hand, seat, handNumber), quote.Fee, quote.Exposure);
                if (bots[seat].DecideReroute(observation))
                {
                    hand.Reroute(seat);
                }
                else
                {
                    hand.PassReroute(seat);
                }
                continue;
            }

            if (!hand.StartBettingRound())
            {
                continue;
            }
            while (hand.IsBetting)
            {
                int seat = hand.ActingSeat;
                var decision = bots[seat].DecideBet(new BettingObservation(Observe(hand, seat, handNumber), hand.GetLegalActions(), hand.CommittedThisRound(seat)));
                hand.Act(seat, decision.Action, decision.RaiseTarget);
            }
        }
    }

    private static BotObservation Observe(Hand hand, int seat, int handNumber)
    {
        int pot = hand.RegularPotTotal;
        int opponents = 0;
        foreach (int other in hand.ParticipantOrder)
        {
            pot += hand.CommittedThisRound(other);
            if (other != seat && !hand.HasFolded(other))
            {
                opponents++;
            }
        }
        return new BotObservation
        {
            Seat = seat,
            HoleCards = hand.CreatePlayerView(seat).HoleCards,
            CommunityCards = hand.CommunityCards.ToArray(),
            DeadCards = hand.DiscardedCards.ToArray(),
            OpponentsInHand = opponents,
            Stage = hand.CurrentStage,
            Ante = hand.Ante,
            Stack = hand.StackAt(seat),
            Pot = pot,
            HandNumber = handNumber,
        };
    }

    /// <summary>
    /// 按人机能看到的信息告诉它这局的结果。“本来是不是最大”只有真正摊牌时才能判断：
    /// 用本人底牌与摊牌时公开的牌力比较；没有摊牌就是“无法判断”。
    /// </summary>
    private static void NotifyOutcomes(Hand hand, HandSettlement settlement, IReadOnlyList<IBot> bots)
    {
        var shown = settlement.HandValues.Where(value => value is not null).Select(value => value!.Value).ToArray();
        HandValue? bestShown = shown.Length > 0 ? shown.Max() : null;
        foreach (int seat in hand.ParticipantOrder)
        {
            bool folded = hand.HasFolded(seat);
            bool won = settlement.Pots.Concat(settlement.BonusPots).Any(pot => pot.Winners.Contains(seat));
            bool? wouldHaveBeenBest = null;
            if (folded && settlement.Showdown && bestShown is { } best)
            {
                wouldHaveBeenBest = HandEvaluator.EvaluateBest(hand.CreatePlayerView(seat).HoleCards, hand.CommunityCards) >= best;
            }
            bots[seat].EndHand(new HandOutcome(folded, settlement.Showdown && !folded, won, wouldHaveBeenBest));
        }
    }

    // Fisher–Yates 洗牌，用确定的伪随机数，便于复现。
    private static Card[] Shuffle(ref SplitMix64 random)
    {
        var cards = Deck.CreateStandard().ToArray();
        for (int i = cards.Length - 1; i > 0; i--)
        {
            int j = random.NextInt(i + 1);
            (cards[i], cards[j]) = (cards[j], cards[i]);
        }
        return cards;
    }
}
