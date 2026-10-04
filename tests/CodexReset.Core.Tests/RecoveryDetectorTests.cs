using CodexReset.Core;
namespace CodexReset.Core.Tests;
public class RecoveryDetectorTests {
 [Fact] public void DetectsRecoveryOnlyOnLimitedToAvailableTransition() {
   var d=new RecoveryDetector();
   Assert.False(d.Observe(new RateLimitSnapshot(100,"rate_limit_reached")));
   Assert.True(d.Observe(new RateLimitSnapshot(42,null)));
   Assert.False(d.Observe(new RateLimitSnapshot(20,null)));
 }
 [Fact] public void ReachedTypeMarksLimitedEvenBelowHundredPercent() {
   var d=new RecoveryDetector();
   Assert.False(d.Observe(new RateLimitSnapshot(20,"workspace_owner_usage_limit_reached")));
   Assert.True(d.Observe(new RateLimitSnapshot(20,null)));
 }
}
