namespace CodexReset.Core;
public sealed record RateLimitWindow(int UsedPercent, long? ResetsAt=null, int? WindowDurationMins=null);
public sealed record RateLimitSnapshot(int UsedPercent, string? ReachedType, RateLimitWindow? Primary=null, RateLimitWindow? Secondary=null, string? PlanType=null, string? CreditBalance=null);
public sealed record CodexThread(string ThreadId,string Title,string Cwd,string? RecoveryHint,long Timestamp) {
 public string ProjectName { get; init; } = "";
}
