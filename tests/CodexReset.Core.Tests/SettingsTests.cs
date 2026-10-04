using CodexReset.Core;
namespace CodexReset.Core.Tests;
public class SettingsTests {
 [Fact] public void DefaultsDoNotSelectAnyConversation() {
  var s=AppSettings.Default; Assert.Empty(s.SelectedThreadIds); Assert.True(s.AutoContinue); Assert.Equal("继续",s.Command);
 }
 [Fact] public void JsonRoundTripPreservesSelection() {
  var s=AppSettings.Default with { SelectedThreadIds=new HashSet<string>{"a","b"} }; var x=SettingsStore.Deserialize(SettingsStore.Serialize(s)); Assert.Equal(2,x.SelectedThreadIds.Count);
 }
}
