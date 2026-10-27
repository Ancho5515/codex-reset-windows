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
        var originalBinary = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        var offlineBinary = Path.Combine(fixture, "offline-codex.cmd");
        File.WriteAllText(offlineBinary, "@exit /b 1");
        Environment.SetEnvironmentVariable("CODEX_CLI_PATH", offlineBinary);
        SettingsStore.Save(new(true, "继续", ["retry", "slow", "desktop", "idle"]), settingsPath);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture, "state_5.sqlite"), Pooling = false }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE threads (id TEXT PRIMARY KEY,title TEXT,cwd TEXT,updated_at INTEGER,archived INTEGER,source TEXT);
                INSERT INTO threads VALUES ('retry','首次失败后重试','D:\RecoveryCheck',1,0,'cli');
                INSERT INTO threads VALUES ('slow','启动耗时16秒','D:\RecoveryCheck',1,0,'cli');
                INSERT INTO threads VALUES ('desktop','桌面占用通过队列续作','D:\RecoveryCheck',1,0,'cli');
                INSERT INTO threads VALUES ('idle','没有限额中断的对话','D:\RecoveryCheck',1,0,'cli');
                """;
            command.ExecuteNonQuery();
        }
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture, "thread_history_1.sqlite"), Pooling = false }.ToString()))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE thread_turns (thread_id TEXT,turn_id TEXT,rollout_ordinal INTEGER,status TEXT,error_json TEXT,started_at INTEGER);
                INSERT INTO thread_turns VALUES ('retry','failed-1',1,'failed','{"codexErrorInfo":"usageLimitExceeded"}',1);
                INSERT INTO thread_turns VALUES ('slow','failed-1',1,'failed','{"codexErrorInfo":"usageLimitExceeded"}',1);
                INSERT INTO thread_turns VALUES ('desktop','failed-1',1,'failed','{"codexErrorInfo":"usageLimitExceeded"}',1);
                INSERT INTO thread_turns VALUES ('idle','done',1,'completed',NULL,1);
                """;
            command.ExecuteNonQuery();
        }
        var server = new AppServerStub { RateLimitsResponse = new { rateLimits = new { primary = new { usedPercent = 100 } } } };
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
            var deadline = DateTime.UtcNow.AddMinutes(2);
            var recoverQuotaAt = DateTime.UtcNow.AddSeconds(12);
            var quotaRecovered = false;
            var checking = false;
            using var check = new System.Windows.Forms.Timer { Interval = 1000 };
            check.Tick += async (_, _) =>
            {
                if (checking) return;
                if (!quotaRecovered)
                {
                    if ((bool)typeof(MainForm).GetField("_busy", Private)!.GetValue(window)!) return;
                    checking = true;
                    try
                    {
                        await (Task)typeof(MainForm).GetMethod("RefreshAsync", Private)!.Invoke(window, null)!;
                        if (server.Messages.Count != 0 || server.QueuedMessages.Count != 0 || server.AccountRequests == 0 || !log.Text.Contains("额度尚未恢复"))
                            throw new Exception("余量耗尽时仍发送了继续指令，或后台未检查额度。");
                        if (DateTime.UtcNow < recoverQuotaAt) return;
                        server.RateLimitsResponse = new { ordinaryUsageAllowed = true, rateLimits = new { } };
                        quotaRecovered = true;
                        await (Task)typeof(MainForm).GetMethod("RefreshAsync", Private)!.Invoke(window, null)!;
                    }
                    catch (Exception error)
                    {
                        File.WriteAllText(Path.Combine(output, "gui-result.txt"), "FAIL: " + error.Message + "\n" + log.Text);
                        check.Stop();
                    }
                    finally { checking = false; }
                    return;
                }
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
                        server.Messages.ContainsKey("desktop") || server.Messages.ContainsKey("idle") || server.AccountRequests == 0 || !log.Text.Contains("桌面队列") ||
                        !log.Text.Contains("测试后台暂时不可用") || !log.Text.Contains("首次失败后重试") || !log.Text.Contains("启动耗时16秒"))
                        throw new Exception("续作次数或界面失败详情不符合预期。");
                    using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture, "thread_history_1.sqlite"), Pooling = false }.ToString()))
                    {
                        db.Open();
                        using var command = db.CreateCommand();
                        command.CommandText = "INSERT INTO thread_turns VALUES ('desktop','failed-2',2,'failed','{\"codexErrorInfo\":\"usageLimitExceeded\"}',2)";
                        command.ExecuteNonQuery();
                    }
                    server.QueueEntries["desktop"].Clear();
                    typeof(MainForm).GetField("_settings", Private)!.SetValue(window, new AppSettings(true, "继续", ["desktop"]));
                    await (Task)typeof(MainForm).GetMethod("RefreshAsync", Private)!.Invoke(window, null)!;
                    if (server.QueuedMessages["desktop"].Count != 1)
                        throw new Exception("再次限额后未确认新的额度恢复，仍重复提交继续指令。");
                    await (Task)typeof(MainForm).GetMethod("ContinueSelectedAsync", Private)!.Invoke(window, null)!;
                    await (Task)typeof(MainForm).GetMethod("RefreshAsync", Private)!.Invoke(window, null)!;
                    if (server.QueuedMessages["desktop"].Count != 2 || server.QueueEntries["desktop"].Count != 1)
                        throw new Exception("手动续作未读取最新失败轮次，自动刷新重复入队。");

                    server.StopConnection();
                    using (var disconnected = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        while (client.IsConnected) await Task.Delay(10, disconnected.Token);
                    var oldBinary = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
                    var unavailable = Path.Combine(fixture, "unavailable-codex.cmd");
                    File.WriteAllText(unavailable, "@exit /b 1");
                    try
                    {
                        Environment.SetEnvironmentVariable("CODEX_CLI_PATH", unavailable);
                        await (Task)typeof(MainForm).GetMethod("RefreshAsync", Private)!.Invoke(window, null)!;
                        var connection = (Label)window.Controls.Find("ConnectionStatus", true).Single();
                        if (typeof(MainForm).GetField("_client", Private)!.GetValue(window) is not null || connection.ForeColor != Color.Firebrick)
                            throw new Exception("断线后仍复用坏连接或错误显示已连接。");
                    }
                    finally { Environment.SetEnvironmentVariable("CODEX_CLI_PATH", oldBinary); }

                    File.WriteAllText(Path.Combine(output, "gui-result.txt"), "PASS: 后台检查额度且界面不显示余量；余量耗尽时不发送，恢复后自动续作；再次限额后等待新的额度恢复或账号变化，不循环提交；首次失败后重试成功；16秒启动独立计时；桌面写入占用转入队列并唤醒；三个中断对话各提交一次；未中断的已选对话不自动发送；手动读取最新轮次且自动不重复入队；断线后清理连接并重新尝试连接。\n" + log.Text);
                    ((System.Windows.Forms.Timer)typeof(MainForm).GetField("_timer", Private)!.GetValue(window)!).Stop();
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
            Environment.SetEnvironmentVariable("CODEX_CLI_PATH", originalBinary);
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
