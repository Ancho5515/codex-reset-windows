using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
namespace CodexReset.Core;
public sealed class AppServerManager : IAsyncDisposable {
 Process? _process; public string CodexHome{get;} public AppServerManager(string home)=>CodexHome=home;
 public static string? FindCodexBinary(){var env=Environment.GetEnvironmentVariable("CODEX_CLI_PATH");var candidates=new[]{env,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"npm","codex.cmd"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","codex","codex.exe")};return candidates.FirstOrDefault(p=>!string.IsNullOrWhiteSpace(p)&&File.Exists(p));}
 public async Task<(AppServerClient Client,Uri Uri)> StartOwnServerAsync(CancellationToken ct=default){var binary=FindCodexBinary()??"codex";var port=FreePort();var psi=new ProcessStartInfo{FileName=binary,Arguments=$"app-server --listen ws://127.0.0.1:{port}",UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};psi.Environment["CODEX_HOME"]=CodexHome;_process=Process.Start(psi)??throw new InvalidOperationException("Failed to start codex app-server");var uri=new Uri($"ws://127.0.0.1:{port}");Exception? last=null;for(var i=0;i<50;i++){ct.ThrowIfCancellationRequested();try{var c=new AppServerClient();await c.ConnectAsync(uri,ct);await c.InitializeAsync(ct);return(c,uri);}catch(Exception e){last=e;await Task.Delay(200,ct);}}throw new InvalidOperationException("Codex app-server did not become ready",last);}
 static int FreePort(){var l=new TcpListener(IPAddress.Loopback,0);l.Start();var p=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();return p;}
 public ValueTask DisposeAsync(){if(_process is {HasExited:false})_process.Kill(true);_process?.Dispose();return ValueTask.CompletedTask;}
}
