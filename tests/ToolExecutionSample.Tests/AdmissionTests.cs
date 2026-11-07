using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

public sealed class AdmissionTests : ExecutionTest
{
    [Fact]
    public async Task ConcurrentMatchingAdmissionsShareOneIntent()
    {
        var context = Context with { OperationId = Guid.NewGuid() };
        var barrier = new AdmissionInsertBarrierInterceptor();
        var first = InterceptedStore(barrier);
        var second = InterceptedStore(barrier);

        await Task.WhenAll(Task.Run(() => first.AdmitAsync(context, "reply-7", Command)),
            Task.Run(() => second.AdmitAsync(context, "reply-7", Command)));

        await using var db = Store.Open();
        var operation = Assert.Single(await db.Operations.Where(o => o.OperationId == context.OperationId).ToListAsync());
        Assert.Equal(SqliteReplyOperations.Fingerprint(Command), operation.Fingerprint);
        Assert.Equal(DispatchState.Ready, operation.State);
        Assert.Equal(0, operation.Version);
    }

    [Fact]
    public async Task ConcurrentConflictingAdmissionsRejectLoser()
    {
        var context = Context with { OperationId = Guid.NewGuid() };
        var barrier = new AdmissionInsertBarrierInterceptor();
        var first = InterceptedStore(barrier);
        var second = InterceptedStore(barrier);
        var otherCommand = Command with { Body = "A different reply" };

        var errors = await Task.WhenAll(
            Task.Run(() => Record.ExceptionAsync(() => first.AdmitAsync(context, "reply-7", Command))),
            Task.Run(() => Record.ExceptionAsync(() => second.AdmitAsync(context, "reply-7", otherCommand))));

        Assert.Single(errors, e => e is null);
        var conflict = Assert.IsType<InvalidOperationException>(Assert.Single(errors, e => e is not null));
        Assert.Equal("Dispatch intent conflicts with its admitted command.", conflict.Message);
        await using var db = Store.Open();
        var winner = await db.Operations.SingleAsync(o => o.OperationId == context.OperationId);
        Assert.Equal(errors[0] is null ? Command.Body : otherCommand.Body, winner.Body);
        Assert.Equal(DispatchState.Ready, winner.State);
    }

    [Fact]
    public async Task AdmissionDoesNotHideUnrelatedConstraintViolation()
    {
        // This tenant has no matching winner, even though the provider-key uniqueness check fails.
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            Store.AdmitAsync(Context with { TenantId = "tenant-b" }, "reply-7", Command));
        await using var db = Store.Open();
        Assert.Single(await db.Operations.ToListAsync());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task AdmissionRetriesTransientSqliteLocks(int errorCode)
    {
        var context = Context with { OperationId = Guid.NewGuid() };
        var contention = new AdmissionLockInterceptor(errorCode, failures: 1);
        await InterceptedStore(contention).AdmitAsync(context, "reply-7", Command);

        Assert.Equal(2, contention.Attempts);
        await using var db = Store.Open();
        Assert.Equal(Command.Body, (await db.Operations.SingleAsync(o => o.OperationId == context.OperationId)).Body);
    }

    [Fact]
    public async Task AdmissionStopsAfterBoundedSqliteRetries()
    {
        var context = Context with { OperationId = Guid.NewGuid() };
        var contention = new AdmissionLockInterceptor(5, failures: int.MaxValue);
        var error = await Assert.ThrowsAsync<SqliteException>(() =>
            InterceptedStore(contention).AdmitAsync(context, "reply-7", Command));

        Assert.Equal(5, error.SqliteErrorCode);
        Assert.Equal(3, contention.Attempts);
        await using var db = Store.Open();
        Assert.False(await db.Operations.AnyAsync(o => o.OperationId == context.OperationId));
    }

    private sealed class AdmissionLockInterceptor(int errorCode, int failures) : SaveChangesInterceptor
    {
        public int Attempts { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (++Attempts <= failures) throw new SqliteException("Simulated SQLite contention.", errorCode);
            return ValueTask.FromResult(result);
        }
    }

}
