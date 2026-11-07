using Microsoft.EntityFrameworkCore;

public sealed class RecoverySchedulingTests : ExecutionTest
{
    [Fact]
    public async Task ConcurrentRecoveryWorkersUseOneLookupSlot()
    {
        DateTime due = await PrepareAsync();
        using var barrier = new Barrier(2);
        var reads = new DueAttemptBarrierInterceptor(barrier);
        var provider = new SimulatedMailProvider();
        var first = new ReplyRecovery(InterceptedStore(reads), provider);
        var second = new ReplyRecovery(InterceptedStore(reads), provider);

        await Task.WhenAll(Task.Run(() => first.ReconcileOnceAsync(Context, due, default)),
            Task.Run(() => second.ReconcileOnceAsync(Context, due, default)));

        Assert.Equal(1, provider.Lookups);
        Assert.Equal(0, provider.Submissions);
        var audit = await AuditAsync();
        Assert.Equal(new[] { "AttemptPrepared", "ReconciliationStarted", "SubmissionOutcomeUnknown" },
            audit.Select(e => e.EventType));
        Assert.Equal(new long[] { 1, 2, 3 }, audit.Select(e => e.Sequence));
        await using var db = Store.Open();
        Assert.Equal(due.AddSeconds(30), (await db.Attempts.SingleAsync()).RecoverAfterUtc);
    }

    [Fact]
    public async Task ResolvedOperationRejectsStaleReconciliation()
    {
        DateTime due = await PrepareAsync();
        var candidate = await Store.ReadRecoverableAsync(Context, due, default);
        await Store.RecordObservationAsync(Context, candidate!.AttemptId,
            new(SubmissionOutcome.Accepted, "receipt-resolved", "provider-event/resolved"), default);

        Assert.False(await Store.TryBeginReconciliationAsync(Context, candidate.AttemptId, due, default));
        Assert.Equal(DispatchState.Succeeded, (await Store.ReadStatusAsync(Context, default)).State);
        Assert.Equal(new[] { "AttemptPrepared", "ProviderAcceptanceConfirmed" },
            (await AuditAsync()).Select(e => e.EventType));
        await using var db = Store.Open();
        Assert.Equal(due, (await db.Attempts.SingleAsync()).RecoverAfterUtc);
        Assert.Equal(2, (await db.Operations.SingleAsync()).Version);
    }

    [Fact]
    public async Task UnknownLookupWaitsForNextScheduledCheck()
    {
        DateTime due = await PrepareAsync();
        var provider = new SimulatedMailProvider();
        var recovery = new ReplyRecovery(Store, provider);
        await recovery.ReconcileOnceAsync(Context, due, default);
        await recovery.ReconcileOnceAsync(Context, due, default);
        await recovery.ReconcileOnceAsync(Context, due.AddSeconds(30).AddTicks(-1), default);
        Assert.Equal(1, provider.Lookups);
        Assert.Equal(3, (await AuditAsync()).Count);

        await recovery.ReconcileOnceAsync(Context, due.AddSeconds(30), default);
        Assert.Equal(2, provider.Lookups);
        Assert.Equal(0, provider.Submissions);
        Assert.Equal(DispatchState.OutcomeUnknown, (await Store.ReadStatusAsync(Context, default)).State);
        Assert.Equal(5, (await AuditAsync()).Count);
        await using var db = Store.Open();
        Assert.Equal(due.AddSeconds(60), (await db.Attempts.SingleAsync()).RecoverAfterUtc);
    }

    [Fact]
    public async Task FailedLookupRetainsNextScheduledCheck()
    {
        DateTime due = await PrepareAsync();
        var provider = new UnavailableLookupProvider();
        var recovery = new ReplyRecovery(Store, provider);
        await Assert.ThrowsAsync<IOException>(() => recovery.ReconcileOnceAsync(Context, due, default));
        await recovery.ReconcileOnceAsync(Context, due.AddSeconds(1), default);

        Assert.Equal(1, provider.Lookups);
        Assert.Equal(DispatchState.InProgress, (await Store.ReadStatusAsync(Context, default)).State);
        Assert.Equal("ReconciliationStarted", (await AuditAsync())[^1].EventType);
        await using var db = Store.Open();
        Assert.Equal(due.AddSeconds(30), (await db.Attempts.SingleAsync()).RecoverAfterUtc);
    }

    [Fact]
    public async Task FailedReconciliationAuditRollsBackSchedule()
    {
        DateTime due = await PrepareAsync();
        var faulty = InterceptedStore(new DuplicateAuditSequenceInterceptor());
        Guid attemptId = await AttemptIdAsync();
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            faulty.TryBeginReconciliationAsync(Context, attemptId, due, default));

        Assert.Equal("AttemptPrepared", Assert.Single(await AuditAsync()).EventType);
        await using var db = Store.Open();
        Assert.Equal(due, (await db.Attempts.SingleAsync()).RecoverAfterUtc);
        Assert.Equal(1, (await db.Operations.SingleAsync()).Version);
        Assert.True(await Store.TryBeginReconciliationAsync(Context, attemptId, due, default));
    }

    private async Task<DateTime> PrepareAsync()
    {
        await Store.TryPrepareAttemptAsync(Context, Guid.NewGuid(), default);
        await using var db = Store.Open();
        return (await db.Attempts.SingleAsync()).RecoverAfterUtc;
    }

    private sealed class UnavailableLookupProvider : IReplyProvider
    {
        public string AccountId => "test-mail-account";
        public string Endpoint => "https://mail.example.test/v1";
        public int Lookups { get; private set; }
        public Task<SubmissionObservation> SubmitAsync(ReplyCommand command, string key, CancellationToken token) =>
            throw new InvalidOperationException("Recovery must never submit.");
        public Task<SubmissionObservation> LookupAsync(string key, CancellationToken token)
        {
            Lookups++;
            return Task.FromException<SubmissionObservation>(new IOException("Simulated lookup outage."));
        }
    }
}
