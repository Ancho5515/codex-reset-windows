using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexReset.Core.Tests;

// 只模拟本机 RPC 后台；监控、续作和 WebSocket 客户端均使用真实生产代码。
internal sealed class AppServerStub : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    private WebSocket? _socket;
    public int AccountRequests { get; private set; }
    public bool FailAccountRead { get; set; }
    public object AccountResponse { get; set; } = new
    {
        account = new { type = "chatgpt", email = "synthetic@invalid", planType = "pro" }, requiresOpenaiAuth = true
    };
    public bool FailRateLimitRead { get; set; }
    public TimeSpan RateLimitReadDelay { get; set; }
    public TaskCompletionSource RateLimitReadReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public object RateLimitsResponse { get; set; } = new
    {
        rateLimits = new { primary = new { usedPercent = 0 }, secondary = new { usedPercent = 0 }, rateLimitReachedType = (string?)null }
    };
    public HashSet<string> FailResumeOnce { get; } = [];
    public TimeSpan ResumeDelay { get; set; }
    public TaskCompletionSource ResumeReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public HashSet<string> FailStartOnce { get; } = [];
    public HashSet<string> ActiveWriterThreads { get; } = [];
    public HashSet<string> FailQueueOnce { get; } = [];
    public HashSet<string> FailQueueListOnce { get; } = [];
    public int QueuePageSize { get; set; } = 50;
    public TimeSpan QueueDelay { get; set; }
    public TaskCompletionSource QueueReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Dictionary<string, List<string>> QueuedMessages { get; } = [];
    public Dictionary<string, List<(string Id, JsonElement Input)>> QueueEntries { get; } = [];
    public TimeSpan StartDelay { get; set; }
    public Dictionary<string, TimeSpan> StartDelays { get; } = [];
    public TaskCompletionSource StartReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Dictionary<string, List<string>> Messages { get; } = [];
    public Uri Address { get; }

    public AppServerStub()
    {
        _listener.Start();
        Address = new Uri($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _run = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            string? key = null;
            while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    key = line.Split(':', 2)[1].Trim();
            if (key is null) throw new InvalidOperationException("测试连接缺少 WebSocket 握手。");
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _stop.Token);
            _socket = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
            var buffer = new byte[4096];
            while (!_stop.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(buffer, _stop.Token);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", _stop.Token);
                        return;
                    }
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                using var document = JsonDocument.Parse(message.ToArray());
                var request = document.RootElement;
                if (!request.TryGetProperty("id", out var id)) continue;
                var method = request.GetProperty("method").GetString();
                var parameters = request.TryGetProperty("params", out var p) ? p : default;
                var threadId = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("threadId", out var t) ? t.GetString()! : "";
                object result;
                if (method == "thread/resume")
                {
                    ResumeReceived.TrySetResult();
                    await Task.Delay(ResumeDelay, _stop.Token);
                }
                if (method == "thread/resume" && ActiveWriterThreads.Contains(threadId))
                {
                    await ReplyAsync(new { id = id.GetInt32(), error = new { code = -32600, message = $"thread {threadId} already has an active writer" } });
                    continue;
                }
                if (method == "thread/resume" && FailResumeOnce.Remove(threadId) || method == "turn/start" && FailStartOnce.Remove(threadId))
                {
                    await ReplyAsync(new { id = id.GetInt32(), error = new { code = -32000, message = "测试后台暂时不可用" } });
                    continue;
                }
                switch (method)
                {
                    case "account/read" when !FailAccountRead:
                        AccountRequests++;
                        result = AccountResponse;
                        break;
                    case "account/rateLimits/read" when !FailRateLimitRead:
                        AccountRequests++;
                        result = RateLimitsResponse;
                        RateLimitReadReceived.TrySetResult();
                        await Task.Delay(RateLimitReadDelay, _stop.Token);
                        break;
                    case string name when name.StartsWith("account/", StringComparison.Ordinal):
                        AccountRequests++;
                        await ReplyAsync(new { id = id.GetInt32(), error = new { code = -32601, message = "路由后台不提供账号接口" } });
                        continue;
                    case "thread/resume":
                        result = new { thread = new { id = threadId, turns = Array.Empty<object>() } };
                        break;
                    case "turn/start":
                        if (!Messages.TryGetValue(threadId, out var messages)) Messages[threadId] = messages = [];
                        messages.Add(parameters.GetProperty("input")[0].GetProperty("text").GetString()!);
                        StartReceived.TrySetResult();
                        await Task.Delay(StartDelays.GetValueOrDefault(threadId, StartDelay), _stop.Token);
                        result = new { turn = new { id = $"{threadId}-{messages.Count}", status = "completed" } };
                        break;
                    case "thread/queue/add":
                        if (FailQueueOnce.Remove(threadId))
                        {
                            await ReplyAsync(new { id = id.GetInt32(), error = new { code = -32000, message = "测试队列暂时不可用" } });
                            continue;
                        }
                        var messageId = parameters.GetProperty("clientUserMessageId").GetString()!;
                        if (!Guid.TryParse(messageId, out _)) throw new InvalidOperationException("队列请求缺少稳定消息 ID。");
                        var input = parameters.GetProperty("input").Clone();
                        if (!QueuedMessages.TryGetValue(threadId, out var queued)) QueuedMessages[threadId] = queued = [];
                        queued.Add(input[0].GetProperty("text").GetString()!);
                        if (!QueueEntries.TryGetValue(threadId, out var entries)) QueueEntries[threadId] = entries = [];
                        entries.Add((messageId, input));
                        QueueReceived.TrySetResult();
                        await Task.Delay(QueueDelay, _stop.Token);
                        result = new { queuedSubmission = new { id = messageId, clientUserMessageId = messageId, input } };
                        break;
                    case "thread/queue/list":
                        if (FailQueueListOnce.Remove(threadId))
                        {
                            await ReplyAsync(new { id = id.GetInt32(), error = new { code = -32000, message = "测试队列读取暂时不可用" } });
                            continue;
                        }
                        var offset = parameters.TryGetProperty("cursor", out var cursor) && cursor.ValueKind == JsonValueKind.String
                            ? int.Parse(cursor.GetString()!) : 0;
                        var pending = QueueEntries.GetValueOrDefault(threadId, []);
                        var page = pending.Skip(offset).Take(QueuePageSize)
                            .Select(entry => new { id = entry.Id, clientUserMessageId = entry.Id, input = entry.Input }).ToArray();
                        result = new { data = page, nextCursor = offset + page.Length < pending.Count ? (offset + page.Length).ToString() : null };
                        break;
                    case "thread/turns/list":
                        result = new { data = Messages.TryGetValue(threadId, out var turns) ? turns.Select((_, i) => new { id = $"{threadId}-{i + 1}", status = "completed" }).ToArray() : [], nextCursor = (string?)null };
                        break;
                    default:
                        result = new { };
                        break;
                }
                await ReplyAsync(new { id = id.GetInt32(), result });
            }
        }
        catch (WebSocketException error) when (error.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely) { }
        catch (Exception) when (_stop.IsCancellationRequested) { }
    }

    private Task ReplyAsync(object response) => _socket!.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response)), WebSocketMessageType.Text, true, _stop.Token);

    public async ValueTask DisposeAsync()
    {
        StopConnection();
        await _run;
        _socket?.Dispose();
        _stop.Dispose();
    }

    public void StopConnection()
    {
        _stop.Cancel();
        _socket?.Abort();
        _listener.Stop();
    }
}
