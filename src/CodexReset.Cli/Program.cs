using CodexReset.Core;
using System.Text.Json;

var home=CodexEnvironment.ResolveHome();
var command=args.FirstOrDefault()?.ToLowerInvariant()??"doctor";
if(command=="doctor"){
 Console.WriteLine($"CODEX_HOME: {home}");
 Console.WriteLine($"state_5.sqlite: {File.Exists(Path.Combine(home,"state_5.sqlite"))}");
 Console.WriteLine($"thread_history_1.sqlite: {File.Exists(Path.Combine(home,"thread_history_1.sqlite"))}");
 Console.WriteLine($"remote_control config: {CodexConfig.Load(home).RemoteControlEnabled}");
 Console.WriteLine($"codex CLI: {AppServerManager.FindCodexBinary()??"PATH lookup at runtime"}");
 return;
}
var history=new ThreadHistory(home);
if(command=="threads"){foreach(var t in history.AllThreads())Console.WriteLine($"{t.ThreadId}\t{t.Title}\t{t.Cwd}");return;}
if(command=="paused"){foreach(var t in history.UsageLimitedThreads())Console.WriteLine($"{t.ThreadId}\t{t.Title}\t{t.RecoveryHint}");return;}
if(command=="status"){
 await using var mgr=new AppServerManager(home);var (client,_)=await mgr.StartOwnServerAsync();await using(client){var x=await client.RequestAsync("account/rateLimits/read");Console.WriteLine(JsonSerializer.Serialize(x,new JsonSerializerOptions{WriteIndented=true}));}return;
}
if(command=="continue"){
 if(args.Length<2){Console.Error.WriteLine("Usage: codex-reset continue <thread-id> [command]");Environment.ExitCode=2;return;}
 var text=args.Length>2?string.Join(' ',args.Skip(2)):"继续";await using var mgr=new AppServerManager(home);var(client,_)=await mgr.StartOwnServerAsync();await using(client){var ok=await new AutoContinueEngine().ContinueAsync(client,args[1],text);Environment.ExitCode=ok?0:1;Console.WriteLine(ok?"Continue sent.":"Continue failed.");}return;
}
Console.Error.WriteLine("Commands: doctor, status, threads, paused, continue <thread-id> [command]");
Environment.ExitCode=2;
