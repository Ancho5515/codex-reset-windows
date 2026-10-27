using Microsoft.Data.Sqlite;
namespace CodexReset.Core;
public sealed class ThreadHistory {
 readonly string _home;
 public ThreadHistory(string home)=>_home=home;
 public IReadOnlyList<CodexThread> PausedThreads(int limit=1000) {
  var history=Path.Combine(_home,"thread_history_1.sqlite"); if(!File.Exists(history)) return [];
  var threads=AllThreads(limit).ToDictionary(thread=>thread.ThreadId);
  using var db=OpenReadOnly(history); using var cmd=db.CreateCommand();
  cmd.CommandText=@"SELECT t.thread_id,t.turn_id,t.started_at FROM thread_turns t
   WHERE t.status='failed' AND t.error_json LIKE '%usageLimitExceeded%'
   AND NOT EXISTS (SELECT 1 FROM thread_turns newer WHERE newer.thread_id=t.thread_id AND newer.rollout_ordinal>t.rollout_ordinal)
   ORDER BY t.started_at DESC LIMIT $limit"; cmd.Parameters.AddWithValue("$limit",limit);
  var result=new List<CodexThread>(); using var rd=cmd.ExecuteReader();
  while(rd.Read()) if(threads.TryGetValue(rd.GetString(0),out var thread))
   result.Add(thread with { PausedTurnId=rd.GetString(1),Timestamp=rd.IsDBNull(2)?0:rd.GetInt64(2) });
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
}
