# dotnet-reliable-ai-tool-execution

A .NET 10 example demonstrating atomic claims, idempotency, audit trails, and recovery of uncertain AI tool executions using EF Core and SQLite.

Companion article: [Tool Execution Needs Idempotency and Audit Trails](https://www.lukaswalter.dev/posts/tool-execution-needs-idempotency-and-audit-trails/).

It demonstrates three boundaries:

- **Atomic claiming:** one worker commits the operation claim, attempt, and required audit evidence together.
- **Lost-response recovery:** a duplicate tool call reads persisted uncertainty, and a later provider lookup resolves it without another send.
- **Audit rollback:** a failed mandatory audit insert rolls back the local state transition.

The provider is a deterministic simulator. No model, email credentials, frontend, or database server is required.

## Run the demo

Install the .NET 10 SDK, then run:

```sh
dotnet build ToolExecutionSample.sln
dotnet run --project ToolExecutionSample.csproj
```

`Program.cs` follows one admitted reply through a lost provider response, a duplicate `AIFunction` call against a reopened store, and lookup-only recovery. It prints `OutcomeUnknown`, `OutcomeUnknown`, then `Succeeded`, with one submission and one lookup. The ordered audit trail includes the operation's stable simulated provider receipt.

Each run creates a new database in the system temporary directory and prints its path for inspection. Every store operation opens its own DbContext and connection, with pooling disabled. The simulator retains acceptance in memory while the store is reopened. The demo supplies a later worker time to avoid waiting for the thirty-second recovery delay. Its `--crash-after-claim` mode is used only by the process-exit test.

Pinned packages are `Microsoft.Extensions.AI` 10.9.0, `Microsoft.EntityFrameworkCore.Sqlite` 10.0.0, and `SQLitePCLRaw.bundle_e_sqlite3` 2.1.14. The native SQLite bundle overrides a vulnerable transitive version. NuGet vulnerability auditing is enabled. Verification used SDK 10.0.401.

## Run the tests

```sh
dotnet test ToolExecutionSample.sln
```

Each xUnit case owns and deletes its own file-backed database. Tests exercise the real EF Core transactions and `AIFunction` boundary. Test-only interceptors force the relevant races and storage failures:

- Both claim workers read `Ready` and version zero before either updates the row. The result is one committed attempt and audit event. A losing caller may return no claim or receive a SQLite busy/locked error.
- Both admission calls stage an insert for the same absent intent. Matching requests reload the winner, conflicting requests fail. SQLite contention retries are limited to three attempts.
- Two recovery workers read the same due attempt before reserving a lookup. Only one lookup slot is granted. An unknown result or lookup failure retains a durable next-check time thirty seconds later.
- A cross-operation audit reference fails the database foreign-key constraint. Late observations preserve resolved state, and failed audit inserts roll back claims, observations, or recovery scheduling.

The suite also covers distinct send intents, distinct stable provider receipts, cancellation, rejection states, destination mismatches, tenant/requester isolation, and an actual child-process exit after claim commit.

<details>
<summary>Individual verification cases</summary>

| Exercised case | Observed result |
| --- | --- |
| Both claim workers read `Ready` and version zero before updating | One committed attempt and `AttemptPrepared` event, the loser returns no claim or a SQLite lock error |
| Two matching admissions stage an insert for the same absent intent | Both callers resolve the single winning operation |
| Concurrent admissions disagree on the stored command | One wins, the other rejects the conflict |
| Admission repeatedly encounters SQLite busy or locked errors | At most three storage attempts, then the error propagates |
| Audit event names one operation and another operation's attempt | The composite foreign key rejects the event |
| Both recovery workers read the same due attempt | One lookup slot and one `ReconciliationStarted` event |
| Lookup remains unknown or fails | The next check is persisted thirty seconds later, intervening passes do no lookup |
| An operation is resolved after the recovery candidate was read | The stale candidate cannot start another reconciliation |
| Mandatory audit insertion violates its unique sequence constraint | The executed claim and attempt insertion roll back, operation remains `Ready` |
| Provider accepts, then loses its response | Reopened store returns `OutcomeUnknown`, lookup resolves acceptance, one submission total |
| Provider accepts, then the result transaction fails | Prepared attempt remains, later lookup records success without a second send |
| Two admitted intents share the same reply revision and body | Two intended submissions with distinct provider receipts, repeated calls reuse each intent's status |
| A command changes under an existing intent | Admission rejects the conflict |
| Provider definitely rejects an attempt | Retryable rejection becomes `RetryPending`, permanent rejection becomes `Rejected` |
| Caller cancels after possible acceptance | Uncertainty is stored even though function-result marshalling cancels |
| Original worker reports unknown after reconciliation succeeds | Late observation is retained, `Succeeded` survives |
| Child process exits immediately after claim commit | A new process can read the claim, lookup-only recovery does not send |
| Adapter account or endpoint differs from the persisted destination | Both submission and recovery lookup stop before calling the provider |
| The same observation is processed again | A new audit event is recorded, resolved operation state survives |
| Another tenant uses the operation ID | No operation is returned or dispatched |

</details>

## Boundaries

The simulator proves no real provider's idempotency, lookup, delivery, or replay contract. Repeated submissions are counted as calls, not deduplicated external effects. Accepted operation keys have distinct stable receipts. Simulator state is not durable across process restarts.

SQLite tests do not prove power-loss durability or another database's concurrency semantics. Storage errors can still escape after bounded contention retries. Before retrying dispatch after an uncertain commit, reload the durable operation. Each demo and test uses a new database. `EnsureCreatedAsync` does not migrate an older schema.

Admission assumes an already-made policy decision and stores a synthetic `admission/<operation-id>` reference. Its nonempty-reference check does not verify authorization. The sample does not implement authentication, an approval service, coordinated cancellation before handoff, or live policy revocation. Its fingerprint covers the synthetic recipient, body, and handler contract. Production commands must include every relevant versioned input, including attachments. The sample stores its synthetic payload inline.

Recovery performs lookup only. It reserves each lookup slot and advances the next due time in one transaction before contacting the provider. The thirty-second interval limits immediate repeat work. It is not a fence that prevents a paused worker from resuming after that interval. Overlapping lookups can finish out of order. The adapter must use the provider's consistency guarantees to distinguish authoritative observations from stale or inconclusive evidence. A failed lookup leaves the operation recoverable at the next due time. The sample has no maximum-lookup escalation policy, manual-review scheduler, automatic send retry, replay admission, provider-key expiry handling, or outbox relay.

Both initial submission and recovery compare the adapter's account and endpoint with the persisted destination. A mismatch stops processing. A real adapter must report its authenticated account and endpoint.

The audit table demonstrates transactional evidence, not tamper-resistant preservation, export delivery, or retention enforcement. Its composite foreign key binds each event to the matching tenant, operation, and attempt. Application writes append events, but database privileges are outside this example.

## License

[MIT](LICENSE), copyright 2026 Lukas Walter.
