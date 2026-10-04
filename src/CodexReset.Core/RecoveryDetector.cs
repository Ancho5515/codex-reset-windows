namespace CodexReset.Core;
public sealed class RecoveryDetector {
 private bool _wasLimited;
 public bool Observe(RateLimitSnapshot snapshot) {
  var limited=!string.IsNullOrWhiteSpace(snapshot.ReachedType)&&!string.Equals(snapshot.ReachedType,"none",StringComparison.OrdinalIgnoreCase) || snapshot.UsedPercent>=100;
  var recovered=_wasLimited&&!limited; _wasLimited=limited; return recovered;
 }
}
