namespace CodexReset.Core;

public static class CodexEnvironment
{
    public static string ResolveHome(IReadOnlyDictionary<string, string?> environment, string userProfile)
    {
        if (environment.TryGetValue("CODEX_HOME", out var configured) && !string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);
        return Path.Combine(userProfile, ".codex");
    }

    public static string ResolveHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }
}
