using System.Text.Json;

namespace CodexReset.Core;

public sealed class AutoContinueMonitor(Action<string, string>? log = null, Action<string>? wakeQueuedThread = null)
{
    private readonly AutoContinueEngine _engine = new(wakeQueuedThread);
    private readonly Dictionary<string, string> _observedTurns = [];
    private readonly Dictionary<string, int> _automaticAttempts = [];
    private string? _quotaAccount;
    private bool _wasLimited;
    private int _quotaGeneration;
    public bool HasActiveContinuations => _engine.HasActiveContinuations;

    public async Task<int> TickAsync(AppServerClient client, Func<AppSettings> readSettings, Func<IReadOnlyList<CodexThread>> readPaused, CancellationToken ct = default)
    {
        if (!readSettings().AutoContinue) return 0;
        var paused = await Task.Run(readPaused, ct);
        return await ContinueAsync(client, readSettings, readPaused, paused.Select(thread => thread.ThreadId).ToArray(), true, ct);
    }

    public Task<int> ContinueSelectedAsync(AppServerClient client, Func<AppSettings> readSettings, Func<IReadOnlyList<CodexThread>> readPaused, CancellationToken ct = default) =>
        ContinueAsync(client, readSettings, readPaused, readSettings().SelectedThreadIds.ToArray(), false, ct);

    private void ObservePausedTurn(CodexThread? thread)
    {
        if (thread?.PausedTurnId is not { } turnId || _observedTurns.GetValueOrDefault(thread.ThreadId) == turnId) return;
        _observedTurns[thread.ThreadId] = turnId;
        _engine.BeginContinuation(thread.ThreadId);
    }

    private async Task<bool> HasAvailableQuotaAsync(AppServerClient client, string id, CancellationToken ct)
    {
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
            request.CancelAfter(TimeSpan.FromSeconds(15));
            if (!client.IsAccountCurrent) throw new InvalidOperationException("登录凭据或路由配置已变化，等待重新连接后台。");
            var account = await client.RequestAsync("account/read", new { refreshToken = false }, request.Token);
            if (!account.TryGetProperty("account", out var current) || current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty("type", out var type) || type.GetString() != "chatgpt")
                throw new InvalidOperationException("未登录 ChatGPT 账号，或无法确认当前账号。");
            var result = await client.RequestAsync("account/rateLimits/read", null, request.Token);
            if (!client.IsAccountCurrent) throw new InvalidOperationException("检查期间登录凭据或路由配置发生变化，已丢弃旧额度结果。");
            if (account.TryGetProperty("workspaceRouting", out var routing) && routing.ValueKind == JsonValueKind.Object &&
                routing.TryGetProperty("chatgptAccountId", out var expected) && expected.ValueKind == JsonValueKind.String &&
                result.TryGetProperty("accountId", out var actual) && actual.ValueKind == JsonValueKind.String &&
                expected.GetString() != actual.GetString())
                throw new InvalidOperationException("额度结果与当前账号不一致。");
            var available = QuotaAvailable(result);
            if (available == false) _wasLimited = true;
            if (available == true)
            {
                var identity = account.GetRawText() + (result.TryGetProperty("accountId", out var accountId) ? accountId.GetRawText() : "");
                if (_wasLimited || _quotaAccount != identity) _quotaGeneration++;
                _wasLimited = false;
                _quotaAccount = identity;
                return true;
            }
            log?.Invoke(id, "额度尚未恢复或尚未确认，已暂停自动续作。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            log?.Invoke(id, $"额度检查失败，已暂停自动续作，下次刷新重试：{error.Message}");
        }
        return false;
    }

    private static bool? QuotaAvailable(JsonElement result)
    {
        // 新版后台已核对当前账号；未知权限不能从百分比或重置时间推断为恢复。
        if (result.TryGetProperty("ordinaryUsageAllowed", out var allowed))
            return allowed.ValueKind == JsonValueKind.True ? true : allowed.ValueKind == JsonValueKind.False ? false : null;
        var limits = result.GetProperty("rateLimits");
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object &&
            buckets.TryGetProperty("codex", out var codex)) limits = codex;
        if (limits.ValueKind != JsonValueKind.Object) return null;
        if (limits.TryGetProperty("rateLimitReachedType", out var reached) && reached.ValueKind != JsonValueKind.Null &&
            (reached.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(reached.GetString()) &&
                !string.Equals(reached.GetString(), "none", StringComparison.OrdinalIgnoreCase))) return false;
        if (limits.TryGetProperty("spendControlReached", out var spend) && spend.ValueKind != JsonValueKind.False)
            return spend.ValueKind == JsonValueKind.True ? false : null;
        var hasWindow = false;
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!limits.TryGetProperty(name, out var window) || window.ValueKind == JsonValueKind.Null) continue;
            if (window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("usedPercent", out var used) ||
                used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) ||
                !double.IsFinite(percent) || percent < 0) return null;
            if (percent >= 100) return false;
            hasWindow = true;
        }
        return hasWindow ? true : null;
    }

    private async Task<int> ContinueAsync(AppServerClient client, Func<AppSettings> readSettings, Func<IReadOnlyList<CodexThread>> readPaused, IEnumerable<string> ids, bool automatic, CancellationToken ct)
    {
        var count = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            // 每条发送前核对当前轮次和选择，前一个对话的等待不能延长旧快照的有效期。
            var paused = await Task.Run(readPaused, ct);
            var settings = readSettings();
            if (!settings.SelectedThreadIds.Contains(id) || automatic && !settings.AutoContinue) continue;
            var thread = paused.FirstOrDefault(thread => thread.ThreadId == id);
            if (automatic && thread is null) continue;
            ObservePausedTurn(thread);
            if (_engine.AlreadyHandled(id))
            {
                if (automatic) continue;
                // 手动重试复用未确认请求，已确认成功的对话允许再次手动续作。
                _engine.BeginContinuation(id);
            }
            if (automatic && !await HasAvailableQuotaAsync(client, id, ct)) break;
            if (automatic && _automaticAttempts.TryGetValue(id, out var generation) && generation == _quotaGeneration)
            {
                log?.Invoke(id, "续作后仍因限额中断，等待账号变化或下一次确认额度恢复。");
                continue;
            }
            if (await _engine.ContinueAsync(client, id, settings.Command, ct, async token =>
            {
                if (automatic && !await HasAvailableQuotaAsync(client, id, token)) return false;
                var latest = (await Task.Run(readPaused, token)).FirstOrDefault(candidate => candidate.ThreadId == id);
                var current = readSettings();
                if (!current.SelectedThreadIds.Contains(id) || automatic && (!current.AutoContinue || latest?.PausedTurnId != thread?.PausedTurnId)) return false;
                // 手动操作也必须关联真正提交时的轮次，防止下一次自动检查重复提交。
                if (!automatic) ObservePausedTurn(latest);
                return true;
            }))
            {
                if (automatic) _automaticAttempts[id] = _quotaGeneration;
                count++;
                log?.Invoke(id, _engine.LastDeliveryMessage ?? "已发送续作指令。");
            }
            else log?.Invoke(id, $"续作未完成：{_engine.LastError}；下次重试时核对原请求。");
        }
        return count;
    }
}
