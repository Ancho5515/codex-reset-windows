using System.Text.Json;
namespace CodexReset.Core;
public sealed record AppSettings(bool AutoContinue,string Command,HashSet<string> SelectedThreadIds) {
 public static AppSettings Default=>new(true,"继续",[]);
}
public static class SettingsStore {
 static readonly JsonSerializerOptions O=new(){WriteIndented=true};
 public static string Serialize(AppSettings s)=>JsonSerializer.Serialize(s,O);
 public static AppSettings Deserialize(string s)=>JsonSerializer.Deserialize<AppSettings>(s,O)??AppSettings.Default;
 public static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CodexReset","settings.json");
 public static AppSettings Load(string? path=null){path??=DefaultPath;try{return File.Exists(path)?Deserialize(File.ReadAllText(path)):AppSettings.Default;}catch{return AppSettings.Default;}}
 public static void Save(AppSettings s,string? path=null){path??=DefaultPath;Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,Serialize(s));}
}
