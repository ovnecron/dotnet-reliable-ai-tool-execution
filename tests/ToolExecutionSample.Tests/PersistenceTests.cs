using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class PersistenceTests : ExecutionTest
{
    [Fact]
    public async Task CompetingWorkersCreateOneAttempt()
    {
        using var barrier = new Barrier(2);
        var reads = new ReadyReadBarrierInterceptor(barrier);
        var firstStore = InterceptedStore(reads);
        var secondStore = InterceptedStore(reads);
        async Task<(PreparedAttempt? Attempt, SqliteException? LockError)> ClaimAsync(SqliteReplyOperations store)
        {
            try { return (await store.TryPrepareAttemptAsync(Context, Guid.NewGuid(), default), null); }
            catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6)
            { return (null, error); }
        }

        var results = await Task.WhenAll(Task.Run(() => ClaimAsync(firstStore)), Task.Run(() => ClaimAsync(secondStore)));

        // The materialization barrier proves both workers used the original Ready/version-0 snapshot.
        Assert.Equal(new long[] { 0, 0 }, reads.Versions.Order());
        var winner = Assert.Single(results, r => r.Attempt is not null).Attempt!;
        var loser = Assert.Single(results, r => r.Attempt is null);
        if (loser.LockError is not null) Assert.Contains(loser.LockError.SqliteErrorCode, new[] { 5, 6 });
        await using var db = Store.Open();
        Assert.Equal(winner.AttemptId, (await db.Attempts.SingleAsync()).AttemptId);
        Assert.Equal("AttemptPrepared", Assert.Single(await AuditAsync()).EventType);
        var operation = await db.Operations.SingleAsync();
        Assert.Equal(DispatchState.InProgress, operation.State);
        Assert.Equal(1, operation.Version);
        Assert.Equal(winner.AttemptId, operation.ActiveAttemptId);
    }

    [Fact]
    public async Task FailedAuditInsertRollsBackClaim()
    {
        var faulty = InterceptedStore(new DuplicateAuditSequenceInterceptor());
        var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
            faulty.TryPrepareAttemptAsync(Context, Guid.NewGuid(), default));
        var sqlite = Assert.IsType<SqliteException>(error.InnerException);
        Assert.Equal(2067, sqlite.SqliteExtendedErrorCode); // SQLITE_CONSTRAINT_UNIQUE

        await using var db = Store.Open();
        var operation = await db.Operations.SingleAsync();
        Assert.Equal(DispatchState.Ready, operation.State);
        Assert.Equal(0, operation.Version);
        Assert.Null(operation.ActiveAttemptId);
        Assert.Empty(await db.Attempts.ToListAsync());
        Assert.Empty(await db.Audit.ToListAsync());
    }

    [Theory]
    [InlineData("body")]
    [InlineData("recipient")]
    [InlineData("revision")]
    [InlineData("requester")]
    public async Task ConflictingAdmissionIsRejected(string changed)
    {
        var command = changed switch {
            "body" => Command with { Body = "Changed" },
            "recipient" => Command with { Recipient = "other@example.net" },
            _ => Command
        };
        var context = changed == "requester" ? Context with { RequesterId = "other-user" } : Context;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.AdmitAsync(context,
            changed == "revision" ? "reply-8" : "ticket-42/reply-7", command));

        await using var db = Store.Open();
        var operation = await db.Operations.SingleAsync();
        Assert.Equal(Command.Body, operation.Body);
        Assert.Equal(Command.Recipient, operation.Recipient);
        Assert.Equal("ticket-42/reply-7", operation.ReplyRevision);
        Assert.Equal(Context.RequesterId, operation.RequesterId);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("admission")]
    public async Task InvalidStoredCommandCannotBeClaimed(string invalid)
    {
        await using (var db = Store.Open())
        {
            var operation = await db.Operations.SingleAsync();
            if (invalid == "payload") operation.Body = "Changed outside admission";
            else operation.AdmissionReference = "";
            await db.SaveChangesAsync();
        }
        var provider = new SimulatedMailProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(provider));
        Assert.Equal(0, provider.Submissions);
        Assert.Equal(DispatchState.Ready, (await Store.ReadStatusAsync(Context, default)).State);
        Assert.Empty(await AuditAsync());
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("evidence")]
    public async Task AcceptanceRequiresReferenceAndEvidence(string missing)
    {
        var attempt = await Store.TryPrepareAttemptAsync(Context, Guid.NewGuid(), default);
        var observation = new SubmissionObservation(SubmissionOutcome.Accepted,
            missing == "reference" ? null : "receipt-1", missing == "evidence" ? null : "evidence-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Store.RecordObservationAsync(Context, attempt!.AttemptId, observation, default));

        Assert.Equal(DispatchState.InProgress, (await Store.ReadStatusAsync(Context, default)).State);
        Assert.Single(await AuditAsync());
    }

    [Fact]
    public async Task FailedObservationCanBeRecovered()
    {
        var provider = new SimulatedMailProvider();
        var faulty = new ObservationFaultOperations(Store,
            InterceptedStore(new DuplicateAuditSequenceInterceptor()));
        await Assert.ThrowsAsync<DbUpdateException>(() => InvokeAsync(provider, store: faulty));

        Assert.Equal(1, provider.Submissions);
        Assert.Equal(DispatchState.InProgress, (await Store.ReadStatusAsync(Context, default)).State);
        Assert.Equal("AttemptPrepared", Assert.Single(await AuditAsync()).EventType);
        await using (var db = Store.Open())
            Assert.Equal("Prepared", (await db.Attempts.SingleAsync()).Observation);

        await ReconcileAsync(provider);
        Assert.Equal(DispatchState.Succeeded, (await Store.ReadStatusAsync(Context, default)).State);
        Assert.Equal(1, provider.Submissions);
        Assert.Equal(1, provider.Lookups);
        Assert.Equal("ProviderAcceptanceReconciled", (await AuditAsync())[^1].EventType);
    }

    [Fact]
    public async Task DatabasePathWithDelimitersRemainsLiteral()
    {
        string path = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "reply;quoted'name.db");
        var store = new SqliteReplyOperations(path);
        await store.InitializeAsync();
        await store.AdmitAsync(Context, "ticket-42/reply-7", Command);

        await using var db = store.Open();
        var connection = new SqliteConnectionStringBuilder(db.Database.GetConnectionString());
        Assert.Equal(path, connection.DataSource);
        Assert.False(connection.Pooling);
        Assert.Equal(5, connection.DefaultTimeout);
        Assert.Equal(true, connection.ForeignKeys);
        Assert.True(File.Exists(path));
        Assert.Equal(Command.Body, (await db.Operations.SingleAsync()).Body);
    }
}
