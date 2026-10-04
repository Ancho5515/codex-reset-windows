using CodexReset.Core;
namespace CodexReset.Core.Tests;
public class CodexEnvironmentTests {
 [Fact] public void UsesCodexHomeEnvironmentVariableWhenPresent() {
   var path = CodexEnvironment.ResolveHome(new Dictionary<string,string?> { ["CODEX_HOME"] = @"D:\codex-home" }, @"C:\Users\test");
   Assert.Equal(@"D:\codex-home", path);
 }
 [Fact] public void DefaultsToDotCodexUnderUserProfile() {
   var path = CodexEnvironment.ResolveHome(new Dictionary<string,string?>(), @"C:\Users\test");
   Assert.Equal(@"C:\Users\test\.codex", path);
 }
}