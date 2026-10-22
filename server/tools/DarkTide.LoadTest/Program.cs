using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using DarkTide.Protocol.V1;
using Google.Protobuf;

// 端到端压测：对一个真实运行的服务器（Kestrel + PostgreSQL），用真实的 WebSocket 客户端打真实的对局。
// 测的是整条路径：认证、建房、入座、下注推送、按接收者投影、写日志。服务器要单独启动（见 docs/开发进度.md）。
//
// 用法：dotnet run -c Release --project server/tools/DarkTide.LoadTest -- <服务器地址> <房间数> <每房人数> <思考毫秒> <秒数>
// 例如：dotnet run -c Release --project server/tools/DarkTide.LoadTest -- http://127.0.0.1:5080 400 5 1000 30
var baseUri = new Uri(args.Length > 0 ? args[0] : "http://127.0.0.1:5080");
int rooms = args.Length > 1 ? int.Parse(args[1]) : 100;
int seats = args.Length > 2 ? int.Parse(args[2]) : 5;
var think = TimeSpan.FromMilliseconds(args.Length > 3 ? int.Parse(args[3]) : 1000);
var duration = TimeSpan.FromSeconds(args.Length > 4 ? int.Parse(args[4]) : 30);

Console.WriteLine($"== 端到端：{rooms} 个房间 × {seats} 人 = {rooms * seats} 个连接，思考约 {think.TotalMilliseconds:F0} ms，持续 {duration.TotalSeconds:F0} s ==");
using var http = new HttpClient { BaseAddress = baseUri };
var stats = new Stats();
var setup = Stopwatch.StartNew();
var tables = new Table[rooms];
// 建立连接并行做，但有并发上限：一次性发起上万个握手会先把本机打满，而不是测服务器。
await Parallel.ForEachAsync(Enumerable.Range(0, rooms), new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (i, _) =>
{
    tables[i] = await Table.CreateAsync(http, baseUri, seats, stats);
});
Console.WriteLine($"  建立 {rooms * seats} 个连接、建房入座、开局用时 {setup.Elapsed.TotalSeconds:F1} s");

stats.Reset();
using var stop = new CancellationTokenSource(duration);
var watch = Stopwatch.StartNew();
await Task.WhenAll(tables.Select((table, i) => Task.Run(() => table.PlayAsync(think, new Random(i), stop.Token))));
watch.Stop();
stats.Report(watch.Elapsed, rooms * seats);
foreach (var table in tables)
{
    await table.DisposeAsync();
}

sealed class Stats
{
    private readonly Lock _lock = new();
    private List<double> _actionLatencies = [];
    private List<double> _pushLatencies = [];
    private long _states, _bytes, _busy, _errors, _closed, _preempted;

    public void Reset()
    {
        lock (_lock)
        {
            _actionLatencies = [];
            _pushLatencies = [];
            _states = _bytes = _busy = _errors = _closed = _preempted = 0;
        }
    }

    public void Action(double ms) { lock (_lock) { _actionLatencies.Add(ms); } }
    public void Push(double ms) { lock (_lock) { _pushLatencies.Add(ms); } }
    public void State(int bytes) { Interlocked.Increment(ref _states); Interlocked.Add(ref _bytes, bytes); }
    public void Busy() => Interlocked.Increment(ref _busy);
    public void Error() => Interlocked.Increment(ref _errors);
    public void Closed() => Interlocked.Increment(ref _closed);
    public void Preempted() => Interlocked.Increment(ref _preempted);

    public void Report(TimeSpan elapsed, int connections)
    {
        double[] actions, pushes;
        lock (_lock)
        {
            actions = _actionLatencies.Order().ToArray();
            pushes = _pushLatencies.Order().ToArray();
        }
        double seconds = elapsed.TotalSeconds;
        Console.WriteLine($"  动作确认  {actions.Length / seconds,7:F0}/s  p50 {P(actions, .5),6:F1}  p95 {P(actions, .95),6:F1}  p99 {P(actions, .99),6:F1}  max {(actions.Length > 0 ? actions[^1] : 0),7:F1} ms");
        Console.WriteLine($"  他人收到  p50 {P(pushes, .5),6:F1}  p95 {P(pushes, .95),6:F1}  p99 {P(pushes, .99),6:F1} ms（从行动者发出到同桌另一人收到新版本）");
        Console.WriteLine($"  推送 {_states / seconds:F0} 份/s，{_bytes / seconds / 1024:F0} KiB/s，平均 {(_states > 0 ? _bytes / _states : 0)} 字节/份");
        Console.WriteLine($"  BUSY {_busy}，错误 {_errors}，被超时抢先 {_preempted}，服务端关闭的连接 {_closed} / {connections}");
    }

