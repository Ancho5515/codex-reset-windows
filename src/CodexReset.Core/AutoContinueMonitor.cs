namespace CodexReset.Core;
public sealed class AutoContinueMonitor {
 readonly RecoveryDetector _detector=new(); readonly AutoContinueEngine _engine=new();
 public async Task<int> TickAsync(AppServerClient client,AppSettings settings,CancellationToken ct=default){
  var root=await client.RequestAsync("account/rateLimits/read",null,ct);var rl=root.GetProperty("rateLimits");var primary=rl.TryGetProperty("primary",out var p)&&p.ValueKind!=System.Text.Json.JsonValueKind.Null?p:default;
  var used=primary.ValueKind==System.Text.Json.JsonValueKind.Object&&primary.TryGetProperty("usedPercent",out var u)?u.GetInt32():0;
  var reached=rl.TryGetProperty("rateLimitReachedType",out var rt)&&rt.ValueKind==System.Text.Json.JsonValueKind.String?rt.GetString():null;
  if(!_detector.Observe(new(used,reached))||!settings.AutoContinue)return 0;
  var count=0;foreach(var id in settings.SelectedThreadIds)if(await _engine.ContinueAsync(client,id,settings.Command,ct))count++;return count;
 }
}
