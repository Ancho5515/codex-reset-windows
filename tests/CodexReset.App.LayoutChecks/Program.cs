using CodexReset.App;
using CodexReset.Core;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        var fixture = Path.Combine(Path.GetTempPath(), "codex-reset-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            var settings = Path.Combine(fixture, "settings.json");
            SettingsStore.Save(AppSettings.Default with { AutoContinue = false }, settings);
            foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
            {
                using var window = new MainForm(fixture, settings);
                try
                {
                    // 隔离布局检查，不连接后台或向真实对话发送指令。
                    typeof(MainForm).GetField("_busy", Private)!.SetValue(window, true);
                    var deviceScale = window.DeviceDpi / 96f;
                    window.AutoScaleMode = AutoScaleMode.None;
                    window.MinimumSize = Size.Empty;
                    window.Scale(new SizeF(scale / deviceScale, scale / deviceScale));
                    window.Font = new Font("Microsoft YaHei UI", 9 * scale / deviceScale);
                    Find<Label>(window, "AppTitle").Font = new Font(window.Font.FontFamily, 18 * scale / deviceScale, FontStyle.Bold);
                    window.ClientSize = new Size((int)(900 * scale), (int)(700 * scale));
                    window.Opacity = 0;
                    window.ShowInTaskbar = false;
                    window.Show();
                    ((System.Windows.Forms.Timer)typeof(MainForm).GetField("_timer", Private)!.GetValue(window)!).Stop();
                    Application.DoEvents();
                    var activity = new DateTimeOffset(2026, 10, 8, 3, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
                    var conversations = new List<CodexThread>
                    {
                        new("fixture-1", "核对校准迭代流程", @"D:\Projects\Calibration", null, activity) { ProjectName = "LED 校准" },
                        new("fixture-2", "检查测量数据", @"D:\Projects\Calibration\tools", null, activity - 60) { ProjectName = "LED 校准" },
                        new("fixture-3", "整理本周进展", @"D:\Projects\Notes", null, 0) { ProjectName = "工作记录" }
                    };
                    typeof(MainForm).GetField("_allThreads", Private)!.SetValue(window, conversations);
                    typeof(MainForm).GetMethod("RenderThreads", Private)!.Invoke(window, null);
                    var list = Find<ListView>(window, "Conversations");
                    var expectedTime = DateTimeOffset.FromUnixTimeSeconds(activity).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
                    if (list.Columns.Count != 3 || list.Columns[1].Text != "最后对话时间" || list.Items[0].Text != conversations[0].Title ||
                        list.Items[0].SubItems.Count != 3 || list.Items[0].SubItems[1].Text != expectedTime || list.Items[2].SubItems[1].Text != "—" ||
                        list.Groups.Cast<ListViewGroup>().Any(group => group.Header.Contains(':')))
                        throw new Exception("列表标题、最后对话时间或项目分组显示不正确。");
                    foreach (ListViewItem item in list.Items)
                        if (TextRenderer.MeasureText(item.SubItems[1].Text, list.Font).Width + 8 > list.Columns[1].Width)
                            throw new Exception("最后对话时间列宽不足。");
                    list.Items[0].Checked = true;
                    var search = Find<TextBox>(window, "ConversationSearch");
                    search.Text = "工作记录";
                    if (list.Items.Count != 1) throw new Exception("项目名称搜索未生效。");
                    search.Clear();
                    if (list.Items.Count != 3 || list.CheckedItems.Count != 1) throw new Exception("搜索后未保留对话勾选。");
                    using var usage = JsonDocument.Parse(JsonSerializer.Serialize(new
                    {
                        rateLimits = new
                        {
                            primary = new { usedPercent = 42, resetsAt = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds() },
                            secondary = new { usedPercent = 68, resetsAt = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds() },
                            planType = "pro",
                            credits = new { balance = "20" }
                        }
                    }));
                    typeof(MainForm).GetMethod("ApplyUsage", Private)!.Invoke(window, [usage.RootElement]);
                    foreach (var name in new[] { "PrimaryReset", "SecondaryReset" })
                        Find<Label>(window, name).Text = "恢复：10-15 23:59 · 剩余 167 小时 59 分钟";
                    Find<Label>(window, "ConnectionStatus").Text = "连接或读取失败，查看运行日志";
                    window.PerformLayout();
                    AssertTextFits(window);
                    var tray = (NotifyIcon)typeof(MainForm).GetField("_tray", Private)!.GetValue(window)!;
                    if (!ReferenceEquals(window.Icon, tray.Icon)) throw new Exception("窗口和托盘图标不一致。");
                    using var icon = window.Icon!.ToBitmap();
                    if (!HasTurquoise(icon)) throw new Exception("窗口未加载设计图标。");
                    if (args.Length > 0)
                    {
                        Directory.CreateDirectory(args[0]);
                        typeof(MainForm).GetMethod("ApplyUsage", Private)!.Invoke(window, [usage.RootElement]);
                        var deadline = DateTime.UtcNow.AddMilliseconds(500);
                        while (DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
                        using var bitmap = new Bitmap(window.Width, window.Height);
                        window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
                        bitmap.Save(Path.Combine(args[0], $"layout-{scale * 100:0}.png"), ImageFormat.Png);
                    }
                    Console.WriteLine($"PASS: {scale * 100:0}% 布局文字完整，窗口和托盘图标已加载。");
                }
                finally
                {
                    typeof(MainForm).GetMethod("ExitApplication", Private)!.Invoke(window, null);
                    Task.Run(window.StopAsync).GetAwaiter().GetResult();
                }
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine($"FAIL: {error.Message}"); return 1; }
        finally
        {
            var resolved = Path.GetFullPath(fixture);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("codex-reset-layout-"))
                throw new InvalidOperationException("验证目录不符合清理范围。");
            Directory.Delete(resolved, true);
        }
    }

    private static T Find<T>(Control window, string name) where T : Control => (T)window.Controls.Find(name, true).Single();

    private static void AssertTextFits(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is TabPage page && page.Text == "运行日志") continue;
            if (control is Label or Button or CheckBox)
            {
                var text = TextRenderer.MeasureText(control.Text, control.Font, Size.Empty, TextFormatFlags.NoPadding);
                var available = control.ClientSize;
                if (control is Button) { available.Width -= control.Padding.Horizontal; available.Height -= control.Padding.Vertical; }
                if (!parent.ClientRectangle.Contains(control.Bounds) || text.Width > available.Width || text.Height > available.Height)
                    throw new Exception($"文字被裁切：{control.Name}（{control.Text}）。");
            }
            AssertTextFits(control);
        }
    }

    private static bool HasTurquoise(Bitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var color = bitmap.GetPixel(x, y);
                if (color.A > 128 && color.G > 150 && color.B > 120 && color.R < 120) return true;
            }
        return false;
    }
}
