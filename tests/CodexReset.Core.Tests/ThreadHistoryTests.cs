using CodexReset.Core;
using Microsoft.Data.Sqlite;
namespace CodexReset.Core.Tests;
public class ThreadHistoryTests {
 [Fact] public void ExtractsRecoveryHint() {
  var json="{\"message\":\"You've hit your usage limit. try again at 1:17 PM\"}";
  Assert.Equal("1:17 PM",ThreadHistory.ExtractRecoveryHint(json));
 }
 [Fact] public void MeaninglessContinueTitleFallsBackToOriginalPrompt() {
  Assert.Equal("Implement Windows reset tool",ThreadHistory.DisplayTitle("继续","Implement Windows reset tool"));
 }

 [Fact] public void UsesDesktopNameAndConversationActivityTime() {
  var threads=ReadThreads("""
     CREATE TABLE threads (id TEXT PRIMARY KEY, title TEXT, name TEXT, cwd TEXT, updated_at INTEGER, updated_at_ms INTEGER, recency_at INTEGER, archived INTEGER, source TEXT);
     INSERT INTO threads VALUES ('older','第一句提示词','保存的对话标题','D:\Projects\Calibration',200,200000,100,0,'cli');
     INSERT INTO threads VALUES ('newer','另一个长提示词','新的对话标题','D:\Projects\Calibration',150,150000,120,0,'cli');
     INSERT INTO threads VALUES ('unnamed','不应当作标题的提示词',NULL,'D:\Projects\Notes',80,80000,0,0,'cli');
     """);
  Assert.Equal(new[]{"newer","older","unnamed"},threads.Select(x=>x.ThreadId));
  Assert.Equal("保存的对话标题",threads[1].Title);
  Assert.Equal(100,threads[1].Timestamp);
  Assert.Equal("未命名对话",threads[2].Title);
  Assert.Equal(80,threads[2].Timestamp);
 }

 [Fact] public void ResolvesSavedProjectNamesAndDirectoryNames() {
  var threads=ReadThreads("""
   CREATE TABLE threads (id TEXT PRIMARY KEY, title TEXT, name TEXT, cwd TEXT, updated_at INTEGER, recency_at INTEGER, project_id TEXT, archived INTEGER, source TEXT);
   CREATE TABLE projects (id TEXT PRIMARY KEY,name TEXT);
   CREATE TABLE project_roots (project_id TEXT,path TEXT);
   INSERT INTO projects VALUES ('calibration','LED 校准'),('tools','校准工具');
   INSERT INTO project_roots VALUES ('calibration','D:\Projects\Calibration'),('tools','D:\Projects\Calibration\tools');
   INSERT INTO threads VALUES ('root','提示词','根目录任务','\\?\D:\Projects\Calibration\',100,100,NULL,0,'cli');
   INSERT INTO threads VALUES ('child','提示词','子目录任务','d:/projects/calibration/tools/src',100,100,NULL,0,'cli');
   INSERT INTO threads VALUES ('assigned','提示词','已关联任务','D:\Other','100',100,'tools',0,'cli');
   INSERT INTO threads VALUES ('outside','提示词','其他任务','D:\Projects\CalibrationOther',100,100,NULL,0,'cli');
   INSERT INTO threads VALUES ('none','提示词','独立任务','',100,100,NULL,0,'cli');
   """).ToDictionary(x=>x.ThreadId);
  Assert.Equal("LED 校准",threads["root"].ProjectName);
  Assert.Equal("校准工具",threads["child"].ProjectName);
  Assert.Equal("校准工具",threads["assigned"].ProjectName);
  Assert.Equal("CalibrationOther",threads["outside"].ProjectName);
  Assert.Equal("未关联项目",threads["none"].ProjectName);
 }

 [Fact] public void ReadsDatabaseWithoutDesktopMetadata() {
  var thread=Assert.Single(ReadThreads("""
   CREATE TABLE threads (id TEXT PRIMARY KEY,title TEXT,cwd TEXT,updated_at INTEGER,archived INTEGER,source TEXT);
   INSERT INTO threads VALUES ('legacy','已有标题','D:\Projects\Legacy',100,0,'cli');
   """));
  Assert.Equal("已有标题",thread.Title);
  Assert.Equal("Legacy",thread.ProjectName);
  Assert.Equal(100,thread.Timestamp);
 }

 static IReadOnlyList<CodexThread> ReadThreads(string sql) {
  var home=Path.Combine(Path.GetTempPath(),"codex-reset-metadata-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(home);
  try {
   using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(home,"state_5.sqlite"),Pooling=false}.ToString())) {
    db.Open();using var command=db.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();
   }
   return new ThreadHistory(home).AllThreads();
  } finally { SqliteConnection.ClearAllPools();Directory.Delete(home,true); }
 }
}
