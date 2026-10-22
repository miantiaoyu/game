using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using DarkTide.Protocol.V1;
using Google.Protobuf;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DarkTide.Server.Tests;

/// <summary>
/// 测试用的协议客户端：只用公开协议，不碰服务端内部。收到的消息分成回复与状态两路，测试按需等待。
/// </summary>
internal sealed class RealtimeClient : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly Task _receive;
    private readonly System.Threading.Channels.Channel<Response> _responses = System.Threading.Channels.Channel.CreateUnbounded<Response>();
    private readonly System.Threading.Channels.Channel<RoomState> _states = System.Threading.Channels.Channel.CreateUnbounded<RoomState>();
    private uint _nextRequest;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _trace = new();

    /// <summary>最近发送的消息与收到的回复（时间、类型、结果），连接被关闭时附在异常里，便于定位。</summary>
    public string Trace => string.Join(" | ", _trace.TakeLast(30));

    public Guid PlayerId { get; }
    public string AccessToken { get; }
    public RoomState? Latest { get; private set; }
    public CloseReason? ClosingReason { get; private set; }
    public Task Closed => _receive;

    private RealtimeClient(WebSocket socket, Guid playerId, string accessToken)
    {
        _socket = socket;
        PlayerId = playerId;
        AccessToken = accessToken;
        _receive = ReceiveAsync();
    }

    public static async Task<(Guid PlayerId, string AccessToken)> CreateGuestAsync(WebApplicationFactory<Program> factory)
    {
        using var http = factory.CreateClient();
        var response = await http.PostAsync("/sessions/guest", null);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("playerId").GetGuid(), json.RootElement.GetProperty("accessToken").GetString()!);
    }

    public static async Task<RealtimeClient> ConnectAsync(WebApplicationFactory<Program> factory, (Guid PlayerId, string AccessToken)? identity = null)
    {
        var (playerId, token) = identity ?? await CreateGuestAsync(factory);
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {token}";
        var socket = await client.ConnectAsync(new Uri(factory.Server.BaseAddress, "realtime"), CancellationToken.None);
        return new RealtimeClient(socket, playerId, token);
    }

    public async Task<Response> SendAsync(ClientMessage message)
    {
        message.RequestId = ++_nextRequest;
        _trace.Enqueue($"{DateTime.UtcNow:ss.fff} 发 {message.BodyCase}#{message.RequestId}");
        await SendRawAsync(message.ToByteArray());
        while (true)
        {
            Response response;
            try
            {
                response = await _responses.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                throw new InvalidOperationException($"等待回复时连接被关闭，原因：{ClosingReason?.ToString() ?? "未说明"}。最近：{Trace}");
            }
            if (response.RequestId == message.RequestId)
            {
                _trace.Enqueue($"{DateTime.UtcNow:ss.fff} 收 #{response.RequestId} {response.Code} 退避{response.RetryAfterMs}");
                return response;
            }
        }
    }

    /// <summary>
    /// 按协议约定处理 BUSY：等 retry_after_ms（没有建议时 50 毫秒）后用同一条消息（同一命令编号）重试。
    /// 这是正确客户端的行为：BUSY 表示没被接受也没被拒绝，换编号重试才可能重复执行。
    /// </summary>
    public async Task<Response> SendWithRetryAsync(ClientMessage message, int attempts = 50)
    {
        for (int attempt = 1; ; attempt++)
        {
            var response = await SendAsync(message.Clone());
            if (response.Code != ResultCode.Busy || attempt == attempts)
            {
                return response;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(response.RetryAfterMs > 0 ? response.RetryAfterMs : 50));
        }
    }

    public Task SendRawAsync(byte[] bytes, WebSocketMessageType type = WebSocketMessageType.Binary) =>
        _socket.SendAsync(bytes, type, endOfMessage: true, CancellationToken.None);

    /// <summary>等到一份满足条件的状态（含已收到的最新一份）。</summary>
    public async Task<RoomState> WaitForStateAsync(Func<RoomState, bool> predicate, TimeSpan? timeout = null)
    {
        if (Latest is { } latest && predicate(latest))
        {
            return latest;
        }
        using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            RoomState state;
            try
            {
                state = await _states.Reader.ReadAsync(cancel.Token);
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                throw new InvalidOperationException($"等待状态时连接被关闭，原因：{ClosingReason?.ToString() ?? "未说明"}。");
            }
            if (predicate(state))
            {
                return state;
            }
        }
    }

    public Task<Response> CreateRoomAsync(uint seats) => SendAsync(new ClientMessage { CreateRoom = new CreateRoom { SeatCount = seats } });

    public Task<Response> JoinAsync(ByteString roomId) => SendAsync(new ClientMessage { JoinRoom = new JoinRoom { RoomId = roomId } });

    public Task<Response> TakeSeatAsync(uint seat, long expectedVersion) => SendAsync(new ClientMessage
    {
        TakeSeat = new TakeSeat { CommandId = NewId(), Seat = seat, ExpectedVersion = expectedVersion }
    });

    public Task<Response> StartAsync(long expectedVersion) => SendAsync(new ClientMessage { StartMatch = new StartMatch { ExpectedVersion = expectedVersion } });

    public static ByteString NewId() => ByteString.CopyFrom(Guid.NewGuid().ToByteArray(bigEndian: true));

    private async Task ReceiveAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                int total = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(total), CancellationToken.None) is var r
                        ? new WebSocketReceiveResult(r.Count, r.MessageType, r.EndOfMessage)
                        : throw new InvalidOperationException();
                    total += result.Count;
                }
                while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                var message = ServerMessage.Parser.ParseFrom(buffer, 0, total);
                switch (message.BodyCase)
                {
                    case ServerMessage.BodyOneofCase.Response:
                        _responses.Writer.TryWrite(message.Response);
                        break;
                    case ServerMessage.BodyOneofCase.State:
                        Latest = message.State;
                        _states.Writer.TryWrite(message.State);
                        break;
                    case ServerMessage.BodyOneofCase.Closing:
                        ClosingReason = message.Closing.Reason;
                        break;
                }
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _responses.Writer.TryComplete();
            _states.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        await _receive.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { });
        _socket.Dispose();
    }
}
