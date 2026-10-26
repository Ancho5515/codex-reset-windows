using System.Text.Json;
namespace CodexReset.Core;
public sealed class AutoContinueEngine(Action<string>? wakeQueuedThread = null)
{
 readonly HashSet<string> _handled=[];
 readonly Dictionary<string,(Task<JsonElement> Task,int Recovery,bool Queued)> _starting=[];
 readonly Action<string> _wakeQueuedThread=wakeQueuedThread??WindowsGuiFallback.WakeQueuedThread;
 int _recovery;
 public string? LastError { get; private set; }
 public string? LastDeliveryMessage { get; private set; }
 public bool AlreadyHandled(string id)=>_handled.Contains(id);
 public void BeginRecovery(){_recovery++;_handled.Clear();}
 public void BeginManualContinuation()=>_handled.Clear();

 public async Task<bool> ContinueAsync(AppServerClient client,string threadId,string command,CancellationToken ct=default)
 {
  LastError=null;
  LastDeliveryMessage=null;
  if(_handled.Contains(threadId))return true;
  using var request=CancellationTokenSource.CreateLinkedTokenSource(ct);
  request.CancelAfter(TimeSpan.FromSeconds(30));
  var stage="恢复对话";
  try
  {
   while(true)
   {
    if(!_starting.TryGetValue(threadId,out var start))
    {
     stage="恢复对话";
     var queued=false;
     try{await client.RequestAsync("thread/resume",new{threadId,excludeTurns=true},request.Token);}
     catch(AppServerRpcException error) when(error.Message.Contains("already has an active writer",StringComparison.OrdinalIgnoreCase))
     {
      // 已由桌面端加载的对话交给其持久队列，不能在另一个后台恢复同一写入者。
      queued=true;
     }
     stage=queued?"提交桌面队列":"发送续作指令";
     object parameters=queued
      ?new{threadId,clientUserMessageId=Guid.NewGuid().ToString(),input=new[]{new{type="text",text=command}}}
      :new{threadId,input=new[]{new{type="text",text=command}}};
     // 等待超时只停止等待，保留原请求及回执，避免后台已接受指令时重复发送。
     start=(client.RequestAsync(queued?"thread/queue/add":"turn/start",parameters),_recovery,queued);
     _starting[threadId]=start;
    }
    else stage="确认续作指令";
    var result=await start.Task.WaitAsync(request.Token);
    if(start.Queued)
    {
     var queueId=result.GetProperty("queuedSubmission").GetProperty("id").GetString();
     if(string.IsNullOrWhiteSpace(queueId))throw new JsonException("桌面队列未返回消息回执。");
     if(start.Recovery!=_recovery)
     {
      stage="核对桌面队列";
      var pending=false;
      string? cursor=null;
      do
      {
       var page=await client.RequestAsync("thread/queue/list",new{threadId,cursor,limit=100},request.Token);
       pending=page.GetProperty("data").EnumerateArray().Any(item=>item.GetProperty("id").GetString()==queueId);
       cursor=page.TryGetProperty("nextCursor",out var next)&&next.ValueKind==JsonValueKind.String?next.GetString():null;
      }while(!pending&&cursor is not null);
      if(!pending){_starting.Remove(threadId);continue;}
     }
     stage="唤醒桌面对话";
     // 入队回执保留到唤醒成功；唤醒失败时只重试唤醒，不能再次提交消息。
     _wakeQueuedThread(threadId);
    }
    else
    {
     var turnId=result.GetProperty("turn").GetProperty("id").GetString()!;
     _=ReleaseWhenDone(client,threadId,turnId);
    }
    _starting.Remove(threadId);
    // 旧轮次的迟到回执不能替代本次续作；尚未消费的队列消息可直接用于本次唤醒。
    if(!start.Queued&&start.Recovery!=_recovery)continue;
    _handled.Add(threadId);
    LastDeliveryMessage=start.Queued?"续作指令已加入桌面队列，已请求唤醒原对话。":"已发送续作指令。";
    return true;
   }
  }
  catch(AppServerRpcException error)
  {
   // 后台明确拒绝的请求可以重试；结果未知的请求保留，不能盲目重发。
   if(stage!="核对桌面队列")_starting.Remove(threadId);
   LastError=$"{stage}失败：{error.Message}";
   try{await client.RequestAsync("thread/unsubscribe",new{threadId},request.Token);}catch{}
   return false;
  }
  catch(OperationCanceledException)
  {
   LastError=_starting.ContainsKey(threadId)?"等待发送结果超时，保留原请求继续核对":"恢复对话超时或已取消";
   return false;
  }
  catch(Exception error)
  {
   LastError=stage=="唤醒桌面对话"?$"指令已加入桌面队列，唤醒失败，下次只重试唤醒：{error.Message}"
    :_starting.ContainsKey(threadId)?$"发送结果未知，请在 Codex 中核对：{error.Message}":$"{stage}失败：{error.Message}";
   return false;
  }
 }
 static async Task ReleaseWhenDone(AppServerClient c,string threadId,string turnId){using var timeout=new CancellationTokenSource(TimeSpan.FromHours(6));try{while(!timeout.IsCancellationRequested){var x=await c.RequestAsync("thread/turns/list",new{threadId},timeout.Token);if(x.TryGetProperty("data",out var data)){foreach(var t in data.EnumerateArray())if(t.GetProperty("id").GetString()==turnId){var s=t.GetProperty("status").GetString();if(s is not ("inProgress" or "queued" or "pending")){await c.RequestAsync("thread/unsubscribe",new{threadId},timeout.Token);return;}}}await Task.Delay(TimeSpan.FromSeconds(10),timeout.Token);}}catch{}}
}
