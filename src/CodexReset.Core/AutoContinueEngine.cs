using System.Text.Json;
namespace CodexReset.Core;
public sealed class AutoContinueEngine {
 readonly HashSet<string> _handled=[]; public bool AlreadyHandled(string id)=>_handled.Contains(id);
 public async Task<bool> ContinueAsync(AppServerClient client,string threadId,string command,CancellationToken ct=default){
  if(_handled.Contains(threadId))return true;
  try{await client.RequestAsync("thread/resume",new{threadId},ct);var result=await client.RequestAsync("turn/start",new{threadId,input=new[]{new{type="text",text=command}}},ct);var turn=result.GetProperty("turn");var turnId=turn.GetProperty("id").GetString()!;_handled.Add(threadId);_=ReleaseWhenDone(client,threadId,turnId);return true;}catch{try{await client.RequestAsync("thread/unsubscribe",new{threadId},ct);}catch{}return false;}
 }
 static async Task ReleaseWhenDone(AppServerClient c,string threadId,string turnId){using var timeout=new CancellationTokenSource(TimeSpan.FromHours(6));try{while(!timeout.IsCancellationRequested){var x=await c.RequestAsync("thread/turns/list",new{threadId},timeout.Token);if(x.TryGetProperty("data",out var data)){foreach(var t in data.EnumerateArray())if(t.GetProperty("id").GetString()==turnId){var s=t.GetProperty("status").GetString();if(s is not ("inProgress" or "queued" or "pending")){await c.RequestAsync("thread/unsubscribe",new{threadId},timeout.Token);return;}}}await Task.Delay(TimeSpan.FromSeconds(10),timeout.Token);}}catch{}}
}
