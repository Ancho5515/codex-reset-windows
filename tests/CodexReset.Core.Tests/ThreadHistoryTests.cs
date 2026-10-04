using CodexReset.Core;
namespace CodexReset.Core.Tests;
public class ThreadHistoryTests {
 [Fact] public void ExtractsRecoveryHint() {
  var json="{\"message\":\"You've hit your usage limit. try again at 1:17 PM\"}";
  Assert.Equal("1:17 PM",ThreadHistory.ExtractRecoveryHint(json));
 }
 [Fact] public void MeaninglessContinueTitleFallsBackToOriginalPrompt() {
  Assert.Equal("Implement Windows reset tool",ThreadHistory.DisplayTitle("继续","Implement Windows reset tool"));
 }
}
