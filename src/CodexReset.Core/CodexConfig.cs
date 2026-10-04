namespace CodexReset.Core;
public sealed record CodexConfig(bool RemoteControlEnabled) {
 public static CodexConfig Parse(string content) {
  var inFeatures=false; var enabled=false;
  foreach(var raw in content.Replace("\r","").Split('\n')) {
   var line=raw.Trim();
   if(line.StartsWith('[')&&line.EndsWith(']')) { inFeatures=line=="[features]"; continue; }
   if(inFeatures&&line.StartsWith("remote_control",StringComparison.OrdinalIgnoreCase))
    enabled=line.Split('=',2).ElementAtOrDefault(1)?.Trim().Equals("true",StringComparison.OrdinalIgnoreCase)==true;
  }
  return new(enabled);
 }
 public static CodexConfig Load(string home) {
  var p=Path.Combine(home,"config.toml"); return File.Exists(p)?Parse(File.ReadAllText(p)):new(false);
 }
 public static string SetRemoteControlText(string content,bool enabled) {
  var lines=content.Replace("\r","").Split('\n').ToList(); var start=lines.FindIndex(x=>x.Trim()=="[features]");
  var value=$"remote_control = {(enabled?"true":"false")}";
  if(start<0) { while(lines.Count>0&&string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count-1); if(lines.Count>0) lines.Add(""); lines.Add("[features]"); lines.Add(value); }
  else {
   var end=lines.FindIndex(start+1,x=>{var t=x.Trim(); return t.StartsWith('[')&&t.EndsWith(']');}); if(end<0) end=lines.Count;
   var idx=-1; for(var i=start+1;i<end;i++) if(lines[i].TrimStart().StartsWith("remote_control",StringComparison.OrdinalIgnoreCase)){idx=i;break;}
   if(idx>=0) lines[idx]=value; else lines.Insert(end,value);
  }
  return string.Join(Environment.NewLine,lines);
 }
 public static void SetRemoteControl(string home,bool enabled) {
  Directory.CreateDirectory(home); var p=Path.Combine(home,"config.toml"); var old=File.Exists(p)?File.ReadAllText(p):""; File.WriteAllText(p,SetRemoteControlText(old,enabled));
 }
}
