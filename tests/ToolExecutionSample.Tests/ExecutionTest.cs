using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;

// xUnit creates a new test-class instance, and therefore a new database, for each case.
public abstract class ExecutionTest : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "tool-execution-test-" + Guid.NewGuid().ToString("N"));
    protected string DatabasePath => Path.Combine(directory, "execution.db");
    protected SqliteReplyOperations Store => new(DatabasePath);
    protected DispatchContext Context { get; } = new("tenant-a", "support-lead", Guid.NewGuid());
    protected ReplyCommand Command { get; } = new("customer@example.net", "Your replacement is approved.");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        await Store.InitializeAsync();
        await Store.AdmitAsync(Context, "ticket-42/reply-7", Command);
    }

    public Task DisposeAsync()
    {
        Directory.Delete(directory, recursive: true);
        return Task.CompletedTask;
    }

    protected SqliteReplyOperations InterceptedStore(params IInterceptor[] interceptors)
    {
        using var db = Store.Open();
        var options = new DbContextOptionsBuilder<ExecutionDb>()
            .UseSqlite(db.Database.GetConnectionString())
            .AddInterceptors(interceptors)
            .Options;
        return new SqliteReplyOperations(options);
    }

    protected AIFunction Tool(IReplyProvider provider, DispatchContext? context = null, IReplyOperations? store = null) =>
        ReplyToolRegistration.Create(new SupportReplyTools(new ReplyExecutor(store ?? Store, provider), context ?? Context));

    protected async Task<DispatchResult> InvokeAsync(IReplyProvider provider, DispatchContext? context = null,
        IReplyOperations? store = null, CancellationToken token = default)
    {
        var result = await Tool(provider, context, store).InvokeAsync(new AIFunctionArguments(), token);
        return ((JsonElement)result!).Deserialize<DispatchResult>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } })!;
    }

    protected async Task<List<DispatchAudit>> AuditAsync()
    {
        await using var db = Store.Open();
        return await db.Audit.Where(e => e.OperationId == Context.OperationId).OrderBy(e => e.Sequence).ToListAsync();
    }

    protected async Task<Guid> AttemptIdAsync()
    {
        await using var db = Store.Open();
        return await db.Attempts.Where(a => a.OperationId == Context.OperationId).Select(a => a.AttemptId).SingleAsync();
    }

    protected async Task ReconcileAsync(IReplyProvider provider) =>
        await new ReplyRecovery(Store, provider).ReconcileOnceAsync(Context, DateTime.UtcNow.AddMinutes(1), default);

    protected async Task ArrangeReconciledAcceptanceAsync()
    {
        var provider = new SimulatedMailProvider { LoseResponse = true };
        await InvokeAsync(provider);
        await ReconcileAsync(provider);
    }
}
