using CodexReset.Core;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace CodexReset.App;

public sealed class MainForm : Form
{
    private readonly string _codexHome;
    private readonly string? _settingsPath;
    private AppSettings _settings;
    private readonly AutoContinueMonitor _monitor = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _connectionLifetime;
    private AppServerManager? _manager;
    private AppServerClient? _client;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30000 };
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly Icon _applicationIcon;
    private readonly NotifyIcon _tray;
    private readonly Label _connection = new() { Name = "ConnectionStatus", Text = "正在连接…", AutoSize = true };
    private readonly Label _account = new() { Name = "AccountStatus", Text = "正在读取账户用量…", AutoSize = true };
    private readonly ProgressBar _primaryBar = new() { Name = "PrimaryUsage", Dock = DockStyle.Fill };
    private readonly ProgressBar _secondaryBar = new() { Name = "SecondaryUsage", Dock = DockStyle.Fill };
    private readonly Label _primaryText = new() { Name = "PrimaryText", Text = "等待用量数据", AutoSize = true };
    private readonly Label _secondaryText = new() { Name = "SecondaryText", Text = "等待用量数据", AutoSize = true };
    private readonly Label _primaryReset = new() { Name = "PrimaryReset", Text = "恢复时间：—", AutoSize = true };
    private readonly Label _secondaryReset = new() { Name = "SecondaryReset", Text = "恢复时间：—", AutoSize = true };
    private readonly CheckBox _autoContinue = new() { Name = "AutoContinue", Text = "用量恢复后自动继续", AutoSize = true };
    private readonly TextBox _command = new() { Name = "ContinueCommand", Width = 180 };
    private readonly TextBox _search = new() { Name = "ConversationSearch", Width = 240, PlaceholderText = "搜索对话标题或项目" };
    private readonly CheckBox _limitedOnly = new() { Name = "LimitedOnly", Text = "仅显示限额记录", AutoSize = true };
    private readonly ListView _threads = new()
    {
        Name = "Conversations", Dock = DockStyle.Fill, View = View.Details,
        CheckBoxes = true, FullRowSelect = true, MultiSelect = false, HideSelection = false,
        ShowItemToolTips = true
    };
    private readonly Label _selection = new() { Name = "SelectionSummary", AutoSize = true };
    private readonly TextBox _log = new()
    {
        Name = "ActivityLog", Dock = DockStyle.Fill, Multiline = true,
        ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White
    };
    private readonly Button _refresh = MakeButton("刷新", "Refresh");
    private readonly Button _continue = MakeButton("立即继续已选对话", "ContinueSelected");
    private readonly TabPage _conversationTab = new("对话");
    private IReadOnlyList<CodexThread> _allThreads = [];
    private HashSet<string> _limitedIds = [];
    private bool _loadingThreads;
    private bool _busy;
    private bool _exiting;

    public MainForm(string codexHome, string? settingsPath = null)
    {
        _codexHome = codexHome;
        _settingsPath = settingsPath;
        _settings = SettingsStore.Load(settingsPath);
        SuspendLayout();
        Text = "CodexReset — 用量与对话";
        Name = "CodexResetMainWindow";
        ClientSize = new Size(900, 700);
        MinimumSize = new Size(840, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(245, 244, 240);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("CodexReset.App.Assets.codex-reset.ico")!)
            _applicationIcon = new Icon(iconStream, new Size(32, 32));
        Icon = _applicationIcon;

        BuildLayout();
        _autoContinue.Checked = _settings.AutoContinue;
        _command.Text = _settings.Command;
        _autoContinue.CheckedChanged += (_, _) => SaveSettings();
        _search.TextChanged += (_, _) => RenderThreads();
        _limitedOnly.CheckedChanged += (_, _) => RenderThreads();
        _threads.SizeChanged += (_, _) => ResizeThreadColumns();
        _threads.FontChanged += (_, _) => ResizeThreadColumns();
        _threads.ItemChecked += (_, e) =>
        {
            if (_loadingThreads || e.Item.Tag is not CodexThread thread) return;
            if (e.Item.Checked) _settings.SelectedThreadIds.Add(thread.ThreadId);
            else _settings.SelectedThreadIds.Remove(thread.ThreadId);
            SaveSettings();
            UpdateSelection();
        };
        _threads.DoubleClick += (_, _) =>
        {
            if (_threads.SelectedItems.Count > 0 && _threads.SelectedItems[0].Tag is CodexThread thread)
                OpenCodex($"codex://threads/{thread.ThreadId}");
        };
        _refresh.Click += async (_, _) => await RefreshAsync();
        _continue.Click += async (_, _) => await ContinueSelectedAsync();

        _trayMenu.Items.Add("打开界面", null, (_, _) => ShowWindow());
        _trayMenu.Items.Add("刷新", null, async (_, _) => await RefreshAsync());
        _trayMenu.Items.Add("打开 Codex", null, (_, _) => OpenCodex("codex://"));
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("退出", null, (_, _) => ExitApplication());
        _tray = new NotifyIcon { Text = "CodexReset", Icon = Icon, Visible = true, ContextMenuStrip = _trayMenu };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        _timer.Tick += async (_, _) => await RefreshAsync();
        UpdateSelection();
        ResumeLayout(true);
    }

    private static Button MakeButton(string text, string name) => new()
    {
        Text = text, Name = name, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(8, 3, 8, 3), UseVisualStyleBackColor = true
    };

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 7 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 4; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        header.Controls.Add(new Label { Name = "AppTitle", Text = "CodexReset", AutoSize = true, Font = new Font(Font.FontFamily, 18, FontStyle.Bold) }, 0, 0);
        _connection.Anchor = AnchorStyles.Right;
        header.Controls.Add(_connection, 1, 0);
        layout.Controls.Add(header, 0, 0);

        var usage = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        usage.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        usage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        usage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        usage.Controls.Add(MakeUsageCard("5 小时用量", _primaryText, _primaryBar, _primaryReset), 0, 0);
        usage.Controls.Add(MakeUsageCard("每周用量", _secondaryText, _secondaryBar, _secondaryReset), 1, 0);
        layout.Controls.Add(usage, 0, 1);
        layout.Controls.Add(_account, 0, 2);

        var options = new GroupBox { Text = "自动续作", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var optionRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 4, 8, 4) };
        _autoContinue.Margin = new Padding(3, 7, 18, 3);
        optionRow.Controls.Add(_autoContinue);
        optionRow.Controls.Add(new Label { Text = "指令", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
        optionRow.Controls.Add(_command);
        var save = MakeButton("保存指令", "SaveCommand");
        save.Click += (_, _) => SaveSettings();
        optionRow.Controls.Add(save);
        options.Controls.Add(optionRow);
        layout.Controls.Add(options, 0, 3);

        var tabs = new TabControl { Name = "MainTabs", Dock = DockStyle.Fill };
        var conversations = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 1, RowCount = 3 };
        conversations.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        conversations.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        conversations.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        conversations.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        filters.Controls.Add(_search);
        _limitedOnly.Margin = new Padding(12, 6, 8, 3);
        filters.Controls.Add(_limitedOnly);
        var selectAll = MakeButton("全选当前列表", "SelectVisible");
        selectAll.Click += (_, _) => SetVisibleSelection(true);
        filters.Controls.Add(selectAll);
        var clear = MakeButton("清除当前选择", "ClearVisible");
        clear.Click += (_, _) => SetVisibleSelection(false);
        filters.Controls.Add(clear);
        conversations.Controls.Add(filters, 0, 0);
        _threads.Columns.Add("对话标题", 330);
        _threads.Columns.Add("最后对话时间", 170);
        _threads.Columns.Add("状态", 110);
        conversations.Controls.Add(_threads, 0, 1);
        conversations.Controls.Add(_selection, 0, 2);
        _conversationTab.Controls.Add(conversations);
        tabs.TabPages.Add(_conversationTab);
        var logTab = new TabPage("运行日志");
        logTab.Controls.Add(_log);
        tabs.TabPages.Add(logTab);
        layout.Controls.Add(tabs, 0, 4);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        actions.Controls.Add(_refresh);
        var open = MakeButton("打开 Codex", "OpenCodex");
        open.Click += (_, _) => OpenCodex("codex://");
        actions.Controls.Add(open);
        actions.Controls.Add(_continue);
        var hide = MakeButton("收起到托盘", "HideWindow");
        hide.Click += (_, _) => Hide();
        actions.Controls.Add(hide);
        var exit = MakeButton("退出", "ExitApplication");
        exit.Click += (_, _) => ExitApplication();
        actions.Controls.Add(exit);
        layout.Controls.Add(actions, 0, 5);
        layout.Controls.Add(new Label { Text = "每 30 秒更新 · 双击对话在 Codex 中打开 · 关闭窗口后可从托盘重新打开", AutoSize = true, ForeColor = Color.DimGray }, 0, 6);
        Controls.Add(layout);
    }

    private static GroupBox MakeUsageCard(string title, Label value, ProgressBar bar, Label reset)
    {
        var card = new GroupBox { Text = title, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12) };
        var rows = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 3 };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rows.Controls.Add(value, 0, 0);
        rows.Controls.Add(bar, 0, 1);
        rows.Controls.Add(reset, 0, 2);
        card.Controls.Add(rows);
        return card;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _timer.Start();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_busy || _lifetime.IsCancellationRequested) return;
        SetBusy(true);
        try
        {
            var history = new ThreadHistory(_codexHome);
            var data = await Task.Run(() => (All: history.AllThreads(), Limited: history.UsageLimitedThreads(1000)), _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            _allThreads = data.All;
            _limitedIds = data.Limited.Select(x => x.ThreadId).ToHashSet();
            RenderThreads();

            if (_client is null)
            {
                _connection.Text = "正在连接 Codex…";
                _manager = new AppServerManager(_codexHome);
                _connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _connectionLifetime.CancelAfter(TimeSpan.FromSeconds(15));
                var server = await _manager.StartOwnServerAsync(_connectionLifetime.Token);
                _client = server.Client;
                _connectionLifetime.CancelAfter(Timeout.InfiniteTimeSpan);
                Log("已连接本机 Codex。");
            }

            using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            request.CancelAfter(TimeSpan.FromSeconds(15));
            ApplyUsage(await _client.RequestAsync("account/rateLimits/read", ct: request.Token));
            var snapshot = _settings with { SelectedThreadIds = new HashSet<string>(_settings.SelectedThreadIds) };
            var continued = await _monitor.TickAsync(_client, snapshot, request.Token);
            if (continued > 0)
            {
                Log($"用量恢复，已继续 {continued} 个对话。");
                _tray.ShowBalloonTip(4000, "CodexReset", $"已继续 {continued} 个对话。", ToolTipIcon.Info);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (_lifetime.IsCancellationRequested) return;
            _connection.Text = error is OperationCanceledException ? "连接超时，点击刷新重试" : "连接或读取失败，查看运行日志";
            _connection.ForeColor = Color.Firebrick;
            Log(error.Message);
            await DisconnectAsync();
        }
        finally { if (!_lifetime.IsCancellationRequested) SetBusy(false); }
    }

    private void ApplyUsage(JsonElement root)
    {
        var limits = root.GetProperty("rateLimits");
        UpdateUsageCard(limits, "primary", _primaryText, _primaryBar, _primaryReset);
        UpdateUsageCard(limits, "secondary", _secondaryText, _secondaryBar, _secondaryReset);
        var plan = limits.TryGetProperty("planType", out var type) ? type.ToString() : "未提供";
        var balance = "未提供";
        if (limits.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object &&
            credits.TryGetProperty("balance", out var creditBalance) && creditBalance.ValueKind != JsonValueKind.Null)
            balance = creditBalance.ToString();
        _account.Text = $"计划：{plan}    点数余额：{balance}";
        _connection.Text = $"已连接 · {DateTime.Now:HH:mm:ss} 更新";
        _connection.ForeColor = Color.FromArgb(38, 112, 62);
        _tray.Text = $"CodexReset · {_primaryText.Text}";
    }

    private static void UpdateUsageCard(JsonElement limits, string property, Label text, ProgressBar bar, Label reset)
    {
        if (!limits.TryGetProperty(property, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            text.Text = "账户未提供此用量窗口";
            bar.Value = 0;
            reset.Text = "恢复时间：—";
            return;
        }
        var used = window.GetProperty("usedPercent").GetDouble();
        bar.Value = Math.Clamp((int)Math.Round(used), 0, 100);
        text.Text = $"已使用 {used:0.#}%";
        if (window.TryGetProperty("resetsAt", out var time) && time.ValueKind == JsonValueKind.Number)
        {
            var at = DateTimeOffset.FromUnixTimeSeconds(time.GetInt64()).LocalDateTime;
            var remaining = at - DateTime.Now;
            reset.Text = remaining > TimeSpan.Zero
                ? $"恢复：{at:MM-dd HH:mm} · 剩余 {(int)remaining.TotalHours} 小时 {remaining.Minutes} 分钟"
                : $"恢复时间：{at:MM-dd HH:mm}";
        }
        else reset.Text = "恢复时间：未提供";
    }

    private void RenderThreads()
    {
        var query = _search.Text.Trim();
        _loadingThreads = true;
        _threads.BeginUpdate();
        try
        {
            _threads.Items.Clear();
            _threads.Groups.Clear();
            var groups = new Dictionary<string, ListViewGroup>();
            foreach (var thread in _allThreads)
            {
                if (_limitedOnly.Checked && !_limitedIds.Contains(thread.ThreadId)) continue;
                var project = thread.ProjectName;
                if (query.Length > 0 && !thread.Title.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !project.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                if (!groups.TryGetValue(project, out var group))
                {
                    group = new ListViewGroup(project, HorizontalAlignment.Left);
                    groups.Add(project, group);
                    _threads.Groups.Add(group);
                }
                var item = new ListViewItem(thread.Title, group)
                {
                    Tag = thread, Checked = _settings.SelectedThreadIds.Contains(thread.ThreadId),
                    ToolTipText = $"{thread.Title}\n{thread.Cwd}"
                };
                item.SubItems.Add(thread.Timestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(thread.Timestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "—");
                item.SubItems.Add(_limitedIds.Contains(thread.ThreadId) ? "有限额记录" : "—");
                _threads.Items.Add(item);
            }
        }
        finally { _threads.EndUpdate(); _loadingThreads = false; }
        ResizeThreadColumns();
        _conversationTab.Text = $"对话（{_allThreads.Count}）";
        UpdateSelection();
    }

    private void ResizeThreadColumns()
    {
        var scale = _threads.DeviceDpi / 96f;
        var padding = (int)Math.Ceiling(20 * scale);
        _threads.Columns[1].Width = TextRenderer.MeasureText("2000-12-31 23:59", _threads.Font).Width + padding;
        _threads.Columns[2].Width = TextRenderer.MeasureText("有限额记录", _threads.Font).Width + padding;
        _threads.Columns[0].Width = Math.Max((int)Math.Ceiling(180 * scale),
            _threads.ClientSize.Width - _threads.Columns[1].Width - _threads.Columns[2].Width - SystemInformation.VerticalScrollBarWidth - 4);
    }

    private void UpdateSelection()
    {
        _selection.Text = $"当前显示 {_threads.Items.Count} 个对话 · 已勾选 {_settings.SelectedThreadIds.Count} 个，允许续作";
        _continue.Enabled = !_busy && _client is not null && _settings.SelectedThreadIds.Count > 0;
    }

    private void SetVisibleSelection(bool selected)
    {
        _loadingThreads = true;
        _threads.BeginUpdate();
        try
        {
            foreach (ListViewItem item in _threads.Items)
            {
                item.Checked = selected;
                var id = ((CodexThread)item.Tag!).ThreadId;
                if (selected) _settings.SelectedThreadIds.Add(id);
                else _settings.SelectedThreadIds.Remove(id);
            }
        }
        finally { _threads.EndUpdate(); _loadingThreads = false; }
        SaveSettings();
        UpdateSelection();
    }

    private void SaveSettings()
    {
        if (string.IsNullOrWhiteSpace(_command.Text)) _command.Text = _settings.Command;
        _settings = _settings with { AutoContinue = _autoContinue.Checked, Command = _command.Text.Trim() };
        try { SettingsStore.Save(_settings, _settingsPath); }
        catch (Exception error) { Log($"设置保存失败：{error.Message}"); _connection.Text = "设置保存失败，查看运行日志"; }
    }

    private async Task ContinueSelectedAsync()
    {
        if (_busy || _client is null || _settings.SelectedThreadIds.Count == 0) return;
        SaveSettings();
        var ids = _settings.SelectedThreadIds.ToArray();
        var command = _settings.Command;
        SetBusy(true);
        try
        {
            var engine = new AutoContinueEngine();
            foreach (var id in ids)
            {
                using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                request.CancelAfter(TimeSpan.FromSeconds(30));
                var sent = await engine.ContinueAsync(_client, id, command, request.Token);
                Log($"对话 {id}：{(sent ? "已发送续作指令" : "续作失败")}");
            }
        }
        finally { if (!_lifetime.IsCancellationRequested) SetBusy(false); }
    }

    private void SetBusy(bool busy) { _busy = busy; _refresh.Enabled = !busy; UpdateSelection(); }
    private void Log(string message) => _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    private void OpenCodex(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception error) { Log($"打开 Codex 失败：{error.Message}"); }
    }
    private void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void ExitApplication() { _exiting = true; Close(); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !_exiting) { e.Cancel = true; Hide(); return; }
        _timer.Stop();
        _lifetime.Cancel();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    private async Task DisconnectAsync()
    {
        if (_manager is not null) { await _manager.DisposeAsync(); _manager = null; }
        if (_client is not null)
        {
            try { await _client.DisposeAsync(); }
            catch (WebSocketException) { }
            _client = null;
        }
        _connectionLifetime?.Dispose();
        _connectionLifetime = null;
    }

    public async Task StopAsync()
    {
        _lifetime.Cancel();
        await DisconnectAsync();
        _lifetime.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _tray.Dispose(); _trayMenu.Dispose(); _applicationIcon.Dispose(); }
        base.Dispose(disposing);
    }
}
