namespace CodexReset.Core;
public sealed class RecoveryDetector {
 private bool _wasLimited;
 public bool Observe(RateLimitSnapshot snapshot) {
  var limited=IsLimited(snapshot);
  var recovered=_wasLimited&&!limited; _wasLimited=limited; return recovered;
 }
 public static bool IsLimited(RateLimitSnapshot snapshot) =>
  !string.IsNullOrWhiteSpace(snapshot.ReachedType)&&!string.Equals(snapshot.ReachedType,"none",StringComparison.OrdinalIgnoreCase)
  || snapshot.UsedPercent>=100 || snapshot.Secondary?.UsedPercent>=100;
}
