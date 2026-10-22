using DarkTide.Protocol.V1;
using DarkTide.Room;
using DarkTide.Rules;
using Google.Protobuf;
using RoomStageV1 = DarkTide.Protocol.V1.RoomStage;
using HandStageV1 = DarkTide.Protocol.V1.HandStage;
using LegalActionsV1 = DarkTide.Protocol.V1.LegalActions;
using RerouteQuoteV1 = DarkTide.Protocol.V1.RerouteQuote;
using PotResultV1 = DarkTide.Protocol.V1.PotResult;

namespace DarkTide.Server.Realtime;

/// <summary>
/// 领域对象与协议消息之间的转换。白名单式：只逐字段拷贝明确列出的公开信息，从不序列化整个领域对象。
/// 观看者自己的底牌只来自 <see cref="ViewerSnapshot.HoleCards"/>；别人的底牌只来自摊牌结算。
/// </summary>
internal static class ProtocolMapping
{
    /// <summary>16 字节 UUID，大端序（RFC 4122 字节顺序）。客户端用任何语言都能按标准解析。</summary>
    public static ByteString ToBytes(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        return ByteString.CopyFrom(bytes);
    }

    /// <summary>解析 16 字节 UUID。长度不对或全零都失败：全零编号在服务端没有意义。</summary>
    public static bool TryReadGuid(ByteString bytes, out Guid id)
    {
        id = Guid.Empty;
        if (bytes.Length != 16)
        {
            return false;
        }
        id = new Guid(bytes.Span, bigEndian: true);
        return id != Guid.Empty;
    }

    public static ByteString ToBytes(IReadOnlyList<Card>? cards)
    {
        if (cards is null || cards.Count == 0)
        {
            return ByteString.Empty;
        }
        var bytes = new byte[cards.Count];
        for (int i = 0; i < cards.Count; i++)
        {
            bytes[i] = cards[i].Id;
        }
        return ByteString.CopyFrom(bytes);
    }

    public static RoomState ToRoomState(ViewerSnapshot view, Guid? owner, Guid viewerId, DateTimeOffset now)
    {
        var overview = view.Overview;
        var state = new RoomState
        {
            RoomId = ToBytes(overview.RoomId),
            StateVersion = overview.StateVersion,
            Stage = overview.Stage switch
            {
                DarkTide.Room.RoomStage.WaitingForSeats => RoomStageV1.WaitingForSeats,
                DarkTide.Room.RoomStage.CardsDealt => RoomStageV1.HandInProgress,
                DarkTide.Room.RoomStage.HandSettled => RoomStageV1.HandSettled,
                DarkTide.Room.RoomStage.MatchFinished => RoomStageV1.MatchFinished,
                _ => throw new InvalidOperationException($"未映射的房间阶段 {overview.Stage}。")
            },
            YouAreOwner = owner is Guid ownerId && ownerId == viewerId,
            YourSeat = view.Seat ?? -1,
            YourHoleCards = ToBytes(view.HoleCards),
            HandsPlayed = (uint)overview.HandsPlayed,
            DeadlineUnixMs = overview.Deadline?.ToUnixTimeMilliseconds() ?? 0,
            ServerTimeUnixMs = now.ToUnixTimeMilliseconds(),
        };
        state.SeatOccupied.AddRange(overview.SeatOccupied);
        state.ParticipantOrder.AddRange(overview.ParticipantOrder.Select(seat => (uint)seat));
        state.MatchChips.AddRange(overview.MatchChips.Select(chips => (uint)chips));
        if (overview.Hand is { } hand)
        {
            state.Hand = ToHandState(hand, view.Seat);
        }
        return state;
    }

