using System.Net;
using System.Net.WebSockets;
using DarkTide.Protocol.V1;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DarkTide.Server.Tests;

/// <summary>
/// 实时连接端到端：真实的 HTTP 管道与 WebSocket，只用公开协议。每个用例对应一条安全规则或资源上限。
/// </summary>
public class RealtimeTests
{
    private static WebApplicationFactory<Program> CreateFactory(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // 默认关掉计时，用例自己控制节奏；需要计时的用例显式打开。
            var defaults = new[] { ("Room:TurnTimeout", "00:00:00"), ("Room:NextHandDelay", "00:00:00") };
            foreach (var (key, value) in TestSessionKeys.Settings.Concat(defaults).Concat(settings))
            {
                builder.UseSetting(key, value);
            }
        });

    private static async Task<(RealtimeClient Owner, RealtimeClient Guest, ByteString RoomId)> TwoSeatedPlayersAsync(WebApplicationFactory<Program> factory)
    {
        var owner = await RealtimeClient.ConnectAsync(factory);
        var created = await owner.CreateRoomAsync(2);
        Assert.Equal(ResultCode.Ok, created.Code);
        var guest = await RealtimeClient.ConnectAsync(factory);
        Assert.Equal(ResultCode.Ok, (await guest.JoinAsync(created.RoomId)).Code);

        var state = await owner.WaitForStateAsync(s => s.StateVersion == 0);
        Assert.Equal(ResultCode.Ok, (await owner.TakeSeatAsync(0, state.StateVersion)).Code);
        state = await guest.WaitForStateAsync(s => s.StateVersion == 1);
        Assert.Equal(ResultCode.Ok, (await guest.TakeSeatAsync(1, state.StateVersion)).Code);
        await owner.WaitForStateAsync(s => s.StateVersion == 2);
        return (owner, guest, created.RoomId);
    }

    [Fact]
    public async Task HandshakeWithoutAValidAccessTokenIsRefusedBeforeUpgrade()
    {
        await using var factory = CreateFactory();
        foreach (var header in new string?[] { null, "Bearer garbage", "Bearer v1.AAAA.AAAA" })
        {
            var client = factory.Server.CreateWebSocketClient();
            client.ConfigureRequest = request =>
            {
                if (header is not null)
                {
                    request.Headers.Authorization = header;
                }
            };
            await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(new Uri(factory.Server.BaseAddress, "realtime"), CancellationToken.None));
        }
        // 令牌放在查询字符串里也不行。
        var (_, token) = await RealtimeClient.CreateGuestAsync(factory);
        var query = factory.Server.CreateWebSocketClient();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            query.ConnectAsync(new Uri(factory.Server.BaseAddress, $"realtime?access_token={Uri.EscapeDataString(token)}"), CancellationToken.None));
    }

    [Fact]
    public async Task PlainHttpRequestToTheRealtimePathIsRejected()
    {
        await using var factory = CreateFactory();
        var (_, token) = await RealtimeClient.CreateGuestAsync(factory);
        using var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/realtime");
        request.Headers.Authorization = new("Bearer", token);

        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task AFullMatchCanBePlayedOverTheProtocol()
    {
        // 建房 → 入座 → 房主开始 → 双方轮流行动直到整场结束。全部经由协议，服务端在局间隔到期后自动发下一局
        // （局间隔为 0 时不自动发牌，所以这里设一个很短的值）。
        await using var factory = CreateFactory(("Room:NextHandDelay", "00:00:00.010"));
        var (owner, guest, _) = await TwoSeatedPlayersAsync(factory);
        await using var _o = owner;
        await using var _g = guest;

        Assert.Equal(ResultCode.Ok, (await owner.StartAsync(2)).Code);
        var clients = new[] { owner, guest };
        var random = new Random(7);
        for (int step = 0; step < 2000; step++)
        {
            FailIfClosed(owner, guest);
            var state = await owner.WaitForStateAsync(s => s.Stage != Protocol.V1.RoomStage.WaitingForSeats);
            if (state.Stage == Protocol.V1.RoomStage.MatchFinished)
            {
                return;
            }
            if (state.Stage == Protocol.V1.RoomStage.HandSettled)
            {
                await owner.WaitForStateAsync(s => s.StateVersion > state.StateVersion, TimeSpan.FromSeconds(10));
                continue;
            }
            var hand = state.Hand;
            int seat = hand.RerouteActingSeat >= 0 ? hand.RerouteActingSeat : hand.ActingSeat;
            var actor = clients[seat];
            var view = await actor.WaitForStateAsync(s => s.StateVersion >= state.StateVersion);
            Response response;
            if (view.Hand.RerouteActingSeat >= 0)
            {
                response = await actor.SendWithRetryAsync(new ClientMessage { Reroute = new Reroute { CommandId = RealtimeClient.NewId(), Pay = false, ExpectedVersion = view.StateVersion } });
            }
            else
            {
                var legal = view.Hand.YourLegalActions;
                Assert.NotNull(legal);
                var action = legal.CanCheck ? BetAction.Check : random.Next(4) == 0 && legal.CanFold ? BetAction.Fold : legal.CanCall ? BetAction.Call : BetAction.AllIn;
                response = await actor.SendWithRetryAsync(new ClientMessage { Bet = new Bet { CommandId = RealtimeClient.NewId(), Action = action, ExpectedVersion = view.StateVersion } });
            }
            Assert.Equal(ResultCode.Ok, response.Code);
            await owner.WaitForStateAsync(s => s.StateVersion >= response.AppliedVersion);
        }
        Assert.Fail("2000 步内没有打完整场。");
    }

    private static void FailIfClosed(params RealtimeClient[] clients)
    {
        foreach (var client in clients)
        {
            if (client.Closed.IsCompleted)
            {
                Assert.Fail($"连接被服务端关闭：{client.ClosingReason}");
            }
        }
    }

    [Fact]
    public async Task EachPlayerReceivesOnlyTheirOwnHoleCards()
    {
        await using var factory = CreateFactory();
        var (owner, guest, roomId) = await TwoSeatedPlayersAsync(factory);
        await using var _o = owner;
        await using var _g = guest;
        await using var spectator = await RealtimeClient.ConnectAsync(factory);
        Assert.Equal(ResultCode.Ok, (await spectator.JoinAsync(roomId)).Code);

        Assert.Equal(ResultCode.Ok, (await owner.StartAsync(2)).Code);
        var mine = await owner.WaitForStateAsync(s => s.YourHoleCards.Length == 2);
        var theirs = await guest.WaitForStateAsync(s => s.YourHoleCards.Length == 2);
        var watched = await spectator.WaitForStateAsync(s => s.Stage == Protocol.V1.RoomStage.HandInProgress);

        Assert.Equal(0, mine.YourSeat);
        Assert.Equal(1, theirs.YourSeat);
        Assert.Empty(mine.YourHoleCards.Intersect(theirs.YourHoleCards));
        Assert.Equal(-1, watched.YourSeat);
        Assert.True(watched.YourHoleCards.IsEmpty);
        // 别人的牌不出现在任何其他字段里（逐字节扫描整条消息）：底牌只有本人那一份。
        var guestBytes = theirs.YourHoleCards.ToByteArray();
        Assert.False(ContainsSequence(mine.Hand.ToByteArray(), guestBytes));
        Assert.Null(mine.Hand.Settlement);
        // 合法动作只发给轮到的人。
        int acting = mine.Hand.ActingSeat;
        Assert.NotNull((acting == 0 ? mine : theirs).Hand.YourLegalActions);
        Assert.Null((acting == 0 ? theirs : mine).Hand.YourLegalActions);
        Assert.Null(watched.Hand.YourLegalActions);
    }

    [Fact]
    public async Task OnlyTheOwnerCanStartAndOnlyWithACurrentView()
    {
        await using var factory = CreateFactory();
        var (owner, guest, _) = await TwoSeatedPlayersAsync(factory);
        await using var _o = owner;
        await using var _g = guest;

        Assert.True(owner.Latest!.YouAreOwner);
        Assert.False(guest.Latest!.YouAreOwner);
        Assert.Equal(ResultCode.Forbidden, (await guest.StartAsync(2)).Code);
        // 房主基于旧画面（版本 1，第二人入座前）开始：拒绝，不能用过时的名单开局。
        var stale = await owner.StartAsync(1);
        Assert.Equal(ResultCode.Rejected, stale.Code);
        Assert.Equal(Rejection.VersionMismatch, stale.Rejection);
        Assert.Equal(ResultCode.Ok, (await owner.StartAsync(2)).Code);
    }

    [Fact]
    public async Task PlayersCannotActForOthersOrOutOfTurn()
    {
        await using var factory = CreateFactory();
        var (owner, guest, _) = await TwoSeatedPlayersAsync(factory);
        await using var _o = owner;
        await using var _g = guest;
        Assert.Equal(ResultCode.Ok, (await owner.StartAsync(2)).Code);
        var state = await owner.WaitForStateAsync(s => s.Stage == Protocol.V1.RoomStage.HandInProgress);
        var notActing = state.Hand.ActingSeat == 0 ? guest : owner;

        // 没轮到的人下注：规则拒绝。协议里没有任何字段能指定“替哪个座位”下注。
        var view = await notActing.WaitForStateAsync(s => s.StateVersion == state.StateVersion);
        var response = await notActing.SendAsync(new ClientMessage { Bet = new Bet { CommandId = RealtimeClient.NewId(), Action = BetAction.Check, ExpectedVersion = view.StateVersion } });
        Assert.Equal(ResultCode.Rejected, response.Code);
        Assert.Equal(Rejection.NotYourTurn, response.Rejection);
    }

    [Theory]
    [InlineData("unspecified-action")]
    [InlineData("raise-without-target")]
    [InlineData("target-without-raise")]
    [InlineData("short-command-id")]
    [InlineData("zero-command-id")]
    [InlineData("negative-version")]
    [InlineData("empty-body")]
    public async Task MalformedCommandsAreInvalidAndDoNotReachTheRoom(string kind)
    {
        await using var factory = CreateFactory();
        var (owner, guest, _) = await TwoSeatedPlayersAsync(factory);
        await using var _o = owner;
        await using var _g = guest;
        Assert.Equal(ResultCode.Ok, (await owner.StartAsync(2)).Code);
        var state = await owner.WaitForStateAsync(s => s.Stage == Protocol.V1.RoomStage.HandInProgress);
        var actor = state.Hand.ActingSeat == 0 ? owner : guest;

        var bet = new Bet { CommandId = RealtimeClient.NewId(), Action = BetAction.Check, ExpectedVersion = state.StateVersion };
        var message = new ClientMessage { Bet = bet };
        switch (kind)
        {
            case "unspecified-action": bet.Action = BetAction.Unspecified; break;
            case "raise-without-target": bet.Action = BetAction.Raise; break;
            case "target-without-raise": bet.RaiseTarget = 5; break;
            case "short-command-id": bet.CommandId = ByteString.CopyFrom(new byte[8]); break;
            case "zero-command-id": bet.CommandId = ByteString.CopyFrom(new byte[16]); break;
            case "negative-version": bet.ExpectedVersion = -1; break;
            case "empty-body": message = new ClientMessage(); break;
        }

        Assert.Equal(ResultCode.Invalid, (await actor.SendAsync(message)).Code);
        // 没有进入房间：版本没变，连接仍然可用。
        Assert.Equal(state.StateVersion, (await actor.WaitForStateAsync(_ => true)).StateVersion);
        Assert.Equal(ResultCode.Ok, (await actor.SendAsync(new ClientMessage { Ping = new Ping() })).Code);
    }

    [Fact]
    public async Task UnparseableBytesCloseTheConnection()
    {
        await using var factory = CreateFactory();
        await using var client = await RealtimeClient.ConnectAsync(factory);

        await client.SendRawAsync([0xFF, 0xFF, 0xFF, 0xFF, 0x0F]);
        await client.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CloseReason.PolicyViolation, client.ClosingReason);
    }

    [Fact]
    public async Task TextMessagesCloseTheConnection()
    {
        await using var factory = CreateFactory();
        await using var client = await RealtimeClient.ConnectAsync(factory);

        await client.SendRawAsync("hello"u8.ToArray(), WebSocketMessageType.Text);
        await client.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CloseReason.PolicyViolation, client.ClosingReason);
    }

    [Fact]
    public async Task OversizedMessagesCloseTheConnection()
    {
        await using var factory = CreateFactory(("Realtime:MaxMessageBytes", "256"));
        await using var client = await RealtimeClient.ConnectAsync(factory);

        await client.SendRawAsync(new byte[10_000]);
        await client.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CloseReason.PolicyViolation, client.ClosingReason);
    }

    [Fact]
    public async Task FloodingMessagesClosesTheConnection()
    {
        await using var factory = CreateFactory(("Realtime:MessagesPerSecond", "5"));
        await using var client = await RealtimeClient.ConnectAsync(factory);
        var ping = new ClientMessage { Ping = new Ping(), RequestId = 1 }.ToByteArray();

        for (int i = 0; i < 50 && !client.Closed.IsCompleted; i++)
        {
            try
            {
                await client.SendRawAsync(ping);
            }
            catch (WebSocketException)
            {
                break;
            }
        }
        await client.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CloseReason.PolicyViolation, client.ClosingReason);
    }

    [Fact]
    public async Task AClientThatHonorsBusyIsNeverDisconnectedForSendingFast()
    {
        // 软上限 5 条/秒：连发 40 个 ping，按 BUSY 的退避重试，全部成功，连接不被关闭。
        await using var factory = CreateFactory(("Realtime:MessagesPerSecond", "5"));
        await using var client = await RealtimeClient.ConnectAsync(factory);

        for (int i = 0; i < 40; i++)
        {
            Assert.Equal(ResultCode.Ok, (await client.SendWithRetryAsync(new ClientMessage { Ping = new Ping() }, attempts: 100)).Code);
        }
        Assert.False(client.Closed.IsCompleted);
    }

    [Fact]
    public async Task ANewConnectionTakesControlAndTheOldOneBecomesReadOnly()
    {
        await using var factory = CreateFactory(("Realtime:MaxConnectionsPerPlayer", "2"));
        var identity = await RealtimeClient.CreateGuestAsync(factory);
        await using var first = await RealtimeClient.ConnectAsync(factory, identity);
        var created = await first.CreateRoomAsync(2);
        Assert.Equal(ResultCode.Ok, created.Code);

        await using var second = await RealtimeClient.ConnectAsync(factory, identity);
        Assert.Equal(ResultCode.Ok, (await second.JoinAsync(created.RoomId)).Code);
        await second.WaitForStateAsync(s => s.StateVersion == 0);

        // 旧连接仍收状态，但不能再操作。
        Assert.Equal(ResultCode.Forbidden, (await first.TakeSeatAsync(0, 0)).Code);
        Assert.Equal(ResultCode.Ok, (await second.TakeSeatAsync(0, 0)).Code);
        await first.WaitForStateAsync(s => s.StateVersion == 1);

        // 第三个连接超过上限：最旧的被取代并关闭。
        await using var third = await RealtimeClient.ConnectAsync(factory, identity);
        await first.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CloseReason.Replaced, first.ClosingReason);
    }

    [Fact]
    public async Task ConnectionIsClosedWhenTheAccessTokenExpires()
    {
        // 连接不能比令牌活得更久：否则封禁和吊销永远不生效。令牌有效期设为最短 1 分钟，用假时钟推进。
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = CreateFactory(("Session:AccessTokenLifetime", "00:01:00")).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(time)));
        await using var client = await RealtimeClient.ConnectAsync(factory);
        Assert.Equal(ResultCode.Ok, (await client.SendAsync(new ClientMessage { Ping = new Ping() })).Code);

        time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        await client.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CloseReason.TokenExpired, client.ClosingReason);
    }

    [Fact]
    public async Task RoomCreationIsCappedPerOwner()
    {
        await using var factory = CreateFactory(("Realtime:MaxOpenRoomsPerOwner", "2"));
        await using var client = await RealtimeClient.ConnectAsync(factory);

        Assert.Equal(ResultCode.Ok, (await client.CreateRoomAsync(2)).Code);
        Assert.Equal(ResultCode.Ok, (await client.CreateRoomAsync(2)).Code);
        Assert.Equal(ResultCode.Forbidden, (await client.CreateRoomAsync(2)).Code);
        Assert.Equal(ResultCode.Invalid, (await client.CreateRoomAsync(99)).Code);
    }

    [Fact]
    public async Task CommandsBeforeJoiningARoomAreInvalidAndUnknownRoomsAreNotFound()
    {
        await using var factory = CreateFactory();
        await using var client = await RealtimeClient.ConnectAsync(factory);

        Assert.Equal(ResultCode.Invalid, (await client.TakeSeatAsync(0, 0)).Code);
        Assert.Equal(ResultCode.NotFound, (await client.JoinAsync(RealtimeClient.NewId())).Code);
        Assert.Equal(ResultCode.Invalid, (await client.JoinAsync(ByteString.CopyFrom(new byte[3]))).Code);
    }

    [Fact]
    public async Task RetryingTheSameCommandIdReturnsTheOriginalResult()
    {
        // 网络重发：同一命令编号得到同一结果，版本不再前进。
        await using var factory = CreateFactory();
        await using var client = await RealtimeClient.ConnectAsync(factory);
        var created = await client.CreateRoomAsync(2);
        var take = new TakeSeat { CommandId = RealtimeClient.NewId(), Seat = 0, ExpectedVersion = 0 };

        var first = await client.SendAsync(new ClientMessage { TakeSeat = take });
        var retry = await client.SendAsync(new ClientMessage { TakeSeat = take.Clone() });

        Assert.Equal(ResultCode.Ok, first.Code);
        Assert.Equal(first.AppliedVersion, retry.AppliedVersion);
        Assert.Equal(1, (await client.WaitForStateAsync(s => s.StateVersion == 1)).StateVersion);
        _ = created;
    }

    [Fact]
    public async Task TimeoutsArePushedWithoutAnyClientAction()
    {
        // 计时开着：没人操作，服务端超时后推送新状态，客户端不需要轮询。
        await using var factory = CreateFactory(("Room:TurnTimeout", "00:00:01"), ("Room:NextHandDelay", "00:00:01"));
        var (owner, guest, _) = await TwoSeatedPlayersAsync(factory);
        await using var _o = owner;
        await using var _g = guest;
        Assert.Equal(ResultCode.Ok, (await owner.StartAsync(2)).Code);
        var started = await owner.WaitForStateAsync(s => s.Stage == Protocol.V1.RoomStage.HandInProgress);
        Assert.True(started.DeadlineUnixMs > 0);

        var after = await guest.WaitForStateAsync(s => s.StateVersion > started.StateVersion, TimeSpan.FromSeconds(10));
        Assert.True(after.StateVersion > started.StateVersion);
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }
        return false;
    }
}