    private static double P(double[] sorted, double q) => sorted.Length == 0 ? 0 : sorted[Math.Max(0, (int)Math.Ceiling(q * sorted.Length) - 1)];
}

sealed class Table : IAsyncDisposable
{
    private readonly Client[] _players;

    private Table(Client[] players) => _players = players;

    public static async Task<Table> CreateAsync(HttpClient http, Uri baseUri, int seats, Stats stats)
    {
        var players = new Client[seats];
        for (int i = 0; i < seats; i++)
        {
            players[i] = await Client.ConnectAsync(http, baseUri, stats);
        }
        var created = await players[0].RequestAsync(new ClientMessage { CreateRoom = new CreateRoom { SeatCount = (uint)seats } });
        if (created.Code != ResultCode.Ok)
        {
            throw new InvalidOperationException($"建房失败：{created.Code}");
        }
        for (int i = 1; i < seats; i++)
        {
            await players[i].RequestAsync(new ClientMessage { JoinRoom = new JoinRoom { RoomId = created.RoomId } });
        }
        for (int i = 0; i < seats; i++)
        {
            var state = await players[i].WaitForAsync(s => s.StateVersion >= i);
            var take = await players[i].RequestWithRetryAsync(new ClientMessage
            {
                TakeSeat = new TakeSeat { CommandId = Client.NewId(), Seat = (uint)i, ExpectedVersion = i }
            });
            if (take.Code != ResultCode.Ok)
            {
                throw new InvalidOperationException($"入座失败：{take.Code} {take.Rejection}");
            }
            _ = state;
        }
        await players[0].WaitForAsync(s => s.StateVersion >= seats);
        var start = await players[0].RequestWithRetryAsync(new ClientMessage { StartMatch = new StartMatch { ExpectedVersion = seats } });
        if (start.Code != ResultCode.Ok)
        {
            throw new InvalidOperationException($"开局失败：{start.Code} {start.Rejection}");
        }
        return new Table(players);
    }

