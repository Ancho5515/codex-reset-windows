using CodexReset.Core;
using System.Diagnostics;
using System.Text.Json;

ApplicationConfiguration.Initialize();
var home=CodexEnvironment.ResolveHome();
var settings=SettingsStore.Load();
using var menu=new ContextMenuStrip();
var status=new ToolStripMenuItem("CodexReset: starting…"){Enabled=false};
var auto=new ToolStripMenuItem("Auto continue"){Checked=settings.AutoContinue,CheckOnClick=true};
var threads=new ToolStripMenuItem("Conversations");
var refresh=new ToolStripMenuItem("Refresh");
var openCodex=new ToolStripMenuItem("Open Codex");
var exit=new ToolStripMenuItem("Exit");
menu.Items.AddRange([status,auto,threads,refresh,openCodex,new ToolStripSeparator(),exit]);
using var tray=new NotifyIcon{Text="CodexReset",Icon=SystemIcons.Application,Visible=true,ContextMenuStrip=menu};
AppServerManager? manager=null; AppServerClient? client=null; var monitor=new AutoContinueMonitor();

void Save(){settings=settings with{AutoContinue=auto.Checked};SettingsStore.Save(settings);}
void LoadThreads(){
 threads.DropDownItems.Clear(); var paused=new HashSet<string>(new ThreadHistory(home).UsageLimitedThreads().Select(x=>x.ThreadId));
 foreach(var t in new ThreadHistory(home).AllThreads(300)){
  var item=new ToolStripMenuItem((paused.Contains(t.ThreadId)?"⏸ ":"")+t.Title){Checked=settings.SelectedThreadIds.Contains(t.ThreadId),CheckOnClick=true,Tag=t};
  item.CheckedChanged+=(_,__)=>{if(item.Checked)settings.SelectedThreadIds.Add(t.ThreadId);else settings.SelectedThreadIds.Remove(t.ThreadId);Save();};
  item.DoubleClick+=(_,__)=>Process.Start(new ProcessStartInfo($"codex://threads/{t.ThreadId}"){UseShellExecute=true});
  threads.DropDownItems.Add(item);
 }
}
async Task Connect(){
 try{manager=new AppServerManager(home);var x=await manager.StartOwnServerAsync();client=x.Client;status.Text="CodexReset: connected";LoadThreads();}
 catch(Exception e){status.Text="CodexReset: "+e.Message.Split('\n')[0];}
}
async Task Tick(){
 if(client is null)return;
 try{
  var root=await client.RequestAsync("account/rateLimits/read");var rl=root.GetProperty("rateLimits");var p=rl.GetProperty("primary");var used=p.GetProperty("usedPercent").GetInt32();var reset=p.TryGetProperty("resetsAt",out var ra)?DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64()).LocalDateTime:(DateTime?)null;
  status.Text=reset is null?$"CodexReset: 5h {used}%":$"CodexReset: 5h {used}% · reset {reset:t}";
  var n=await monitor.TickAsync(client,settings);if(n>0)tray.ShowBalloonTip(4000,"CodexReset",$"Continued {n} conversation(s).",ToolTipIcon.Info);
 }catch(Exception e){status.Text="CodexReset: "+e.Message.Split('\n')[0];}
}
auto.CheckedChanged+=(_,__)=>Save();refresh.Click+=(_,__)=>LoadThreads();openCodex.Click+=(_,__)=>Process.Start(new ProcessStartInfo("codex://"){UseShellExecute=true});exit.Click+=(_,__)=>Application.Exit();
var timer=new System.Windows.Forms.Timer{Interval=30000};timer.Tick+=async(_,__)=>await Tick();timer.Start();
await Connect();await Tick();Application.Run();timer.Stop();if(client is not null)await client.DisposeAsync();if(manager is not null)await manager.DisposeAsync();
