using CodexReset.Core;

namespace CodexReset.Core.Tests;

public class AutoContinueTests
{
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
        engine.BeginRecovery();
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
        engine.BeginRecovery();
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
        var settings = new AppSettings(true, "继续", ["failed", "successful"]);

        Assert.Equal(0, await monitor.TickAsync(client, settings));
        server.UsedPercent = 20;
        Assert.Equal(1, await monitor.TickAsync(client, settings));
        Assert.Equal(1, await monitor.TickAsync(client, settings));
        Assert.Equal(0, await monitor.TickAsync(client, settings));
        Assert.Equal(["继续"], server.Messages["failed"]);
        Assert.Equal(["继续"], server.Messages["successful"]);
    }

    [Fact]
    public async Task SameThreadCanContinueAfterAnotherRecovery()
    {
        await using var server = new AppServerStub();
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        var settings = new AppSettings(true, "继续", ["selected"]);

        await monitor.TickAsync(client, settings);
        server.UsedPercent = 20;
        Assert.Equal(1, await monitor.TickAsync(client, settings));
        server.UsedPercent = 100;
        Assert.Equal(0, await monitor.TickAsync(client, settings));
        server.UsedPercent = 20;
        Assert.Equal(1, await monitor.TickAsync(client, settings));
        Assert.Equal(["继续", "继续"], server.Messages["selected"]);
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

        await monitor.TickAsync(client, settings);
        server.UsedPercent = 20;
        Assert.Equal(1, await monitor.TickAsync(client, settings));
        settings.SelectedThreadIds.Remove("failed");
        Assert.Equal(0, await monitor.TickAsync(client, settings));
        Assert.False(server.Messages.ContainsKey("failed"));
        Assert.Equal(["继续"], server.Messages["successful"]);
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
    public async Task WeeklyLimitBlocksContinuationUntilBothWindowsAreAvailable()
    {
        await using var server = new AppServerStub { SecondaryUsedPercent = 100 };
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        var settings = new AppSettings(true, "继续", ["selected"]);

        await monitor.TickAsync(client, settings);
        server.UsedPercent = 20;
        Assert.Equal(0, await monitor.TickAsync(client, settings));
        server.SecondaryUsedPercent = 20;
        Assert.Equal(1, await monitor.TickAsync(client, settings));
        Assert.Equal(["继续"], server.Messages["selected"]);
    }

    [Fact]
    public async Task LateAcknowledgementDoesNotConsumeNextRecovery()
    {
        await using var server = new AppServerStub { StartDelay = TimeSpan.FromMilliseconds(300) };
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        var settings = new AppSettings(true, "继续", ["selected"]);
        await monitor.TickAsync(client, settings);
        server.UsedPercent = 20;
        using var timeout = new CancellationTokenSource();
        var first = monitor.TickAsync(client, settings, timeout.Token);
        await server.StartReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        timeout.Cancel();
        Assert.Equal(0, await first);

        server.UsedPercent = 100;
        Assert.Equal(0, await monitor.TickAsync(client, settings));
        server.UsedPercent = 20;
        Assert.Equal(1, await monitor.TickAsync(client, settings));
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
        await monitor.TickAsync(client, settings);
        server.UsedPercent = 20;
        using var timeout = new CancellationTokenSource();
        var first = monitor.TickAsync(client, settings, timeout.Token);
        await server.StartReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        timeout.Cancel();
        Assert.Equal(0, await first);

        Assert.Equal(1, await monitor.ContinueSelectedAsync(client, settings));
        Assert.Equal(0, await monitor.TickAsync(client, settings));
        Assert.Equal(["继续"], server.Messages["selected"]);
    }

    [Fact]
    public async Task DisablingAutoContinueCancelsUnsentRetries()
    {
        await using var server = new AppServerStub();
        server.FailResumeOnce.Add("selected");
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        var settings = new AppSettings(true, "继续", ["selected"]);
        await monitor.TickAsync(client, settings);
        server.UsedPercent = 20;
        Assert.Equal(0, await monitor.TickAsync(client, settings));

        Assert.Equal(0, await monitor.TickAsync(client, settings with { AutoContinue = false }));
        Assert.Equal(0, await monitor.TickAsync(client, settings));
        Assert.False(server.Messages.ContainsKey("selected"));
    }

    [Fact]
    public async Task ManualContinuationBeforeRecoveryPollIsNotRepeatedAutomatically()
    {
        await using var server = new AppServerStub();
        await using var client = new AppServerClient();
        await client.ConnectAsync(server.Address);
        await client.InitializeAsync();
        var monitor = new AutoContinueMonitor();
        var settings = new AppSettings(true, "继续", ["selected"]);
        await monitor.TickAsync(client, settings);
        server.UsedPercent = 20;

        Assert.Equal(1, await monitor.ContinueSelectedAsync(client, settings));
        Assert.Equal(0, await monitor.TickAsync(client, settings));
        Assert.Equal(["继续"], server.Messages["selected"]);
    }
}
