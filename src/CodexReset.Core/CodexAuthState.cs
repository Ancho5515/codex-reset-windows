using System.Security.Cryptography;

namespace CodexReset.Core;

internal static class CodexAuthState
{
    public static string? Read(string home)
    {
        try
        {
            return string.Join("|", new[] { "auth.json", "config.toml" }.Select(name =>
            {
                var path = Path.Combine(home, name);
                if (!File.Exists(path)) return "missing";
                using var stream = File.OpenRead(path);
                return Convert.ToHexString(SHA256.HashData(stream));
            }));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
