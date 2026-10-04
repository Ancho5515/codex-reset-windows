using CodexReset.Core;
namespace CodexReset.Core.Tests;
public class CodexConfigTests {
 [Fact] public void ReadsRemoteControlInsideFeatures() {
  Assert.True(CodexConfig.Parse("[features]\nremote_control = true\n").RemoteControlEnabled);
 }
 [Fact] public void EnablingPreservesExistingFeaturesSection() {
  var s=CodexConfig.SetRemoteControlText("[features]\nfoo = true\n",true);
  Assert.Equal(1,s.Split("[features]").Length-1);
  Assert.Contains("remote_control = true",s);
 }
}
