using System.Buffers;
using System.Net.WebSockets;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using DarkTide.Protocol.V1;
using DarkTide.Room;
using DarkTide.Rules;
using Google.Protobuf;

namespace DarkTide.Server.Realtime;

/// <summary>
/// 一个已认证的 WebSocket 连接。三个循环，各自独立结束：
/// - 接收：读一条完整消息（有字节上限）→ 限速 → 解析 → 处理，回复放进回复通道；
/// - 推送：等订阅房间的已发布快照被替代，取最新的一份，按本人视角投影后发送。没有队列：慢客户端跳过中间版本，
///   内存与客户端快慢无关。回复与推送共用一把发送锁（WebSocket 同一时刻只能有一个发送）；
/// - 过期：访问令牌到期时关闭连接，客户端换新令牌后重连。连接不能比令牌活得更久，否则吊销与封禁永远不生效。
///
/// 命令只经 <see cref="RoomExecutor.ExecuteAsync"/> 提交，玩家编号一律取自认证身份，客户端消息里没有任何字段能指定别人。
/// </summary>
internal sealed class RoomConnection
{
    // 回复通道：每条请求最多一条回复，请求又受速率限制，所以很小的容量就够；满了说明客户端不读，按太慢关闭。
    private const int ResponseCapacity = 64;

    private readonly WebSocket _socket;
    private readonly Guid _playerId;
    private readonly DateTimeOffset _tokenExpiresAt;
    private readonly RoomDirectory _directory;
    private readonly ConnectionRegistry _registry;
    private readonly RealtimeOptions _options;
    private readonly RoomCreationLimits _roomLimits;
    private readonly TokenBucketRateLimiter _roomCreations;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly RealtimeMetrics _metrics;
    private readonly PlayerConnection _registration;

