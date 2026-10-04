using CodexReset.Core;
using System.Text.Json;
namespace CodexReset.Core.Tests;
public class RpcMessageTests {
 [Fact] public void InitializeUsesCodexAppServerShape() {
  var json=RpcMessage.Request(1,"initialize",new {clientInfo=new {name="CodexReset",version="1.0.0"},capabilities=new {experimentalApi=true}});
  using var d=JsonDocument.Parse(json); var root=d.RootElement;
  Assert.False(root.TryGetProperty("jsonrpc",out _));
  Assert.Equal("initialize",root.GetProperty("method").GetString());
  Assert.Equal(1,root.GetProperty("id").GetInt32());
 }
 [Fact] public void TurnStartCarriesThreadAndTextInput() {
  var json=RpcMessage.TurnStart(7,"thread-1","继续");
  using var d=JsonDocument.Parse(json); var p=d.RootElement.GetProperty("params");
  Assert.Equal("thread-1",p.GetProperty("threadId").GetString());
  Assert.Equal("text",p.GetProperty("input")[0].GetProperty("type").GetString());
  Assert.Equal("继续",p.GetProperty("input")[0].GetProperty("text").GetString());
 }
}
