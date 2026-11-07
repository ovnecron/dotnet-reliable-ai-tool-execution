using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

// Forces a real audit-table constraint violation inside the store's transaction.
internal sealed class DuplicateAuditSequenceInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var db = (ExecutionDb)eventData.Context!;
        var audit = db.ChangeTracker.Entries<DispatchAudit>()
            .Single(e => e.State == EntityState.Added).Entity;
        db.Audit.Add(new DispatchAudit {
            EventId = Guid.NewGuid(), TenantId = audit.TenantId, OperationId = audit.OperationId,
            AttemptId = audit.AttemptId, Sequence = audit.Sequence, EventType = "InjectedDuplicate"
        });
        return ValueTask.FromResult(result);
    }
}
