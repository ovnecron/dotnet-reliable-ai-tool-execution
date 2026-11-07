using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

// Used by the process-exit test to stop after the claim commits.
if (args is ["--crash-after-claim", var childPath, var childOperation])
{
    var childStore = new SqliteReplyOperations(childPath);
    await childStore.TryPrepareAttemptAsync(
        new("tenant-a", "support-lead", Guid.Parse(childOperation)), Guid.NewGuid(), default);
    Environment.Exit(23);
    return;
}

string directory = Path.Combine(Path.GetTempPath(), "tool-execution-demo-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
string databasePath = Path.Combine(directory, "execution.db");
var store = new SqliteReplyOperations(databasePath);
await store.InitializeAsync();

// Trusted application code admits a fixed reply. The model supplies no recipient or body.
var context = new DispatchContext("tenant-a", "support-lead", Guid.NewGuid());
await store.AdmitAsync(context, "ticket-42/reply-7",
    new ReplyCommand("customer@example.net", "Your replacement is approved."));
var provider = new SimulatedMailProvider { LoseResponse = true };
AIFunction tool = ReplyToolRegistration.Create(
    new SupportReplyTools(new ReplyExecutor(store, provider), context));

Console.WriteLine("The provider accepts the reply, but its response is lost.");
await tool.InvokeAsync(new AIFunctionArguments());
Console.WriteLine($"Stored state: {(await store.ReadStatusAsync(context, default)).State}");

// Reopen the store to demonstrate that the duplicate is handled by persisted state.
var reopened = new SqliteReplyOperations(databasePath);
tool = ReplyToolRegistration.Create(
    new SupportReplyTools(new ReplyExecutor(reopened, provider), context));
await tool.InvokeAsync(new AIFunctionArguments());
Console.WriteLine($"Duplicate call: {(await reopened.ReadStatusAsync(context, default)).State}");
Console.WriteLine($"Provider submissions: {provider.Submissions}");

// Supply a later worker time so the demo does not need to wait for the recovery delay.
await new ReplyRecovery(reopened, provider)
    .ReconcileOnceAsync(context, DateTime.UtcNow.AddMinutes(1), default);
Console.WriteLine($"After provider lookup: {(await reopened.ReadStatusAsync(context, default)).State}");
Console.WriteLine($"Provider submissions: {provider.Submissions}, lookups: {provider.Lookups}");

Console.WriteLine("\nStored audit trail:");
await using var db = reopened.Open();
foreach (var entry in await db.Audit.OrderBy(e => e.Sequence).ToListAsync())
    Console.WriteLine($"{entry.Sequence}. {entry.EventType} ({entry.Actor}) {entry.EvidenceReference}");
Console.WriteLine($"\nDatabase: {databasePath}");
