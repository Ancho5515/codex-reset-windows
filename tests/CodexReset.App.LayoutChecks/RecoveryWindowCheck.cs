using CodexReset.App;
using CodexReset.Core;
using CodexReset.Core.Tests;
using Microsoft.Data.Sqlite;
using System.Reflection;

internal static class RecoveryWindowCheck
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    // 隔离设置、SQLite 和 RPC 后台，实际运行生产 MainForm，不接触真实对话。
    public static int Run(string output, bool autoClose = false)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        var fixture = Path.Combine(Path.GetTempPath(), "codex-reset-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        Directory.CreateDirectory(output);
        var settingsPath = Path.Combine(fixture, "settings.json");
        SettingsStore.Save(new(true, "继续", ["retry", "slow", "desktop"]), settingsPath);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture, "state_5.sqlite"), Pooling = false }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE threads (id TEXT PRIMARY KEY,title TEXT,cwd TEXT,updated_at INTEGER,archived INTEGER,source TEXT);
                INSERT INTO threads VALUES ('retry','首次失败后重试','D:\RecoveryCheck',1,0,'cli');
                INSERT INTO threads VALUES ('slow','启动耗时16秒','D:\RecoveryCheck',1,0,'cli');
                INSERT INTO threads VALUES ('desktop','桌面占用通过队列续作','D:\RecoveryCheck',1,0,'cli');
                """;
            command.ExecuteNonQuery();
        }
        var server = new AppServerStub();
        server.FailResumeOnce.Add("retry");
        server.StartDelays["slow"] = TimeSpan.FromSeconds(16);
        server.ActiveWriterThreads.Add("desktop");
        var wakes = new List<string>();
        var client = new AppServerClient();
        var clientDisposed = false;
        var result = 1;
        try
        {
            client.ConnectAsync(server.Address).GetAwaiter().GetResult();
            client.InitializeAsync().GetAwaiter().GetResult();
            using var window = new MainForm(fixture, settingsPath) { Text = "CodexReset — 续作回归验证" };
            typeof(MainForm).GetField("_monitor", Private)!.SetValue(window, new AutoContinueMonitor(
                (id, message) => typeof(MainForm).GetMethod("LogContinuation", Private)!.Invoke(window, [id, message]), wakes.Add));
            // 后台启动器的 Hidden 仅用于隐藏控制台；再次显示确保验证窗口可实际查看。
            window.Shown += (_, _) => window.BeginInvoke(new Action(() => { window.Hide(); window.Show(); }));
            typeof(MainForm).GetField("_client", Private)!.SetValue(window, client);
            var log = (TextBox)window.Controls.Find("ActivityLog", true).Single();
            var ready = DateTime.UtcNow.AddSeconds(5);
            var deadline = DateTime.UtcNow.AddMinutes(2);
            var checking = false;
            using var check = new System.Windows.Forms.Timer { Interval = 1000 };
            check.Tick += async (_, _) =>
            {
                if (checking) return;
                if (DateTime.UtcNow >= ready) server.UsedPercent = 20;
                if ((bool)typeof(MainForm).GetField("_busy", Private)!.GetValue(window)! ||
                    !server.Messages.ContainsKey("retry") || !server.Messages.ContainsKey("slow") ||
                    !server.QueuedMessages.ContainsKey("desktop"))
                {
                    if (DateTime.UtcNow < deadline) return;
                    File.WriteAllText(Path.Combine(output, "gui-result.txt"), "FAIL: 超时，三个测试对话未完成续作。\n" + log.Text);
                    check.Stop();
                    return;
                }
                checking = true;
                check.Stop();
                try
                {
                    await (Task)typeof(MainForm).GetMethod("RefreshAsync", Private)!.Invoke(window, null)!;
                    if (server.Messages["retry"].Count != 1 || server.Messages["slow"].Count != 1 ||
                        server.QueuedMessages["desktop"].Count != 1 || !wakes.SequenceEqual(["desktop"]) ||
                        server.Messages.ContainsKey("desktop") || !log.Text.Contains("桌面队列") ||
                        !log.Text.Contains("测试后台暂时不可用") || !log.Text.Contains("首次失败后重试") || !log.Text.Contains("启动耗时16秒"))
                        throw new Exception("续作次数或界面失败详情不符合预期。");
                    File.WriteAllText(Path.Combine(output, "gui-result.txt"), "PASS: 首次失败后重试成功；16秒启动独立计时；桌面写入占用转入队列并唤醒；三个对话各提交一次指令；实际界面显示各通道结果。\n" + log.Text);
                    result = 0;
                    window.Text = "CodexReset — 续作回归验证（已通过）";
                    Console.WriteLine("PASS: Windows 续作回归验证。");
                    if (autoClose) typeof(MainForm).GetMethod("ExitApplication", Private)!.Invoke(window, null);
                }
                catch (Exception error)
                {
                    File.WriteAllText(Path.Combine(output, "gui-result.txt"), "FAIL: " + error.Message + "\n" + log.Text);
                }
            };
            check.Start();
            Application.Run(window);
            Task.Run(window.StopAsync).GetAwaiter().GetResult();
            clientDisposed = true;
            return result;
        }
        finally
        {
            Task.Run(async () =>
            {
                if (!clientDisposed) await client.DisposeAsync();
                await server.DisposeAsync();
            }).GetAwaiter().GetResult();
            SqliteConnection.ClearAllPools();
            var resolved = Path.GetFullPath(fixture);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("codex-reset-recovery-"))
                throw new InvalidOperationException("验证目录不符合清理范围。");
            Directory.Delete(resolved, true);
        }
    }
}
