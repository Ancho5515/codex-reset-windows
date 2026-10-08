using CodexReset.Core;

namespace CodexReset.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var window = new MainForm(CodexEnvironment.ResolveHome());
        Application.Run(window);
        Task.Run(window.StopAsync).GetAwaiter().GetResult();
    }
}
