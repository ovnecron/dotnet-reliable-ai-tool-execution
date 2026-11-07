using System.Diagnostics;
using Microsoft.EntityFrameworkCore;

public sealed class RecoveryTests : ExecutionTest
{
    [Fact]
    public async Task LateUnknownObservationPreservesSuccess()
    {
        await ArrangeReconciledAcceptanceAsync();
        await Store.RecordObservationAsync(Context, await AttemptIdAsync(), new(SubmissionOutcome.Unknown, null), default);

        Assert.Equal(DispatchState.Succeeded, (await Store.ReadStatusAsync(Context, default)).State);
        Assert.Equal("LateUnknownObservation", (await AuditAsync())[^1].EventType);
        await using var db = Store.Open();
        Assert.Equal($"receipt-{Context.OperationId:N}", (await db.Operations.SingleAsync()).ProviderReference);
        Assert.Equal("Accepted", (await db.Attempts.SingleAsync()).Observation);
    }

    [Fact]
    public async Task RepeatedObservationGetsDistinctEvents()
    {
        await ArrangeReconciledAcceptanceAsync();
        var attemptId = await AttemptIdAsync();
        var observation = new SubmissionObservation(SubmissionOutcome.Unknown, null);
        await Store.RecordObservationAsync(Context, attemptId, observation, default);
        await Store.RecordObservationAsync(Context, attemptId, observation, default);

        var late = (await AuditAsync()).Where(e => e.EventType == "LateUnknownObservation").ToList();
        Assert.Equal(2, late.Count);
        Assert.Equal(2, late.Select(e => e.EventId).Distinct().Count());
        Assert.Equal(new long[] { 5, 6 }, late.Select(e => e.Sequence));
        Assert.Equal(DispatchState.Succeeded, (await Store.ReadStatusAsync(Context, default)).State);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    public async Task RecoveryWaitsUntilDueTime(int offsetTicks, int expectedLookups)
    {
        await Store.TryPrepareAttemptAsync(Context, Guid.NewGuid(), default);
        DateTime due;
        await using (var db = Store.Open())
            due = (await db.Attempts.SingleAsync()).RecoverAfterUtc;
        var provider = new SimulatedMailProvider();

        await new ReplyRecovery(Store, provider).ReconcileOnceAsync(Context, due.AddTicks(offsetTicks), default);

        Assert.Equal(expectedLookups, provider.Lookups);
        Assert.Equal(0, provider.Submissions);
        Assert.Equal(expectedLookups == 0 ? DispatchState.InProgress : DispatchState.OutcomeUnknown,
            (await Store.ReadStatusAsync(Context, default)).State);
        Assert.Equal(expectedLookups == 0 ? 1 : 3, (await AuditAsync()).Count);
    }

    [Fact]
    public async Task ResolvedOperationNeedsNoFurtherLookup()
    {
        var provider = new SimulatedMailProvider { LoseResponse = true };
        await InvokeAsync(provider);
        await ReconcileAsync(provider);
        await ReconcileAsync(provider);
        Assert.Equal(1, provider.Lookups);
        Assert.Equal(1, provider.Submissions);
        Assert.Equal(4, (await AuditAsync()).Count);
    }

    [Fact]
    public async Task ProcessExitLeavesRecoverableClaim()
    {
        // Execute the sample's entry point using this test project's runtime/dependency manifests.
        // This launches a separate process, never the test runner itself.
        string testAssembly = typeof(RecoveryTests).Assembly.Location;
        var child = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") {
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"), typeof(ReplyExecutor).Assembly.Location,
            "--crash-after-claim", DatabasePath, Context.OperationId.ToString() })
            child.ArgumentList.Add(argument);
        using var process = Process.Start(child)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        Assert.True(process.ExitCode == 23, $"Exit {process.ExitCode}: {await stderr} {await stdout}");

        Assert.Equal(DispatchState.InProgress, (await Store.ReadStatusAsync(Context, default)).State);
        var provider = new SimulatedMailProvider();
        await ReconcileAsync(provider);
        Assert.Equal(DispatchState.OutcomeUnknown, (await Store.ReadStatusAsync(Context, default)).State);
        Assert.Equal(0, provider.Submissions);
        Assert.Equal(1, provider.Lookups);
        Assert.Equal("provider-lookup/not-found", (await AuditAsync())[^1].EvidenceReference);
    }
}
