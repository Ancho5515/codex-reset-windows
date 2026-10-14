using System.Text.Json;
namespace CodexReset.Core;
public sealed class AutoContinueEngine
{
 readonly HashSet<string> _handled=[];
 readonly Dictionary<string,(Task<JsonElement> Task,int Recovery)> _starting=[];
 int _recovery;
 public string? LastError { get; private set; }
 public bool AlreadyHandled(string id)=>_handled.Contains(id);
 public void BeginRecovery(){_recovery++;_handled.Clear();}
 public void BeginManualContinuation()=>_handled.Clear();

 public async Task<bool> ContinueAsync(AppServerClient client,string threadId,string command,CancellationToken ct=default)
 {
  LastError=null;
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
     await client.RequestAsync("thread/resume",new{threadId,excludeTurns=true},request.Token);
     stage="发送续作指令";
     // 等待超时只停止等待，保留原请求及回执，避免后台已接受指令时重复发送。
     start=(client.RequestAsync("turn/start",new{threadId,input=new[]{new{type="text",text=command}}}),_recovery);
     _starting[threadId]=start;
    }
    else stage="确认续作指令";
    var result=await start.Task.WaitAsync(request.Token);
    var turnId=result.GetProperty("turn").GetProperty("id").GetString()!;
    _starting.Remove(threadId);
    _=ReleaseWhenDone(client,threadId,turnId);
    // 旧周期的迟到回执不能替代当前额度恢复后的续作。
    if(start.Recovery!=_recovery)continue;
    _handled.Add(threadId);
    return true;
   }
  }
  catch(AppServerRpcException error)
  {
   // 后台明确拒绝的请求可以重试；结果未知的请求保留，不能盲目重发。
   _starting.Remove(threadId);
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
   LastError=_starting.ContainsKey(threadId)?$"发送结果未知，请在 Codex 中核对：{error.Message}":$"{stage}失败：{error.Message}";
   return false;
  }
 }
 static async Task ReleaseWhenDone(AppServerClient c,string threadId,string turnId){using var timeout=new CancellationTokenSource(TimeSpan.FromHours(6));try{while(!timeout.IsCancellationRequested){var x=await c.RequestAsync("thread/turns/list",new{threadId},timeout.Token);if(x.TryGetProperty("data",out var data)){foreach(var t in data.EnumerateArray())if(t.GetProperty("id").GetString()==turnId){var s=t.GetProperty("status").GetString();if(s is not ("inProgress" or "queued" or "pending")){await c.RequestAsync("thread/unsubscribe",new{threadId},timeout.Token);return;}}}await Task.Delay(TimeSpan.FromSeconds(10),timeout.Token);}}catch{}}
}
