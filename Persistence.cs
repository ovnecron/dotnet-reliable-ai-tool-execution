using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class DispatchOperation
{
    public string TenantId { get; set; } = "";
    // OperationId IS the admitted DispatchIntentId, not a hash of the reply.
    public Guid OperationId { get; set; }
    public string RequesterId { get; set; } = "";
    public string ReplyRevision { get; set; } = "";
    public string Recipient { get; set; } = "";
    public string Body { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string AdmissionReference { get; set; } = "";
    public string ProviderAccount { get; set; } = "";
    public string ProviderEndpoint { get; set; } = "";
    public string ProviderKey { get; set; } = "";
    public DispatchState State { get; set; }
    public Guid? ActiveAttemptId { get; set; }
    public long Version { get; set; }
    public string? ProviderReference { get; set; }
}

public sealed class DispatchAttempt
{
    public string TenantId { get; set; } = "";
    public Guid AttemptId { get; set; }
    public Guid OperationId { get; set; }
    public string ExecutorId { get; set; } = "";
    public string ProviderAccount { get; set; } = "";
    public string ProviderEndpoint { get; set; } = "";
    public string ProviderKey { get; set; } = "";
    public DateTime PreparedUtc { get; set; }
    public DateTime RecoverAfterUtc { get; set; }
    public string Observation { get; set; } = "Prepared";
}

public sealed class DispatchAudit
{
    public Guid EventId { get; set; }
    public string TenantId { get; set; } = "";
    public Guid OperationId { get; set; }
    public Guid AttemptId { get; set; }
    public long Sequence { get; set; }
    public string Actor { get; set; } = "";
    public DateTime RecordedUtc { get; set; }
    public string EventType { get; set; } = "";
    public string? EvidenceReference { get; set; }
}

public sealed class ExecutionDb(DbContextOptions<ExecutionDb> options) : DbContext(options)
{
    public DbSet<DispatchOperation> Operations => Set<DispatchOperation>();
    public DbSet<DispatchAttempt> Attempts => Set<DispatchAttempt>();
    public DbSet<DispatchAudit> Audit => Set<DispatchAudit>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<DispatchOperation>().HasKey(o => new { o.TenantId, o.OperationId });
        model.Entity<DispatchOperation>().Property(o => o.Version).IsConcurrencyToken();
        model.Entity<DispatchOperation>().HasIndex(o => new { o.ProviderAccount, o.ProviderEndpoint, o.ProviderKey }).IsUnique();
        model.Entity<DispatchAttempt>().HasKey(a => new { a.TenantId, a.AttemptId });
        model.Entity<DispatchAttempt>().HasOne<DispatchOperation>().WithMany()
            .HasForeignKey(a => new { a.TenantId, a.OperationId });
        model.Entity<DispatchAudit>().HasKey(e => e.EventId);
        model.Entity<DispatchAudit>().HasIndex(e => new { e.TenantId, e.OperationId, e.Sequence }).IsUnique();
        model.Entity<DispatchAudit>().HasOne<DispatchAttempt>().WithMany()
            .HasForeignKey(e => new { e.TenantId, e.OperationId, e.AttemptId })
            .HasPrincipalKey(a => new { a.TenantId, a.OperationId, a.AttemptId });
    }
}

public sealed class SqliteReplyOperations : IReplyOperations
{
    private readonly DbContextOptions<ExecutionDb> options;

    public SqliteReplyOperations(string databasePath)
        : this(new DbContextOptionsBuilder<ExecutionDb>()
            .UseSqlite(new SqliteConnectionStringBuilder {
                DataSource = databasePath, Pooling = false, DefaultTimeout = 5, ForeignKeys = true
            }.ToString())
            .Options)
    {
    }

    public SqliteReplyOperations(DbContextOptions<ExecutionDb> options)
    {
        this.options = options;
    }

    // Each call creates an independent context and database connection.
    public ExecutionDb Open() => new(options);

