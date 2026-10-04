using System.Text.Json;
namespace CodexReset.Core;
public static class RpcMessage {
 static readonly JsonSerializerOptions Options=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
 public static string Request(int id,string method,object? @params=null) {
  var body=@params is null ? new Dictionary<string,object?>{{"method",method},{"id",id}} : new Dictionary<string,object?>{{"method",method},{"id",id},{"params",@params}};
  return JsonSerializer.Serialize(body,Options);
 }
 public static string Notification(string method,object? @params=null) {
  var body=@params is null ? new Dictionary<string,object?>{{"method",method}} : new Dictionary<string,object?>{{"method",method},{"params",@params}};
  return JsonSerializer.Serialize(body,Options);
 }
 public static string TurnStart(int id,string threadId,string command)=>Request(id,"turn/start",new {threadId,input=new[]{new {type="text",text=command}}});
}
