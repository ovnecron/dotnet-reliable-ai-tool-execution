using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class AuditIntegrityTests : ExecutionTest
{
    [Fact]
    public async Task AuditCannotReferenceAnotherOperationsAttempt()
    {
        await Store.TryPrepareAttemptAsync(Context, Guid.NewGuid(), default);
        var other = Context with { OperationId = Guid.NewGuid() };
        await Store.AdmitAsync(other, "reply-8", Command);
        var otherAttempt = await Store.TryPrepareAttemptAsync(other, Guid.NewGuid(), default);
        var invalidEventId = Guid.NewGuid();
        await using (var db = Store.Open())
        {
            db.Audit.Add(new DispatchAudit {
                EventId = invalidEventId, TenantId = Context.TenantId, OperationId = Context.OperationId,
                AttemptId = otherAttempt!.AttemptId, Sequence = 2, EventType = "WrongOperation"
            });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(787, Assert.IsType<SqliteException>(error.InnerException).SqliteExtendedErrorCode);
        }
        await using var reopened = Store.Open();
        Assert.False(await reopened.Audit.AnyAsync(e => e.EventId == invalidEventId));
        Assert.Equal("AttemptPrepared", Assert.Single(await AuditAsync()).EventType);
        Assert.Equal(1, (await reopened.Operations.SingleAsync(o => o.OperationId == Context.OperationId)).Version);
    }

    [Fact]
    public async Task ReconciliationRejectsAnotherOperationsAttempt()
    {
        await Store.TryPrepareAttemptAsync(Context, Guid.NewGuid(), default);
        var other = Context with { OperationId = Guid.NewGuid() };
        await Store.AdmitAsync(other, "reply-8", Command);
        var otherAttempt = await Store.TryPrepareAttemptAsync(other, Guid.NewGuid(), default);

        bool permitted = await Store.TryBeginReconciliationAsync(Context, otherAttempt!.AttemptId,
            DateTime.UtcNow.AddMinutes(1), default);

        Assert.False(permitted);
        Assert.Single(await AuditAsync());
        await using var db = Store.Open();
        Assert.Equal(1, (await db.Operations.SingleAsync(o => o.OperationId == Context.OperationId)).Version);
    }

    [Fact]
    public async Task ReconciliationRejectsInactiveAttempt()
    {
        await Store.TryPrepareAttemptAsync(Context, Guid.NewGuid(), default);
        Guid inactive = Guid.NewGuid();
        await using (var db = Store.Open())
        {
            db.Attempts.Add(new DispatchAttempt { TenantId = Context.TenantId,
                OperationId = Context.OperationId, AttemptId = inactive, RecoverAfterUtc = DateTime.UtcNow.AddDays(-1) });
            await db.SaveChangesAsync();
        }

        Assert.False(await Store.TryBeginReconciliationAsync(Context, inactive, DateTime.UtcNow, default));
        Assert.Single(await AuditAsync());
    }
}