    // 软上限：超出回复 BUSY。硬上限：超出关闭连接。见 RealtimeOptions.MessagesPerSecond。
    private readonly TokenBucketRateLimiter _inbound;
    private readonly TokenBucketRateLimiter _inboundHard;
    private readonly Channel<ServerMessage> _responses = Channel.CreateBounded<ServerMessage>(
        new BoundedChannelOptions(ResponseCapacity) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _closing = new();

    // 只有接收循环写，推送循环读：换订阅时整体替换这个引用，推送循环下一轮就跟着换。
    private volatile RoomExecutor? _subscribed;
    // 唤醒推送循环：换订阅时让它不再等旧房间。
    private volatile TaskCompletionSource _subscriptionChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _closeReason;

    public RoomConnection(WebSocket socket, Guid playerId, DateTimeOffset tokenExpiresAt, RoomDirectory directory, ConnectionRegistry registry,
        RealtimeOptions options, TokenBucketRateLimiter roomCreations, TimeProvider time, ILogger logger, RealtimeMetrics metrics)
    {
        _socket = socket;
        _playerId = playerId;
        _tokenExpiresAt = tokenExpiresAt;
        _directory = directory;
        _registry = registry;
        _options = options;
        _roomLimits = new RoomCreationLimits(options.MaxRooms, options.MaxOpenRoomsPerOwner);
        _roomCreations = roomCreations;
        _time = time;
        _logger = logger;
        _metrics = metrics;
        _registration = new PlayerConnection(playerId, RequestClose);
        _inbound = PerSecond(options.MessagesPerSecond);
        _inboundHard = PerSecond(options.MessagesPerSecond * RealtimeOptions.HardLimitMultiplier);
    }

    private static TokenBucketRateLimiter PerSecond(int rate) => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = rate,
        TokensPerPeriod = rate,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    });

    internal PlayerConnection Registration => _registration;

    /// <summary>运行到连接关闭。不抛异常：网络错误、客户端断开都是正常结束。</summary>
    public async Task RunAsync(CancellationToken shutdown)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown, _closing.Token);
        var token = linked.Token;
        var receive = ReceiveLoopAsync(token);
        var push = PushLoopAsync(token);
        var expiry = ExpireAsync(token);
        try
        {
            // 任何一个结束（客户端断开、推送失败、令牌到期）都结束整个连接。
            await Task.WhenAny(receive, push, expiry).ConfigureAwait(false);
            if (shutdown.IsCancellationRequested)
            {
                RequestClose(CloseReason.ServerShutdown);
            }
        }
        finally
        {
            _closing.Cancel();
            await CloseSocketAsync().ConfigureAwait(false);
            // 三个循环都已收到取消；等它们退出，异常已在各自内部处理。
            await Task.WhenAll(receive.ContinueWith(_ => { }), push.ContinueWith(_ => { }), expiry.ContinueWith(_ => { })).ConfigureAwait(false);
            _inbound.Dispose();
            _inboundHard.Dispose();
            _closing.Dispose();
        }
    }

    /// <summary>请求关闭：记下第一个原因，停止所有循环。可以从任何线程调用。</summary>
    public void RequestClose(CloseReason reason)
    {
        Interlocked.CompareExchange(ref _closeReason, (int)reason, 0);
        try
        {
            _closing.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 连接已经结束。
        }
    }

    // ───────────────────────────── 接收 ─────────────────────────────

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_options.MaxMessageBytes);
        try
        {
            while (!token.IsCancellationRequested)
            {
                int length = await ReceiveMessageAsync(buffer, token).ConfigureAwait(false);
                if (length < 0)
                {
                    return;
                }
                // 硬上限在解析之前：无视退避的洪水连解析都不做。
                using (var hard = _inboundHard.AttemptAcquire())
                {
                    if (!hard.IsAcquired)
                    {
                        _metrics.Closed("rate_limited");
                        RequestClose(CloseReason.PolicyViolation);
                        return;
                    }
                }

                ClientMessage message;
                try
                {
                    message = ClientMessage.Parser.ParseFrom(buffer, 0, length);
                }
                catch (InvalidProtocolBufferException)
                {
                    _metrics.Closed("malformed");
                    RequestClose(CloseReason.PolicyViolation);
                    return;
                }

                Response? response;
                using (var soft = _inbound.AttemptAcquire())
                {
                    // 软上限在解析之后：回复要带上请求编号，客户端才知道是哪一条要重试。
                    response = soft.IsAcquired ? await HandleAsync(message).ConfigureAwait(false) : Throttled(message, soft);
                }
                if (response is not null && !_responses.Writer.TryWrite(new ServerMessage { Response = response }))
                {
                    // 回复积压满了：客户端在发，却不读。
                    _metrics.Closed("too_slow");
                    RequestClose(CloseReason.TooSlow);
                    return;
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // 关闭或网络断开。
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>读一条完整的二进制消息。超过上限、文本消息都按违规关闭；对方关闭返回 -1。</summary>
    private async Task<int> ReceiveMessageAsync(byte[] buffer, CancellationToken token)
    {
        int total = 0;
        while (true)
        {
            // 按整条消息计算上限：分片再多，加起来也不能超过 MaxMessageBytes。
            if (total >= _options.MaxMessageBytes)
            {
                _metrics.Closed("message_too_large");
                RequestClose(CloseReason.PolicyViolation);
                return -1;
            }
            var result = await _socket.ReceiveAsync(buffer.AsMemory(total, _options.MaxMessageBytes - total), token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return -1;
            }
            if (result.MessageType != WebSocketMessageType.Binary)
            {
                _metrics.Closed("not_binary");
                RequestClose(CloseReason.PolicyViolation);
                return -1;
            }
            total += result.Count;
            if (result.EndOfMessage)
            {
                return total;
            }
        }
    }

    // ───────────────────────────── 处理请求 ─────────────────────────────

    private async Task<Response?> HandleAsync(ClientMessage message)
    {
        var response = new Response { RequestId = message.RequestId };
        try
        {
            switch (message.BodyCase)
            {
                case ClientMessage.BodyOneofCase.Ping:
                    return Ok(response);
                case ClientMessage.BodyOneofCase.CreateRoom:
                    return await CreateRoomAsync(message.CreateRoom, response).ConfigureAwait(false);
                case ClientMessage.BodyOneofCase.JoinRoom:
                    return JoinRoom(message.JoinRoom, response);
                case ClientMessage.BodyOneofCase.StartMatch:
                    return await StartMatchAsync(message.StartMatch, response).ConfigureAwait(false);
                case ClientMessage.BodyOneofCase.TakeSeat:
                case ClientMessage.BodyOneofCase.LeaveSeat:
                case ClientMessage.BodyOneofCase.Bet:
                case ClientMessage.BodyOneofCase.Reroute:
                    return await ExecuteAsync(message, response).ConfigureAwait(false);
                default:
                    // 没有内容或未知的消息类型（新版客户端发给旧服务端）：明确拒绝，不猜测。
                    return Code(response, ResultCode.Invalid);
            }
        }
        catch (RuleRejectedException error)
        {
            response.Code = ResultCode.Rejected;
            response.Rejection = ProtocolMapping.ToRejection(error.Reason);
            return response;
        }
        catch (RoomBusyException error)
        {
            response.Code = ResultCode.Busy;
            response.RetryAfterMs = error.RetryAfter is { } retry ? (uint)Math.Ceiling(retry.TotalMilliseconds) : 0;
            return response;
        }
        catch (Exception error) when (error is RoomFaultedException or RoomPersistenceException or ObjectDisposedException)
        {
            // 房间故障或已停止（执行器以 ObjectDisposedException 拒绝新请求）：结果可能未知（持久化故障），
            // 客户端稍后重新加入，用同一命令编号重试是安全的。
            return Code(response, ResultCode.Unavailable);
        }
    }

    private async Task<Response> CreateRoomAsync(CreateRoom request, Response response)
    {
        if (request.SeatCount < TableLimits.MinSeats || request.SeatCount > TableLimits.MaxSeats)
        {
            return Code(response, ResultCode.Invalid);
        }
        using (var lease = _roomCreations.AttemptAcquire())
        {
            if (!lease.IsAcquired)
            {
                response.RetryAfterMs = 1000;
                return Code(response, ResultCode.Busy);
            }
        }
        RoomExecutor room;
        try
        {
            room = await _directory.CreateRoomAsync(Guid.CreateVersion7(), (int)request.SeatCount, _closing.Token, _playerId, _roomLimits)
                .ConfigureAwait(false);
        }
        catch (RoomLimitExceededException error)
        {
            return Code(response, error.Limit == RoomLimit.PerOwner ? ResultCode.Forbidden : ResultCode.Unavailable);
        }
        catch (InvalidOperationException)
        {
            // 进程正在关闭。
            return Code(response, ResultCode.Unavailable);
        }
        _metrics.RoomCreated();
        Subscribe(room);
        response.RoomId = ProtocolMapping.ToBytes(room.RoomId);
        return Ok(response);
    }

    private Response JoinRoom(JoinRoom request, Response response)
    {
        if (!ProtocolMapping.TryReadGuid(request.RoomId, out var roomId))
        {
            return Code(response, ResultCode.Invalid);
        }
        if (!_directory.TryGetRoom(roomId, out var room))
        {
            // 重建中的房间也报“不可用”而不是“不存在”：客户端应稍后重试，而不是告诉玩家房间没了。
            return Code(response, _directory.IsRecovering(roomId) ? ResultCode.Unavailable : ResultCode.NotFound);
        }
        Subscribe(room);
        response.RoomId = request.RoomId;
        return Ok(response);
    }

    private async Task<Response> StartMatchAsync(StartMatch request, Response response)
    {
        if (CheckCanAct(response, out var room) is { } refused)
        {
            return refused;
        }
        if (room.CreatedBy != _playerId)
        {
            return Code(response, ResultCode.Forbidden);
        }
        var receipt = await room.StartMatchAsync(request.ExpectedVersion).ConfigureAwait(false);
        response.AppliedVersion = receipt.CommittedVersion;
        return Ok(response);
    }

    private async Task<Response> ExecuteAsync(ClientMessage message, Response response)
    {
        if (CheckCanAct(response, out var room) is { } refused)
        {
            return refused;
        }
        var command = ToCommand(message);
        if (command is null)
        {
            return Code(response, ResultCode.Invalid);
        }
        var receipt = await room.ExecuteAsync(command).ConfigureAwait(false);
        response.AppliedVersion = receipt.CommittedVersion;
        return Ok(response);
    }

    /// <summary>
    /// 协议消息 → 房间命令。玩家编号只来自认证身份。任何字段不合法都返回 null（INVALID），不抛给房间：
    /// 房间把参数异常当作服务端缺陷，而这里的错误来自客户端。
    /// </summary>
    private RoomCommand? ToCommand(ClientMessage message)
    {
        try
        {
            switch (message.BodyCase)
            {
                case ClientMessage.BodyOneofCase.TakeSeat:
                {
                    var take = message.TakeSeat;
                    if (!ProtocolMapping.TryReadGuid(take.CommandId, out var id) || take.Seat >= TableLimits.MaxSeats || take.ExpectedVersion < 0)
                    {
                        return null;
                    }
                    return new TakeSeatCommand(id, _playerId, (int)take.Seat, take.ExpectedVersion);
                }
                case ClientMessage.BodyOneofCase.LeaveSeat:
                {
                    var leave = message.LeaveSeat;
                    if (!ProtocolMapping.TryReadGuid(leave.CommandId, out var id) || leave.ExpectedVersion < 0)
                    {
                        return null;
                    }
                    return new LeaveSeatCommand(id, _playerId, leave.ExpectedVersion);
                }
                case ClientMessage.BodyOneofCase.Bet:
                {
                    var bet = message.Bet;
                    if (!ProtocolMapping.TryReadGuid(bet.CommandId, out var id) || bet.ExpectedVersion < 0
                        || ProtocolMapping.ToBettingAction(bet.Action) is not BettingAction action
                        || bet.RaiseTarget > BetCommand.MaxRaiseTarget
                        || (action == BettingAction.Raise) != (bet.RaiseTarget > 0))
                    {
                        return null;
                    }
                    return new BetCommand(id, _playerId, action, (int)bet.RaiseTarget, bet.ExpectedVersion);
                }
                case ClientMessage.BodyOneofCase.Reroute:
                {
                    var reroute = message.Reroute;
                    if (!ProtocolMapping.TryReadGuid(reroute.CommandId, out var id) || reroute.ExpectedVersion < 0)
                    {
                        return null;
                    }
                    return new RerouteCommand(id, _playerId, reroute.Pay, reroute.ExpectedVersion);
                }
                default:
                    return null;
            }
        }
        catch (ArgumentException)
        {
            // 构造函数的格式检查兜底：上面漏掉的组合也只会变成 INVALID。
            return null;
        }
    }

    /// <summary>能否在当前订阅的房间里操作：必须是该玩家的控制连接，且已订阅一个仍在运行的房间。不能时返回拒绝回复。</summary>
    private Response? CheckCanAct(Response response, out RoomExecutor room)
    {
        room = _subscribed!;
        if (!_registry.IsController(_registration))
        {
            return Code(response, ResultCode.Forbidden);
        }
        if (room is null)
        {
            return Code(response, ResultCode.Invalid);
        }
        return room.IsStopped ? Code(response, ResultCode.Unavailable) : null;
    }

    private void Subscribe(RoomExecutor room)
    {
        _subscribed = room;
        Interlocked.Exchange(ref _subscriptionChanged, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    private Response Throttled(ClientMessage message, RateLimitLease lease)
    {
        _metrics.Throttled();
        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var hint) ? hint : TimeSpan.FromMilliseconds(100);
        return new Response
        {
            RequestId = message.RequestId,
            Code = ResultCode.Busy,
            RetryAfterMs = (uint)Math.Max(1, Math.Ceiling(retryAfter.TotalMilliseconds)),
        };
    }

    private static Response Ok(Response response) => Code(response, ResultCode.Ok);

    private static Response Code(Response response, ResultCode code)
    {
        response.Code = code;
        return response;
    }

    // ───────────────────────────── 推送 ─────────────────────────────

    private async Task PushLoopAsync(CancellationToken token)
    {
        long sentVersion = -1;
        DateTimeOffset? sentDeadline = null;
        RoomExecutor? sentRoom = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                // 先发积压的回复：客户端拿到命令结果后，紧接着收到的状态一定不比它旧。
                while (_responses.Reader.TryRead(out var reply))
                {
                    await SendAsync(reply, token).ConfigureAwait(false);
                }

                var room = _subscribed;
                var subscriptionChanged = _subscriptionChanged.Task;
                if (room is not null)
                {
                    var published = room.Published;
                    bool changed = !ReferenceEquals(room, sentRoom) || published.StateVersion != sentVersion || published.Deadline != sentDeadline;
                    if (changed)
                    {
                        var state = ProtocolMapping.ToRoomState(published.ViewFor(_playerId), room.CreatedBy, _playerId, _time.GetUtcNow());
                        await SendAsync(new ServerMessage { State = state }, token).ConfigureAwait(false);
                        _metrics.StatePushed();
                        (sentRoom, sentVersion, sentDeadline) = (room, published.StateVersion, published.Deadline);
                    }
                    if (room.IsStopped)
                    {
                        // 房间关闭或故障：告诉客户端，结束连接。故障重建后客户端重新加入即可。
                        RequestClose(CloseReason.RoomClosed);
                        return;
                    }
                    await Task.WhenAny(published.Superseded, subscriptionChanged, _responses.Reader.WaitToReadAsync(token).AsTask())
                        .ConfigureAwait(false);
                }
                else
                {
                    await Task.WhenAny(subscriptionChanged, _responses.Reader.WaitToReadAsync(token).AsTask()).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // 关闭、网络断开或发送超时。
        }
    }

    /// <summary>发送一条消息。每次发送都有时限：客户端不读，发送缓冲满了会一直挂着，超时就当作太慢关闭。</summary>
    private async Task SendAsync(ServerMessage message, CancellationToken token)
    {
        int size = message.CalculateSize();
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        await _sendLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            message.WriteTo(buffer.AsSpan(0, size));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_options.SendTimeout);
            try
            {
                await _socket.SendAsync(buffer.AsMemory(0, size), WebSocketMessageType.Binary, endOfMessage: true, timeout.Token).ConfigureAwait(false);
                _metrics.BytesSent(size);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                _metrics.Closed("too_slow");
                RequestClose(CloseReason.TooSlow);
                throw;
            }
        }
        finally
        {
            _sendLock.Release();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ───────────────────────────── 生命周期 ─────────────────────────────

    private async Task ExpireAsync(CancellationToken token)
    {
        var remaining = _tokenExpiresAt - _time.GetUtcNow();
        try
        {
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, _time, token).ConfigureAwait(false);
            }
            _metrics.Closed("token_expired");
            RequestClose(CloseReason.TokenExpired);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CloseSocketAsync()
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }
        var reason = (CloseReason)Volatile.Read(ref _closeReason);
        // 关闭握手也要有时限：对方不回应时不能挂着。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            if (reason != CloseReason.Unspecified && _socket.State == WebSocketState.Open)
            {
                // 尽力告诉客户端原因；发送锁可能被卡住的推送占着，所以同样有时限。
                if (await _sendLock.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false))
                {
                    try
                    {
                        var closing = new ServerMessage { Closing = new Closing { Reason = reason } }.ToByteArray();
                        await _socket.SendAsync(closing, WebSocketMessageType.Binary, true, timeout.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _sendLock.Release();
                    }
                }
            }
            var status = reason switch
            {
                CloseReason.PolicyViolation => WebSocketCloseStatus.PolicyViolation,
                CloseReason.ServerShutdown => WebSocketCloseStatus.EndpointUnavailable,
                CloseReason.Unspecified => WebSocketCloseStatus.NormalClosure,
                _ => WebSocketCloseStatus.NormalClosure
            };
            await _socket.CloseOutputAsync(status, null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            _socket.Abort();
        }
    }
}
