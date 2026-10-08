using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace CodexReset.Core;
public sealed class ThreadHistory {
 readonly string _home;
 public ThreadHistory(string home)=>_home=home;
 public IReadOnlyList<CodexThread> UsageLimitedThreads(int limit=20) {
  var history=Path.Combine(_home,"thread_history_1.sqlite"); if(!File.Exists(history)) return [];
  using var db=OpenReadOnly(history); using var cmd=db.CreateCommand();
  cmd.CommandText=@"SELECT t.thread_id,t.error_json,t.started_at FROM thread_turns t JOIN (SELECT thread_id,MAX(turn_id) max_turn FROM thread_turns WHERE status='failed' AND error_json LIKE '%usageLimitExceeded%' GROUP BY thread_id) m ON t.thread_id=m.thread_id AND t.turn_id=m.max_turn ORDER BY t.turn_id DESC LIMIT $limit"; cmd.Parameters.AddWithValue("$limit",limit);
  var result=new List<CodexThread>(); using var rd=cmd.ExecuteReader();
  while(rd.Read()) { var id=rd.GetString(0); if(IsSubagent(id)) continue; var err=rd.IsDBNull(1)?"":rd.GetString(1); var at=rd.IsDBNull(2)?0:rd.GetInt64(2); result.Add(new(id,Title(id),Cwd(id),ExtractRecoveryHint(err),at)); }
  return result;
 }
 public IReadOnlyList<CodexThread> AllThreads(int limit=1000) {
  var state=Path.Combine(_home,"state_5.sqlite"); if(!File.Exists(state)) return [];
  using var db=OpenReadOnly(state);
  var columns=new HashSet<string>();
  using(var schema=db.CreateCommand()) { schema.CommandText="PRAGMA table_info(threads)";using var reader=schema.ExecuteReader();while(reader.Read())columns.Add(reader.GetString(1)); }
  var title=columns.Contains("name")?"name":"title";
  var activity=columns.Contains("recency_at")?"COALESCE(NULLIF(recency_at,0),updated_at)":"updated_at";
  var projectId=columns.Contains("project_id")?"project_id":"NULL";
  var projects=ReadProjects(db);
  using var cmd=db.CreateCommand();
  cmd.CommandText=$"SELECT id,{title},cwd,{activity} AS activity_at,{projectId} FROM threads WHERE archived=0 AND source NOT LIKE '{{\"subagent\"%' ORDER BY activity_at DESC LIMIT $limit";
  cmd.Parameters.AddWithValue("$limit",limit);
  var result=new List<CodexThread>();using var rd=cmd.ExecuteReader();
  while(rd.Read()) {
   var id=rd.GetString(0);var name=rd.IsDBNull(1)?null:rd.GetString(1);var cwd=rd.IsDBNull(2)?"":rd.GetString(2);var at=rd.IsDBNull(3)?0:rd.GetInt64(3);var project=rd.IsDBNull(4)?null:rd.GetString(4);
   result.Add(new(id,string.IsNullOrWhiteSpace(name)?"未命名对话":name.Trim(),cwd,null,at){ProjectName=ResolveProjectName(cwd,project,projects)});
  }
  return result;
 }
 static IReadOnlyList<(string Id,string Name,string Root)> ReadProjects(SqliteConnection db) {
  using var cmd=db.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('projects','project_roots')";
  if(Convert.ToInt32(cmd.ExecuteScalar())!=2)return [];
  cmd.CommandText="SELECT p.id,p.name,r.path FROM projects p LEFT JOIN project_roots r ON r.project_id=p.id";
  var projects=new List<(string Id,string Name,string Root)>();using var reader=cmd.ExecuteReader();
  while(reader.Read())projects.Add((reader.GetString(0),reader.GetString(1),reader.IsDBNull(2)?"":NormalizePath(reader.GetString(2))));
  return projects.OrderByDescending(x=>x.Root.Length).ToArray();
 }
 static string ResolveProjectName(string cwd,string? projectId,IReadOnlyList<(string Id,string Name,string Root)> projects) {
  if(projectId is not null)foreach(var project in projects)if(project.Id==projectId)return project.Name;
  var path=NormalizePath(cwd);
  foreach(var project in projects)if(project.Root.Length>0&&(path.Equals(project.Root,StringComparison.OrdinalIgnoreCase)||path.StartsWith(project.Root+"/",StringComparison.OrdinalIgnoreCase)))return project.Name;
  return string.IsNullOrWhiteSpace(path)?"未关联项目":path[(path.LastIndexOf('/')+1)..];
 }
 static string NormalizePath(string path) {
  path=path.Replace('\\','/').TrimEnd('/');
  return path.StartsWith("//?/UNC/",StringComparison.OrdinalIgnoreCase)?"//"+path[8..]:path.StartsWith("//?/")?path[4..]:path;
 }
 static SqliteConnection OpenReadOnly(string p){var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=p,Mode=SqliteOpenMode.ReadOnly}.ToString());c.Open();return c;}
 bool IsSubagent(string id)=>Scalar(Path.Combine(_home,"state_5.sqlite"),"SELECT source FROM threads WHERE id=$id",id)?.TrimStart().StartsWith("{\"subagent\"")==true;
 string Title(string id)=>DisplayTitle(Scalar(Path.Combine(_home,"state_5.sqlite"),"SELECT title FROM threads WHERE id=$id",id),FallbackTitle(id));
 string Cwd(string id)=>Scalar(Path.Combine(_home,"state_5.sqlite"),"SELECT cwd FROM threads WHERE id=$id",id)??"";
 string? FallbackTitle(string id) {
  var p=Path.Combine(_home,"thread_history_1.sqlite"); if(!File.Exists(p)) return null; var json=Scalar(p,"SELECT item_json FROM thread_items WHERE thread_id=$id AND item_type='userMessage' ORDER BY rollout_ordinal ASC LIMIT 1",id); if(json is null)return null;
  try { using var d=JsonDocument.Parse(json); var text=d.RootElement.GetProperty("content")[0].GetProperty("text").GetString()?.Replace('\n',' ').Trim(); return string.IsNullOrEmpty(text)?null:string.Concat(text.Take(40)); } catch{return null;}
 }
 static string? Scalar(string p,string sql,string id){if(!File.Exists(p))return null;using var db=OpenReadOnly(p);using var c=db.CreateCommand();c.CommandText=sql;c.Parameters.AddWithValue("$id",id);return c.ExecuteScalar()?.ToString();}
 public static string DisplayTitle(string? stateTitle,string? fallback)=>!string.IsNullOrEmpty(stateTitle)&&stateTitle.Length>=3?stateTitle:!string.IsNullOrEmpty(fallback)?fallback:stateTitle??"Unnamed conversation";
 public static string? ExtractRecoveryHint(string errorJson){try{using var d=JsonDocument.Parse(errorJson);var m=d.RootElement.GetProperty("message").GetString();const string marker="try again at ";var i=m?.IndexOf(marker,StringComparison.OrdinalIgnoreCase)??-1;return i<0?null:m![(i+marker.Length)..];}catch{return null;}}
}
