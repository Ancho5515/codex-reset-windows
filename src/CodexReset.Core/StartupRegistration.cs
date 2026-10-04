using Microsoft.Win32;
namespace CodexReset.Core;
public static class StartupRegistration {
 const string Key=@"Software\Microsoft\Windows\CurrentVersion\Run"; const string Name="CodexReset";
 public static bool IsEnabled(){using var k=Registry.CurrentUser.OpenSubKey(Key);return k?.GetValue(Name) is string;}
 public static void Set(bool enabled,string executablePath){using var k=Registry.CurrentUser.CreateSubKey(Key);if(enabled)k.SetValue(Name,$"\"{executablePath}\"");else k.DeleteValue(Name,false);}
}