    public static string Fingerprint(ReplyCommand command) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command) + "|reply-v1")));

    public async Task InitializeAsync()
    {
        await using var db = Open();
        await db.Database.EnsureCreatedAsync();
    }

    // Called by a trusted admission workflow AFTER it has committed its policy decision.
    // The fixture supplies that decision. It does not implement an approval service.
    public async Task AdmitAsync(DispatchContext context, string replyRevision, ReplyCommand command)
    {
        for (int retry = 0; ; retry++)
        {
            try
            {
                // A fresh context reloads the winner after a competing insert or SQLite lock.
                await using var db = Open();
                var existing = await db.Operations.FindAsync(context.TenantId, context.OperationId);
                if (existing is not null)
                {
                    if (existing.RequesterId != context.RequesterId || existing.ReplyRevision != replyRevision
                        || existing.Fingerprint != Fingerprint(command))
                        throw new InvalidOperationException("Dispatch intent conflicts with its admitted command.");
                    return;
                }
                db.Operations.Add(new DispatchOperation
                {
                    TenantId = context.TenantId, OperationId = context.OperationId,
                    RequesterId = context.RequesterId, ReplyRevision = replyRevision,
                    Recipient = command.Recipient, Body = command.Body, Fingerprint = Fingerprint(command),
                    AdmissionReference = $"admission/{context.OperationId:N}",
                    ProviderAccount = "test-mail-account", ProviderEndpoint = "https://mail.example.test/v1",
                    ProviderKey = context.OperationId.ToString("N"),
                    State = DispatchState.Ready
                });
                await db.SaveChangesAsync();
                return;
            }
            catch (Exception error) when (retry < 2 && IsAdmissionContention(error))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25));
            }
        }
    }

    private static bool IsAdmissionContention(Exception error)
    {
        var sqlite = error as SqliteException ?? error.InnerException as SqliteException;
        // Busy/locked, or a competing primary-key/unique-key insertion. Other failures propagate.
        return sqlite is { SqliteErrorCode: 5 or 6 }
            or { SqliteExtendedErrorCode: 1555 or 2067 };
    }

    public async Task<PreparedAttempt?> TryPrepareAttemptAsync(
        DispatchContext context, Guid attemptId, CancellationToken token)
    {
        await using var db = Open();
        var operation = await Authorized(db, context).AsNoTracking().SingleAsync(token);
        if (operation.State != DispatchState.Ready) return null;
        var command = new ReplyCommand(operation.Recipient, operation.Body);
        if (operation.Fingerprint != Fingerprint(command)
            || string.IsNullOrEmpty(operation.AdmissionReference))
            throw new InvalidOperationException("Stored command or admission is invalid.");

        await using var transaction = await db.Database.BeginTransactionAsync(token);
        int claimed = await Authorized(db, context)
            .Where(o => o.State == DispatchState.Ready && o.Version == operation.Version)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.State, DispatchState.InProgress)
                .SetProperty(o => o.ActiveAttemptId, attemptId)
                .SetProperty(o => o.Version, o => o.Version + 1), token);
        if (claimed != 1) return null;

        DateTime now = DateTime.UtcNow;
        db.Attempts.Add(new DispatchAttempt
        {
            TenantId = context.TenantId, OperationId = context.OperationId,
            AttemptId = attemptId, ExecutorId = "dispatch-worker",
            ProviderAccount = operation.ProviderAccount, ProviderEndpoint = operation.ProviderEndpoint,
            ProviderKey = operation.ProviderKey,
            PreparedUtc = now, RecoverAfterUtc = now.AddSeconds(30)
        });
        var audit = NewAudit(context, attemptId, operation.Version + 1,
            "dispatch-worker", "AttemptPrepared", null);
        db.Audit.Add(audit);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new PreparedAttempt(attemptId, command, operation.ProviderKey,
            operation.ProviderAccount, operation.ProviderEndpoint);
    }

    public async Task<DispatchResult> ReadStatusAsync(DispatchContext context, CancellationToken token)
    {
        await using var db = Open();
        return await Authorized(db, context)
            .Select(o => new DispatchResult(o.OperationId, o.State)).SingleAsync(token);
    }

    public Task<DispatchResult> RecordObservationAsync(DispatchContext context, Guid attemptId,
        SubmissionObservation observation, CancellationToken token) =>
        RecordAsync(context, attemptId, observation, false, token);

    public async Task<DispatchResult> RecordAsync(DispatchContext context, Guid attemptId,
        SubmissionObservation observation, bool reconciled, CancellationToken token)
    {
        if (observation.Outcome == SubmissionOutcome.Accepted
            && (observation.ProviderReference is null || observation.EvidenceReference is null))
            throw new InvalidOperationException("Acceptance needs a provider reference and evidence.");
        await using var db = Open();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var operation = await Authorized(db, context).SingleAsync(token);
        var attempt = await db.Attempts.SingleAsync(a => a.TenantId == context.TenantId
            && a.OperationId == context.OperationId && a.AttemptId == attemptId, token);
        bool ownsState = operation.ActiveAttemptId == attemptId
            && operation.State is DispatchState.InProgress or DispatchState.OutcomeUnknown;
        if (ownsState)
        {
            operation.State = observation.Outcome switch
            {
                SubmissionOutcome.Accepted => DispatchState.Succeeded,
                SubmissionOutcome.PermanentRejection => DispatchState.Rejected,
                SubmissionOutcome.RetryableRejection => DispatchState.RetryPending,
                _ => DispatchState.OutcomeUnknown
            };
            operation.ProviderReference = observation.ProviderReference;
            attempt.Observation = observation.Outcome.ToString();
        }
        string eventType = ObservationEventType(observation.Outcome, ownsState, reconciled);
        db.Audit.Add(NewAudit(context, attemptId, ++operation.Version,
            reconciled ? "recovery-worker" : "dispatch-worker",
            eventType, observation.EvidenceReference));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new DispatchResult(operation.OperationId, operation.State);
    }

    public async Task<DispatchAttempt?> ReadRecoverableAsync(
        DispatchContext context, DateTime nowUtc, CancellationToken token)
    {
        await using var db = Open();
        var operation = await Authorized(db, context).AsNoTracking().SingleAsync(token);
        if (operation.State is not (DispatchState.InProgress or DispatchState.OutcomeUnknown)) return null;
        return await db.Attempts.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == context.TenantId
            && a.OperationId == context.OperationId && a.AttemptId == operation.ActiveAttemptId
            && a.RecoverAfterUtc <= nowUtc, token);
    }

    public async Task<bool> TryBeginReconciliationAsync(
        DispatchContext context, Guid attemptId, DateTime nowUtc, CancellationToken token)
    {
        await using var db = Open();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var operation = await Authorized(db, context).SingleAsync(token);
        if (operation.ActiveAttemptId != attemptId
            || operation.State is not (DispatchState.InProgress or DispatchState.OutcomeUnknown))
            return false;

        var attempt = await db.Attempts.SingleOrDefaultAsync(a => a.TenantId == context.TenantId
            && a.OperationId == context.OperationId && a.AttemptId == attemptId
            && a.RecoverAfterUtc <= nowUtc, token);
        if (attempt is null) return false;

        // Reserve this lookup slot and a later check together, even if lookup fails or the worker exits.
        attempt.RecoverAfterUtc = nowUtc.AddSeconds(30);
        db.Audit.Add(NewAudit(context, attemptId, ++operation.Version,
            "recovery-worker", "ReconciliationStarted", null));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return true;
    }

    private static string ObservationEventType(
        SubmissionOutcome outcome, bool ownsState, bool reconciled)
    {
        if (!ownsState) return $"Late{outcome}Observation";

        return outcome switch
        {
            SubmissionOutcome.Accepted when reconciled => "ProviderAcceptanceReconciled",
            SubmissionOutcome.Accepted => "ProviderAcceptanceConfirmed",
            SubmissionOutcome.Unknown => "SubmissionOutcomeUnknown",
            _ => outcome.ToString()
        };
    }

    private static IQueryable<DispatchOperation> Authorized(ExecutionDb db, DispatchContext context) =>
        db.Operations.Where(o => o.TenantId == context.TenantId && o.OperationId == context.OperationId
            && o.RequesterId == context.RequesterId);

    private static DispatchAudit NewAudit(DispatchContext context, Guid attemptId,
        long sequence, string actor, string eventType, string? evidence) => new()
    {
        EventId = Guid.NewGuid(), TenantId = context.TenantId, OperationId = context.OperationId,
        AttemptId = attemptId, Sequence = sequence, Actor = actor, RecordedUtc = DateTime.UtcNow,
        EventType = eventType, EvidenceReference = evidence
    };
}

// This worker deliberately uses lookup only. It never resubmits an uncertain operation.
public sealed class ReplyRecovery(SqliteReplyOperations operations, IReplyProvider provider)
{
    public async Task ReconcileOnceAsync(
        DispatchContext context, DateTime nowUtc, CancellationToken token)
    {
        DispatchAttempt? attempt = await operations.ReadRecoverableAsync(context, nowUtc, token);
        if (attempt is null) return;
        ProviderDestination.EnsureMatches(
            provider, attempt.ProviderAccount, attempt.ProviderEndpoint);

        if (!await operations.TryBeginReconciliationAsync(context, attempt.AttemptId, nowUtc, token))
            return;
        SubmissionObservation evidence = await provider.LookupAsync(attempt.ProviderKey, token);
        await operations.RecordAsync(context, attempt.AttemptId, evidence, true, token);
    }
}