    private static HandState ToHandState(HandOverview hand, int? viewerSeat)
    {
        var state = new HandState
        {
            HandNumber = (uint)hand.HandNumber,
            Ante = (uint)hand.Ante,
            Stage = hand.Stage switch
            {
                DarkTide.Rules.HandStage.Ante => HandStageV1.Ante,
                DarkTide.Rules.HandStage.FirstBet => HandStageV1.FirstBet,
                DarkTide.Rules.HandStage.SecondBet => HandStageV1.SecondBet,
                DarkTide.Rules.HandStage.Reroute => HandStageV1.Reroute,
                DarkTide.Rules.HandStage.ThirdBet => HandStageV1.ThirdBet,
                DarkTide.Rules.HandStage.Showdown => HandStageV1.Showdown,
                _ => throw new InvalidOperationException($"未映射的单局阶段 {hand.Stage}。")
            },
            CommunityCards = ToBytes(hand.CommunityCards),
            DiscardedCards = ToBytes(hand.DiscardedCards),
            ActingSeat = hand.ActingSeat,
            RerouteActingSeat = hand.RerouteActingSeat,
        };
        state.Stacks.AddRange(hand.Stacks.Select(value => (uint)value));
        state.Contributions.AddRange(hand.Contributions.Select(value => (uint)value));
        state.CommittedThisRound.AddRange(hand.CommittedThisRound.Select(value => (uint)value));
        state.RerouteFees.AddRange(hand.RerouteFees.Select(value => (uint)value));
        state.Folded.AddRange(hand.Folded);
        // 合法动作本身是公开信息，但只发给轮到的人：别人收到也没用，还多占带宽。
        if (hand.LegalActions is { } legal && viewerSeat is int seat && seat == hand.ActingSeat)
        {
            state.YourLegalActions = new LegalActionsV1
            {
                CanCheck = legal.CanCheck,
                CanCall = legal.CanCall,
                CallAmount = (uint)legal.CallAmount,
                CanRaise = legal.CanRaise,
                MinRaiseTarget = (uint)legal.MinRaiseTarget,
                MaxRaiseTarget = (uint)legal.MaxRaiseTarget,
                CanFold = legal.CanFold,
                CanGoAllIn = legal.CanGoAllIn,
            };
        }
        foreach (var quote in hand.RerouteQuotes)
        {
            var mapped = new RerouteQuoteV1 { Seat = (uint)quote.Seat, Position = (uint)quote.Position, Fee = (uint)quote.Fee, Exposure = (uint)quote.Exposure };
            mapped.BonusEligibleSeats.AddRange(quote.BonusEligibleSeats.Select(value => (uint)value));
            state.RerouteQuotes.Add(mapped);
        }
        if (hand.Settlement is { } settlement)
        {
            var mapped = new Settlement { Showdown = settlement.Showdown };
            mapped.FinalStacks.AddRange(settlement.FinalStacks.Select(value => (uint)value));
            mapped.Pots.AddRange(settlement.Pots.Select(ToPot));
            mapped.BonusPots.AddRange(settlement.BonusPots.Select(ToPot));
            // 只有摊牌才有公开底牌；领域层没有摊牌时这里为空，映射不额外判断也不额外添加。
            foreach (var (shownSeat, cards) in settlement.ShownHoleCards.OrderBy(pair => pair.Key))
            {
                mapped.ShownHoleCards.Add(new ShownCards { Seat = (uint)shownSeat, Cards = ToBytes(cards) });
            }
            state.Settlement = mapped;
        }
        return state;
    }

    private static PotResultV1 ToPot(DarkTide.Rules.PotResult pot)
    {
        var mapped = new PotResultV1 { Total = (uint)pot.Total.Amount };
        mapped.EligibleSeats.AddRange(pot.EligibleSeats.Select(value => (uint)value));
        mapped.Winners.AddRange(pot.Winners.Select(value => (uint)value));
        mapped.Shares.AddRange(pot.Shares.Select(value => (uint)value));
        return mapped;
    }

    public static Protocol.V1.Rejection ToRejection(RejectionReason reason) =>
        Enum.IsDefined((Protocol.V1.Rejection)(int)reason) ? (Protocol.V1.Rejection)(int)reason : Protocol.V1.Rejection.Unspecified;

    /// <summary>未知值与 UNSPECIFIED 都返回 null：不能落入默认的过牌或跟注。</summary>
    public static BettingAction? ToBettingAction(BetAction action) => action switch
    {
        BetAction.Check => BettingAction.Check,
        BetAction.Call => BettingAction.Call,
        BetAction.Raise => BettingAction.Raise,
        BetAction.Fold => BettingAction.Fold,
        BetAction.AllIn => BettingAction.AllIn,
        _ => null
    };
}