    public async Task PlayAsync(TimeSpan think, Random random, CancellationToken stop)
    {
        var observer = _players[0];
        while (!stop.IsCancellationRequested)
        {
            RoomState state;
            try
            {
                state = await observer.WaitForAsync(s => s.Stage is RoomStage.HandInProgress or RoomStage.MatchFinished, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (state.Stage == RoomStage.MatchFinished || observer.IsClosed)
            {
                return;
            }
            int seat = state.Hand.RerouteActingSeat >= 0 ? state.Hand.RerouteActingSeat : state.Hand.ActingSeat;
            if (seat < 0)
            {
                await observer.WaitForAsync(s => s.StateVersion > state.StateVersion, stop).ContinueWith(_ => { });
                continue;
            }
            var actor = _players[seat];
            var witness = _players[(seat + 1) % _players.Length];
            try
            {
                var view = await actor.WaitForAsync(s => s.StateVersion >= state.StateVersion, stop);
                if (view.StateVersion != state.StateVersion)
                {
                    continue;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(think.TotalMilliseconds * (0.5 + random.NextDouble())), stop);
                var message = Choose(view, random);
                var sent = Stopwatch.GetTimestamp();
                var response = await actor.RequestWithRetryAsync(message);
                if (response.Code == ResultCode.Ok)
                {
                    actor.Stats.Action(Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
                    await witness.WaitForAsync(s => s.StateVersion >= response.AppliedVersion, stop);
                    actor.Stats.Push(Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
                }
                else if (response.Code == ResultCode.Rejected && response.Rejection is Rejection.VersionMismatch or Rejection.NotYourTurn)
                {
                    // 思考期间超时已经替他行动了。
                    actor.Stats.Preempted();
                }
                else
                {
                    actor.Stats.Error();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static ClientMessage Choose(RoomState view, Random random)
    {
        if (view.Hand.RerouteActingSeat >= 0)
        {
            return new ClientMessage { Reroute = new Reroute { CommandId = Client.NewId(), Pay = random.Next(4) == 0, ExpectedVersion = view.StateVersion } };
        }
        var legal = view.Hand.YourLegalActions;
        int roll = random.Next(10);
        var bet = new Bet { CommandId = Client.NewId(), ExpectedVersion = view.StateVersion };
        if (legal.CanRaise && roll == 0)
        {
            bet.Action = BetAction.Raise;
            bet.RaiseTarget = legal.MinRaiseTarget;
        }
        else if (legal.CanFold && roll <= 2)
        {
            bet.Action = BetAction.Fold;
        }
        else
        {
            bet.Action = legal.CanCheck ? BetAction.Check : legal.CanCall ? BetAction.Call : BetAction.AllIn;
        }
        return new ClientMessage { Bet = bet };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var player in _players)
        {
            await player.DisposeAsync();
        }
    }
}

sealed class Client : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly Task _receive;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Dictionary<uint, TaskCompletionSource<Response>> _pending = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RoomState? _latest;
    private uint _nextRequest;

    public Stats Stats { get; }
    public bool IsClosed => _receive.IsCompleted;

    private Client(ClientWebSocket socket, Stats stats)
    {
        _socket = socket;
        Stats = stats;
        _receive = ReceiveAsync();
    }

    public static async Task<Client> ConnectAsync(HttpClient http, Uri baseUri, Stats stats)
    {
        // 503（会话并发上限）与 429（限流）是可重试的：按 Retry-After 退避，和真实客户端一样。
        HttpResponseMessage guest;
        for (int attempt = 0; ; attempt++)
        {
            guest = await http.PostAsync("/sessions/guest", null);
            if (guest.StatusCode is not (System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests) || attempt >= 100)
            {
                break;
            }
            await Task.Delay(guest.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(20 + Random.Shared.Next(50)));
        }
        guest.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await guest.Content.ReadAsStringAsync());
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {json.RootElement.GetProperty("accessToken").GetString()}");
        socket.Options.AddSubProtocol("darktide.v1");
        var ws = new UriBuilder(baseUri) { Scheme = baseUri.Scheme == "https" ? "wss" : "ws", Path = "/realtime" }.Uri;
        await socket.ConnectAsync(ws, CancellationToken.None);
        return new Client(socket, stats);
    }

    public static ByteString NewId() => ByteString.CopyFrom(Guid.NewGuid().ToByteArray(bigEndian: true));

    public async Task<Response> RequestAsync(ClientMessage message)
    {
        var completion = new TaskCompletionSource<Response>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            message.RequestId = ++_nextRequest;
            _pending[message.RequestId] = completion;
        }
        await _sendLock.WaitAsync();
        try
        {
            await _socket.SendAsync(message.ToByteArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public async Task<Response> RequestWithRetryAsync(ClientMessage message)
    {
        for (int attempt = 0; ; attempt++)
        {
            var response = await RequestAsync(message.Clone());
            if (response.Code != ResultCode.Busy || attempt >= 50)
            {
                return response;
            }
            Stats.Busy();
            await Task.Delay(TimeSpan.FromMilliseconds(response.RetryAfterMs > 0 ? response.RetryAfterMs : 50));
        }
    }

    public async Task<RoomState> WaitForAsync(Func<RoomState, bool> predicate, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                if (_latest is { } latest && predicate(latest))
                {
                    return latest;
                }
                changed = _changed.Task;
            }
            if (IsClosed)
            {
                throw new OperationCanceledException("连接已关闭。");
            }
            await changed.WaitAsync(timeout.Token);
        }
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                int total = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(total), CancellationToken.None);
                    total += result.Count;
                }
                while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Stats.Closed();
                    return;
                }
                var message = ServerMessage.Parser.ParseFrom(buffer, 0, total);
                if (message.BodyCase == ServerMessage.BodyOneofCase.Response)
                {
                    TaskCompletionSource<Response>? completion;
                    lock (_lock)
                    {
                        _pending.Remove(message.Response.RequestId, out completion);
                    }
                    completion?.TrySetResult(message.Response);
                }
                else if (message.BodyCase == ServerMessage.BodyOneofCase.State)
                {
                    Stats.State(total);
                    TaskCompletionSource previous;
                    lock (_lock)
                    {
                        _latest = message.State;
                        previous = _changed;
                        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    previous.TrySetResult();
                }
                else if (message.BodyCase == ServerMessage.BodyOneofCase.Closing)
                {
                    Console.WriteLine($"  服务端关闭连接：{message.Closing.Reason}");
                }
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            lock (_lock)
            {
                _changed.TrySetResult();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        _socket.Dispose();
    }
}
