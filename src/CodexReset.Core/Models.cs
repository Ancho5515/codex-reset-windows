namespace CodexReset.Core;
public sealed record CodexThread(string ThreadId,string Title,string Cwd,string? PausedTurnId,long Timestamp) {
 public string ProjectName { get; init; } = "";
}
