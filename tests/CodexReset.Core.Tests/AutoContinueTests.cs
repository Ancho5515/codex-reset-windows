using CodexReset.Core;

namespace CodexReset.Core.Tests;

public class AutoContinueTests
{
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
