using System.Text.Json;

namespace CodexReset.Core;

public sealed class AutoContinueMonitor(Action<string, string>? log = null)
{
    private readonly RecoveryDetector _detector = new();
    private readonly AutoContinueEngine _engine = new();
    private readonly HashSet<string> _pending = [];

    public async Task<int> TickAsync(AppServerClient client, AppSettings settings, CancellationToken ct = default)
    {
        var snapshot = await ReadSnapshotAsync(client, ct);
        var recovered = _detector.Observe(snapshot);
        if (!settings.AutoContinue) { _pending.Clear(); return 0; }
        if (recovered)
        {
            _engine.BeginRecovery();
            _pending.UnionWith(settings.SelectedThreadIds);
        }
        _pending.RemoveWhere(id => !settings.SelectedThreadIds.Contains(id));
        if (RecoveryDetector.IsLimited(snapshot)) return 0;
        return await ContinuePendingAsync(client, settings, ct);
    }

    public async Task<int> ContinueSelectedAsync(AppServerClient client, AppSettings settings, CancellationToken ct = default)
    {
        if (_detector.Observe(await ReadSnapshotAsync(client, ct))) _engine.BeginRecovery();
        // 手动重试与自动续作共享未确认请求，已经确认成功的手动操作可再次执行。
        _engine.BeginManualContinuation();
        _pending.UnionWith(settings.SelectedThreadIds);
        return await ContinuePendingAsync(client, settings, ct);
    }

    private static async Task<RateLimitSnapshot> ReadSnapshotAsync(AppServerClient client, CancellationToken ct)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
        request.CancelAfter(TimeSpan.FromSeconds(15));
        var root = await client.RequestAsync("account/rateLimits/read", null, request.Token);
        var limits = root.GetProperty("rateLimits");
        var reached = limits.TryGetProperty("rateLimitReachedType", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
        return new(UsedPercent(limits, "primary"), reached, Secondary: new(UsedPercent(limits, "secondary")));
    }

    private async Task<int> ContinuePendingAsync(AppServerClient client, AppSettings settings, CancellationToken ct)
    {
        _pending.RemoveWhere(id => !settings.SelectedThreadIds.Contains(id));
        var count = 0;
        foreach (var id in _pending.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (await _engine.ContinueAsync(client, id, settings.Command, ct))
            {
                _pending.Remove(id);
                count++;
                log?.Invoke(id, "已发送续作指令。");
            }
            else log?.Invoke(id, $"续作未完成：{_engine.LastError}；保留待处理，下次刷新重试或核对原请求。");
        }
        return count;
    }

    private static int UsedPercent(JsonElement limits, string name) =>
        limits.TryGetProperty(name, out var window) && window.ValueKind == JsonValueKind.Object && window.TryGetProperty("usedPercent", out var used)
            ? (int)used.GetDouble() : 0;
}
