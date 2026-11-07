using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

internal sealed class ReadyReadBarrierInterceptor(Barrier barrier) : IMaterializationInterceptor
{
    internal ConcurrentBag<long> Versions { get; } = [];

    public object InitializedInstance(MaterializationInterceptionData data, object entity)
    {
        if (entity is DispatchOperation { State: DispatchState.Ready } operation)
        {
            Versions.Add(operation.Version);
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Both workers must read Ready before either can claim.");
        }
        return entity;
    }
}

internal sealed class AdmissionInsertBarrierInterceptor : SaveChangesInterceptor
{
    private readonly TaskCompletionSource bothStaged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int arrivals;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context!.ChangeTracker.Entries<DispatchOperation>().Any(e => e.State == EntityState.Added))
        {
            // Both calls have observed an absent intent and staged an insert before either saves.
            int arrival = Interlocked.Increment(ref arrivals);
            if (arrival == 2) bothStaged.SetResult();
            if (arrival <= 2) await bothStaged.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        return result;
    }
}

internal sealed class DueAttemptBarrierInterceptor(Barrier barrier) : IMaterializationInterceptor
{
    private int arrivals;

    public object InitializedInstance(MaterializationInterceptionData data, object entity)
    {
        if (entity is DispatchAttempt && Interlocked.Increment(ref arrivals) <= 2
            && !barrier.SignalAndWait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("Both recovery workers must read the due attempt before reserving a lookup.");
        return entity;
    }
}
