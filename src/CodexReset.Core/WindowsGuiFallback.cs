using System.Diagnostics;
using System.Runtime.InteropServices;
namespace CodexReset.Core;
public static class WindowsGuiFallback {
 [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
 public static async Task<bool> OpenThreadAsync(string threadId,CancellationToken ct=default){
  try{Process.Start(new ProcessStartInfo($"codex://threads/{Uri.EscapeDataString(threadId)}"){UseShellExecute=true});await Task.Delay(2500,ct);var p=Process.GetProcesses().FirstOrDefault(x=>x.ProcessName.Contains("Codex",StringComparison.OrdinalIgnoreCase)||x.ProcessName.Contains("ChatGPT",StringComparison.OrdinalIgnoreCase));return p is not null&&p.MainWindowHandle!=IntPtr.Zero&&SetForegroundWindow(p.MainWindowHandle);}catch{return false;}
 }
}
