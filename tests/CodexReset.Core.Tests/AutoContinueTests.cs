using CodexReset.Core;
using System.Text.Json;

namespace CodexReset.Core.Tests;

public class AutoContinueTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnotherQuotaFailureWaitsForRecoveryOrAccountChange(bool desktop)
    {
        await using var server = new AppServerStub();
        if (desktop) server.ActiveWriterThreads.Add("selected");
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor(wakeQueuedThread: _ => { });
        var settings = new AppSettings(true, "继续", ["selected"]);
        CodexThread[] paused = [Paused("selected")];
        Assert.Equal(1, await monitor.TickAsync(client, () => settings, () => paused));
        paused = [Paused("selected", "failed-2")];

        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        server.AccountResponse = new { account = new { type = "chatgpt", email = "account-b@synthetic.invalid", planType = "pro" }, requiresOpenaiAuth = true };
        Assert.Equal(1, await monitor.TickAsync(client, () => settings, () => paused));
        paused = [Paused("selected", "failed-3")];
        server.RateLimitsResponse = new { ordinaryUsageAllowed = (bool?)null, rateLimits = new { } };
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        server.RateLimitsResponse = new { ordinaryUsageAllowed = true, rateLimits = new { } };
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Equal(["继续", "继续"], (desktop ? server.QueuedMessages : server.Messages)["selected"]);

        server.RateLimitsResponse = new { ordinaryUsageAllowed = false, rateLimits = new { } };
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        server.RateLimitsResponse = new { ordinaryUsageAllowed = true, rateLimits = new { } };
        Assert.Equal(1, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Equal(["继续", "继续", "继续"], (desktop ? server.QueuedMessages : server.Messages)["selected"]);
    }

    [Fact]
    public async Task TemporarilyLockedCredentialsAreUnknownUntilReadableAgain()
    {
        var home = Path.Combine(Path.GetTempPath(), "codex-reset-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var path = Path.Combine(home, "auth.json");
            File.WriteAllText(path, "synthetic-account");
            await using var client = new AppServerClient(home);
            Assert.True(client.IsAccountCurrent);
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.False(client.IsAccountCurrent);
            Assert.True(client.IsAccountCurrent);
        }
        finally { Directory.Delete(home, true); }
    }

    [Theory]
    [InlineData("auth.json")]
    [InlineData("config.toml")]
    public async Task CredentialChangeDiscardsOldQuotaAndUsesAReconnectedClient(string file)
    {
        var home = Path.Combine(Path.GetTempPath(), "codex-reset-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var path = Path.Combine(home, file);
            File.WriteAllText(path, "synthetic-account-a");
            await using var server = new AppServerStub { RateLimitReadDelay = TimeSpan.FromMilliseconds(300) };
            await using var client = new AppServerClient(home);
            await client.ConnectAsync(server.Address);
            await client.InitializeAsync();
            var logs = new List<string>();
            var monitor = new AutoContinueMonitor((_, message) => logs.Add(message));
            var settings = new AppSettings(true, "继续", ["selected"]);
            var pending = monitor.TickAsync(client, () => settings, () => [Paused("selected")]);
            await server.RateLimitReadReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            File.WriteAllText(path, "synthetic-account-b");

            Assert.Equal(0, await pending);
            Assert.False(client.IsAccountCurrent);
            Assert.Empty(server.Messages);
            Assert.False(server.ResumeReceived.Task.IsCompleted);
            Assert.Contains(logs, message => message.Contains("已丢弃旧额度结果"));

            await using var replacement = new AppServerStub();
            await using var reconnected = new AppServerClient(home);
            await reconnected.ConnectAsync(replacement.Address);
            await reconnected.InitializeAsync();
            Assert.True(reconnected.IsAccountCurrent);
            Assert.Equal(1, await monitor.TickAsync(reconnected, () => settings, () => [Paused("selected")]));
            Assert.Equal(0, await monitor.TickAsync(reconnected, () => settings, () => [Paused("selected")]));
            Assert.Equal(["继续"], replacement.Messages["selected"]);
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public async Task AccountMustBeConfirmedAgainBeforeUsingQuota()
    {
        await using var server = new AppServerStub { FailAccountRead = true };
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        var settings = new AppSettings(true, "继续", ["selected"]);
        CodexThread[] paused = [Paused("selected")];

        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        server.FailAccountRead = false;
        server.AccountResponse = new { account = (object?)null, requiresOpenaiAuth = true };
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        server.AccountResponse = new { account = new { type = "chatgpt", email = "account-b@synthetic.invalid", planType = "pro" }, requiresOpenaiAuth = true };
        server.RateLimitsResponse = new { rateLimits = new { primary = new { usedPercent = 100 } } };
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Empty(server.Messages);

        server.RateLimitsResponse = new { ordinaryUsageAllowed = true, rateLimits = new { } };
        Assert.Equal(1, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Equal(["继续"], server.Messages["selected"]);
        paused = [Paused("selected", "failed-2")];
        server.AccountResponse = new { account = (object?)null, requiresOpenaiAuth = true };
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Equal(["继续"], server.Messages["selected"]);
    }

    [Fact]
    public async Task QuotaFromAnotherWorkspaceIsNotUsed()
    {
        await using var server = new AppServerStub
        {
            AccountResponse = new { account = new { type = "chatgpt" }, workspaceRouting = new { chatgptAccountId = "account-b" } },
            RateLimitsResponse = new { accountId = "account-a", ordinaryUsageAllowed = true, rateLimits = new { } }
        };
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();

        Assert.Equal(0, await monitor.TickAsync(client, () => new(true, "继续", ["selected"]), () => [Paused("selected")]));
        Assert.Empty(server.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuotaExhaustionDoesNotLoopAcrossNewFailures(bool desktop)
    {
        await using var server = new AppServerStub
        {
            RateLimitsResponse = new { rateLimits = new { primary = new { usedPercent = 100 } } }
        };
        if (desktop) server.ActiveWriterThreads.Add("selected");
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor(wakeQueuedThread: _ => { });
        var settings = new AppSettings(true, "继续", ["selected"]);
        CodexThread[] paused = [Paused("selected")];

        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        paused = [Paused("selected", "failed-2")];
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Empty(server.Messages);
        Assert.Empty(server.QueuedMessages);

        server.RateLimitsResponse = new { rateLimits = new { primary = new { usedPercent = 20 } } };
        Assert.Equal(1, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        server.RateLimitsResponse = new { rateLimits = new { primary = new { usedPercent = 100 } } };
        paused = [Paused("selected", "failed-3")];
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Equal(["继续"], (desktop ? server.QueuedMessages : server.Messages)["selected"]);

        server.RateLimitsResponse = new { rateLimits = new { primary = new { usedPercent = 20 } } };
        Assert.Equal(1, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Equal(["继续", "继续"], (desktop ? server.QueuedMessages : server.Messages)["selected"]);
    }

    [Theory]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":10},\"secondary\":{\"usedPercent\":100}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":10},\"rateLimitReachedType\":\"workspace_owner_credits_depleted\"}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":10},\"spendControlReached\":true}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":-1}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":\"10\"}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{},\"secondary\":{\"usedPercent\":10}}}")]
    [InlineData("{\"rateLimits\":{}}")]
    [InlineData("{\"rateLimits\":null}")]
    [InlineData("{\"ordinaryUsageAllowed\":false,\"rateLimits\":{\"primary\":{\"usedPercent\":10}}}")]
    [InlineData("{\"ordinaryUsageAllowed\":null,\"rateLimits\":{\"primary\":{\"usedPercent\":10}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":10}},\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":100}}}}")]
    public async Task LimitedOrUnknownQuotaNeverStartsAutomaticContinuation(string response)
    {
        using var document = JsonDocument.Parse(response);
        await using var server = new AppServerStub { RateLimitsResponse = document.RootElement.Clone() };
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();

        Assert.Equal(0, await monitor.TickAsync(client, () => new(true, "继续", ["selected"]), () => [Paused("selected")]));
        Assert.Empty(server.Messages);
        Assert.Empty(server.QueuedMessages);
    }

    [Fact]
    public async Task BackendUsagePermissionAllowsContinuationWithoutPercentages()
    {
        await using var server = new AppServerStub { RateLimitsResponse = new { ordinaryUsageAllowed = true, rateLimits = new { } } };
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();

        Assert.Equal(1, await monitor.TickAsync(client, () => new(true, "继续", ["selected"]), () => [Paused("selected")]));
        Assert.Equal(["继续"], server.Messages["selected"]);
    }

    [Fact]
    public async Task FailedQuotaReadWaitsForTheNextSuccessfulCheck()
    {
        await using var server = new AppServerStub { FailRateLimitRead = true };
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var logs = new List<string>();
        var monitor = new AutoContinueMonitor((_, message) => logs.Add(message));
        var settings = new AppSettings(true, "继续", ["selected"]);

        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => [Paused("selected")]));
        Assert.Empty(server.Messages);
        Assert.Contains(logs, message => message.Contains("额度") && message.Contains("路由后台不提供账号接口"));
        server.FailRateLimitRead = false;
        Assert.Equal(1, await monitor.TickAsync(client, () => settings, () => [Paused("selected")]));
        Assert.Equal(["继续"], server.Messages["selected"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RechecksQuotaAfterResumeBeforeSubmitting(bool desktop)
    {
        await using var server = new AppServerStub { ResumeDelay = TimeSpan.FromMilliseconds(300) };
        if (desktop) server.ActiveWriterThreads.Add("selected");
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor(wakeQueuedThread: _ => { });
        var pending = monitor.TickAsync(client, () => new(true, "继续", ["selected"]), () => [Paused("selected")]);
        await server.ResumeReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        server.RateLimitsResponse = new { rateLimits = new { primary = new { usedPercent = 100 } } };

        Assert.Equal(0, await pending);
        Assert.Empty(server.Messages);
        Assert.Empty(server.QueuedMessages);
    }

    [Fact]
    public async Task RechecksQuotaBeforeEachThread()
    {
        await using var server = new AppServerStub { StartDelay = TimeSpan.FromMilliseconds(300) };
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        var settings = new AppSettings(true, "继续", ["first", "second"]);
        CodexThread[] paused = [Paused("first"), Paused("second")];
        var pending = monitor.TickAsync(client, () => settings, () => paused);
        await server.StartReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        server.RateLimitsResponse = new { rateLimits = new { primary = new { usedPercent = 100 } } };

        Assert.Equal(1, await pending);
        Assert.False(server.Messages.ContainsKey("second"));
        server.RateLimitsResponse = new { rateLimits = new { primary = new { usedPercent = 20 } } };
        Assert.Equal(1, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Equal(["继续"], server.Messages["first"]);
        Assert.Equal(["继续"], server.Messages["second"]);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task RechecksEligibilityAfterResumeBeforeSubmitting(bool desktop, int change)
    {
        await using var server = new AppServerStub { ResumeDelay = TimeSpan.FromMilliseconds(300) };
        if (desktop) server.ActiveWriterThreads.Add("selected");
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor(wakeQueuedThread: _ => { });
        var settings = new AppSettings(true, "继续", ["selected"]);
        var paused = new List<CodexThread> { Paused("selected") };
        var pending = monitor.TickAsync(client, () => settings, () => paused);
        await server.ResumeReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (change == 0) settings.SelectedThreadIds.Clear();
        else if (change == 1) settings = settings with { AutoContinue = false };
        else paused.Clear();

        Assert.Equal(0, await pending);
        Assert.Empty(server.Messages);
        Assert.Empty(server.QueuedMessages);
    }

    [Fact]
    public async Task DisablingAutomaticContinuationStopsTheUnsentThreads()
    {
        await using var server = new AppServerStub { StartDelay = TimeSpan.FromMilliseconds(300) };
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        var settings = new AppSettings(true, "继续", ["first", "second"]);
        var pending = monitor.TickAsync(client, () => settings, () => [Paused("first"), Paused("second")]);
        await server.StartReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        settings = settings with { AutoContinue = false };

        Assert.Equal(1, await pending);
        Assert.False(server.Messages.ContainsKey("second"));
    }

    [Fact]
    public async Task ManualContinuationReadsTheLatestFailureBeforeAutomaticPolling()
    {
        await using var server = new AppServerStub();
        server.ActiveWriterThreads.Add("selected");
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor(wakeQueuedThread: _ => { });
        var settings = new AppSettings(true, "继续", ["selected"]);
        CodexThread[] paused = [Paused("selected")];
        Assert.Equal(1, await monitor.TickAsync(client, () => settings, () => paused));
        server.QueueEntries["selected"].Clear();
        paused = [Paused("selected", "failed-2")];

        Assert.Equal(1, await monitor.ContinueSelectedAsync(client, () => settings, () => paused));
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Equal(["继续", "继续"], server.QueuedMessages["selected"]);
        Assert.Single(server.QueueEntries["selected"]);
    }

    [Fact]
    public async Task DetectsDisconnectedTransportAndContinuesThroughANewClient()
    {
        await using var server = new AppServerStub();
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        Assert.True(client.IsConnected);
        server.StopConnection();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (client.IsConnected) await Task.Delay(10, timeout.Token);
        Assert.False(client.IsConnected);

        await using var replacement = new AppServerStub();
        await using var reconnected = new AppServerClient();
        await reconnected.ConnectAsync(replacement.Address);
        await reconnected.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        Assert.Equal(1, await monitor.TickAsync(reconnected, () => new(true, "继续", ["selected"]), () => [Paused("selected")]));
        Assert.Equal(["继续"], replacement.Messages["selected"]);
        Assert.True(replacement.AccountRequests > 0);
    }

    [Fact]
    public async Task RechecksSelectedThreadsBeforeEachSend()
    {
        await using var server = new AppServerStub { StartDelay = TimeSpan.FromMilliseconds(300) };
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        var settings = new AppSettings(true, "继续", ["first", "second"]);
        var pending = monitor.TickAsync(client, () => settings, () => [Paused("first"), Paused("second")]);
        await server.StartReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        settings.SelectedThreadIds.Remove("second");

        Assert.Equal(1, await pending);
        Assert.False(server.Messages.ContainsKey("second"));
    }

    [Fact]
    public async Task RechecksPausedThreadsBeforeEachSend()
    {
        await using var server = new AppServerStub { StartDelay = TimeSpan.FromMilliseconds(300) };
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        var settings = new AppSettings(true, "继续", ["first", "second"]);
        var paused = new List<CodexThread> { Paused("first"), Paused("second") };
        var pending = monitor.TickAsync(client, () => settings, () => paused);
        await server.StartReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        paused.RemoveAt(1);

        Assert.Equal(1, await pending);
        Assert.False(server.Messages.ContainsKey("second"));
    }

    [Fact]
    public async Task ManualContinuationDoesNotRequireAccountMonitoring()
    {
        await using var server = new AppServerStub();
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();

        Assert.Equal(1, await monitor.ContinueSelectedAsync(client, () => new(false, "继续", ["selected"]), () => []));
        Assert.Equal(["继续"], server.Messages["selected"]);
        Assert.Equal(0, server.AccountRequests);
    }

    [Fact]
    public async Task ActiveWriterUsesDesktopQueueWithoutRepeatingTheMessage()
    {
        await using var server = new AppServerStub();
        server.ActiveWriterThreads.Add("selected");
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var wakes = new List<string>();
        var engine = new AutoContinueEngine(wakes.Add);

        Assert.True(await engine.ContinueAsync(client, "selected", "继续"));
        Assert.True(await engine.ContinueAsync(client, "selected", "继续"));
        Assert.Equal(["继续"], server.QueuedMessages["selected"]);
        Assert.Equal(["selected"], wakes);
        Assert.False(server.Messages.ContainsKey("selected"));
    }

    [Fact]
    public async Task RejectedDesktopQueueCanBeRetried()
    {
        await using var server = new AppServerStub();
        server.ActiveWriterThreads.Add("selected");
        server.FailQueueOnce.Add("selected");
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var wakes = new List<string>();
        var engine = new AutoContinueEngine(wakes.Add);

        Assert.False(await engine.ContinueAsync(client, "selected", "继续"));
        Assert.Empty(wakes);
        Assert.True(await engine.ContinueAsync(client, "selected", "继续"));
        Assert.Equal(["继续"], server.QueuedMessages["selected"]);
        Assert.Equal(["selected"], wakes);
    }

    [Fact]
    public async Task TimedOutDesktopQueueWaitsForTheOriginalAcknowledgement()
    {
        await using var server = new AppServerStub { QueueDelay = TimeSpan.FromMilliseconds(300) };
        server.ActiveWriterThreads.Add("selected");
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var wakes = new List<string>();
        var engine = new AutoContinueEngine(wakes.Add);
        using var timeout = new CancellationTokenSource();

        var first = engine.ContinueAsync(client, "selected", "继续", timeout.Token);
        var queued = await Task.WhenAny(server.QueueReceived.Task, first).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(server.QueueReceived.Task, queued);
        timeout.Cancel();
        Assert.False(await first);
        Assert.True(await engine.ContinueAsync(client, "selected", "继续"));
        Assert.Equal(["继续"], server.QueuedMessages["selected"]);
        Assert.Equal(["selected"], wakes);
    }

    [Fact]
    public async Task FailedDesktopWakeRetriesWakeWithoutEnqueuingAgain()
    {
        await using var server = new AppServerStub();
        server.ActiveWriterThreads.Add("selected");
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var attempts = 0;
        var engine = new AutoContinueEngine(_ =>
        {
            if (++attempts == 1) throw new InvalidOperationException("测试唤醒暂时失败");
        });

        Assert.False(await engine.ContinueAsync(client, "selected", "继续"));
        Assert.True(await engine.ContinueAsync(client, "selected", "继续"));
        Assert.Equal(["继续"], server.QueuedMessages["selected"]);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NextRecoveryReusesOnlyAnUnconsumedDesktopQueueMessage(bool consumed)
    {
        await using var server = new AppServerStub { QueuePageSize = 1 };
        server.ActiveWriterThreads.Add("selected");
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var wakes = 0;
        var engine = new AutoContinueEngine(_ =>
        {
            if (++wakes == 1) throw new InvalidOperationException("测试首次唤醒失败");
        });

        Assert.False(await engine.ContinueAsync(client, "selected", "继续"));
        var queuedInput = server.QueueEntries["selected"][0].Input;
        if (consumed) server.QueueEntries["selected"].Clear();
        server.QueueEntries["selected"].Insert(0, ("unrelated-message", queuedInput));
        engine.BeginContinuation("selected");
        Assert.True(await engine.ContinueAsync(client, "selected", "继续"));
        Assert.Equal(consumed ? 2 : 1, server.QueuedMessages["selected"].Count);
        Assert.Equal(2, wakes);
    }

    [Fact]
    public async Task FailedCrossRecoveryQueueReadPreservesTheAcceptedMessage()
    {
        await using var server = new AppServerStub();
        server.ActiveWriterThreads.Add("selected");
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var wakes = 0;
        var engine = new AutoContinueEngine(_ =>
        {
            if (++wakes == 1) throw new InvalidOperationException("测试首次唤醒失败");
        });
        Assert.False(await engine.ContinueAsync(client, "selected", "继续"));
        engine.BeginContinuation("selected");
        server.FailQueueListOnce.Add("selected");

        Assert.False(await engine.ContinueAsync(client, "selected", "继续"));
        Assert.True(await engine.ContinueAsync(client, "selected", "继续"));
        Assert.Equal(["继续"], server.QueuedMessages["selected"]);
        Assert.Equal(2, wakes);
    }

    [Fact]
    public async Task FailedThreadIsRetriedWithoutRepeatingSuccessfulThread()
    {
        await using var server = new AppServerStub();
        server.FailResumeOnce.Add("failed");
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        var settings = new AppSettings(true, "继续", ["failed", "successful", "idle"]);
        CodexThread[] paused = [Paused("failed"), Paused("successful"), Paused("not-selected")];

        Assert.Equal(1, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Equal(1, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Equal(["继续"], server.Messages["failed"]);
        Assert.Equal(["继续"], server.Messages["successful"]);
        Assert.False(server.Messages.ContainsKey("idle"));
        Assert.False(server.Messages.ContainsKey("not-selected"));
        Assert.True(server.AccountRequests > 0);
    }

    [Fact]
    public async Task NewFailureOnlyRestartsItsOwnThread()
    {
        await using var server = new AppServerStub();
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        var settings = new AppSettings(true, "继续", ["first", "second"]);

        Assert.Equal(2, await monitor.TickAsync(client, () => settings, () => [Paused("first"), Paused("second")]));
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => [Paused("first"), Paused("second")]));
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => [Paused("first", "failed-2"), Paused("second")]));
        server.RateLimitsResponse = new { rateLimits = new { primary = new { usedPercent = 100 } } };
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => [Paused("first", "failed-2"), Paused("second")]));
        server.RateLimitsResponse = new { rateLimits = new { primary = new { usedPercent = 20 } } };
        Assert.Equal(1, await monitor.TickAsync(client, () => settings, () => [Paused("first", "failed-2"), Paused("second")]));
        Assert.Equal(["继续", "继续"], server.Messages["first"]);
        Assert.Equal(["继续"], server.Messages["second"]);
    }

    [Fact]
    public async Task DeselectedFailureIsNotRetried()
    {
        await using var server = new AppServerStub();
        server.FailResumeOnce.Add("failed");
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        var settings = new AppSettings(true, "继续", ["failed", "successful"]);
        CodexThread[] paused = [Paused("failed"), Paused("successful")];

        Assert.Equal(1, await monitor.TickAsync(client, () => settings, () => paused));
        settings.SelectedThreadIds.Remove("failed");
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.False(server.Messages.ContainsKey("failed"));
        Assert.Equal(["继续"], server.Messages["successful"]);
    }

    [Fact]
    public async Task ResumedThreadIsNoLongerRetried()
    {
        await using var server = new AppServerStub();
        server.FailResumeOnce.Add("selected");
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        var settings = new AppSettings(true, "继续", ["selected"]);

        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => [Paused("selected")]));
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => []));
        Assert.Empty(server.Messages);
    }

    [Fact]
    public async Task TimedOutStartWaitsForOriginalAcknowledgementWithoutResending()
    {
        await using var server = new AppServerStub { StartDelay = TimeSpan.FromMilliseconds(300) };
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var engine = new AutoContinueEngine();
        using var timeout = new CancellationTokenSource();

        var first = engine.ContinueAsync(client, "selected", "继续", timeout.Token);
        await server.StartReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        timeout.Cancel();
        Assert.False(await first);
        Assert.True(await engine.ContinueAsync(client, "selected", "继续"));
        Assert.Equal(["继续"], server.Messages["selected"]);
    }

    [Fact]
    public async Task RejectedStartCanBeRetried()
    {
        await using var server = new AppServerStub();
        server.FailStartOnce.Add("selected");
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var engine = new AutoContinueEngine();

        Assert.False(await engine.ContinueAsync(client, "selected", "继续"));
        Assert.True(await engine.ContinueAsync(client, "selected", "继续"));
        Assert.Equal(["继续"], server.Messages["selected"]);
    }

    [Fact]
    public async Task LateAcknowledgementDoesNotConsumeNextFailure()
    {
        await using var server = new AppServerStub { StartDelay = TimeSpan.FromMilliseconds(300) };
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        var settings = new AppSettings(true, "继续", ["selected"]);
        using var timeout = new CancellationTokenSource();
        var first = monitor.TickAsync(client, () => settings, () => [Paused("selected")], timeout.Token);
        await server.StartReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        timeout.Cancel();
        Assert.Equal(0, await first);

        Assert.Equal(1, await monitor.TickAsync(client, () => settings, () => [Paused("selected", "failed-2")]));
        Assert.Equal(["继续", "继续"], server.Messages["selected"]);
    }

    [Fact]
    public async Task ManualRetryConfirmsUnacknowledgedAutomaticStartWithoutRepeatingIt()
    {
        await using var server = new AppServerStub { StartDelay = TimeSpan.FromMilliseconds(300) };
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        var settings = new AppSettings(true, "继续", ["selected"]);
        CodexThread[] paused = [Paused("selected")];
        using var timeout = new CancellationTokenSource();
        var first = monitor.TickAsync(client, () => settings, () => paused, timeout.Token);
        await server.StartReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        timeout.Cancel();
        Assert.Equal(0, await first);

        Assert.Equal(1, await monitor.ContinueSelectedAsync(client, () => settings, () => paused));
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Equal(["继续"], server.Messages["selected"]);
    }

    [Fact]
    public async Task DisabledAutomaticContinuationLeavesManualContinuationAvailable()
    {
        await using var server = new AppServerStub();
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        var settings = new AppSettings(false, "继续", ["selected"]);
        CodexThread[] paused = [Paused("selected")];

        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Empty(server.Messages);
        Assert.Equal(1, await monitor.ContinueSelectedAsync(client, () => settings, () => paused));
        Assert.Equal(0, await monitor.TickAsync(client, () => settings with { AutoContinue = true }, () => paused));
        Assert.Equal(["继续"], server.Messages["selected"]);
    }

    [Fact]
    public async Task ManualContinuationBeforeFirstPollIsNotRepeatedAutomatically()
    {
        await using var server = new AppServerStub();
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        var settings = new AppSettings(true, "继续", ["selected"]);
        CodexThread[] paused = [Paused("selected")];

        Assert.Equal(1, await monitor.ContinueSelectedAsync(client, () => settings, () => paused));
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Equal(1, await monitor.ContinueSelectedAsync(client, () => settings, () => paused));
        Assert.Equal(0, await monitor.TickAsync(client, () => settings, () => paused));
        Assert.Equal(["继续", "继续"], server.Messages["selected"]);
    }

    private static CodexThread Paused(string id, string turnId = "failed-1") => new(id, id, "", turnId, 1);
}
