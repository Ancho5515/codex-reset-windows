using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
namespace CodexReset.Core;
public sealed class AppServerClient : IAsyncDisposable {
 readonly ClientWebSocket _ws=new(); readonly ConcurrentDictionary<int,TaskCompletionSource<JsonElement>> _pending=new(); int _nextId; CancellationTokenSource? _receiveCts;
 public async Task ConnectAsync(Uri uri,CancellationToken ct=default){await _ws.ConnectAsync(uri,ct);_receiveCts=CancellationTokenSource.CreateLinkedTokenSource(ct);_=ReceiveLoop(_receiveCts.Token);}
 public async Task InitializeAsync(CancellationToken ct=default){await RequestAsync("initialize",new {clientInfo=new{name="CodexReset",version="1.0.0"},capabilities=new{experimentalApi=true}},ct);await NotifyAsync("initialized",new{},ct);}
 public async Task<JsonElement> RequestAsync(string method,object? p=null,CancellationToken ct=default){var id=Interlocked.Increment(ref _nextId);var tcs=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);_pending[id]=tcs;await Send(RpcMessage.Request(id,method,p),ct);using var reg=ct.Register(()=>tcs.TrySetCanceled(ct));return await tcs.Task;}
 public Task NotifyAsync(string method,object? p=null,CancellationToken ct=default)=>Send(RpcMessage.Notification(method,p),ct);
 async Task Send(string text,CancellationToken ct){var b=Encoding.UTF8.GetBytes(text);await _ws.SendAsync(b,WebSocketMessageType.Text,true,ct);}
 async Task ReceiveLoop(CancellationToken ct){var b=new byte[65536];try{while(_ws.State==WebSocketState.Open&&!ct.IsCancellationRequested){using var ms=new MemoryStream();WebSocketReceiveResult x;do{x=await _ws.ReceiveAsync(b,ct);if(x.MessageType==WebSocketMessageType.Close)return;ms.Write(b,0,x.Count);}while(!x.EndOfMessage);using var d=JsonDocument.Parse(ms.ToArray());var root=d.RootElement;if(!root.TryGetProperty("id",out var idEl)||!idEl.TryGetInt32(out var id))continue;if(!_pending.TryRemove(id,out var tcs))continue;if(root.TryGetProperty("error",out var err)){tcs.TrySetException(new InvalidOperationException(err.ToString()));continue;}tcs.TrySetResult(root.TryGetProperty("result",out var result)?result.Clone():default);}}catch(Exception e){foreach(var p in _pending.Values)p.TrySetException(e);_pending.Clear();}}
 public async ValueTask DisposeAsync(){_receiveCts?.Cancel();if(_ws.State==WebSocketState.Open)await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure,"bye",CancellationToken.None);_ws.Dispose();_receiveCts?.Dispose();}
}
