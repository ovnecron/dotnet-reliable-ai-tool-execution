using Microsoft.EntityFrameworkCore;

public sealed class ToolExecutionTests : ExecutionTest
{
    [Fact]
    public async Task LostResponseIsRecoveredWithoutResubmission()
    {
        var provider = new SimulatedMailProvider { LoseResponse = true };

        Assert.Equal(DispatchState.OutcomeUnknown, (await InvokeAsync(provider)).State);
        // InvokeAsync opens a new store, so the duplicate must use persisted state.
        Assert.Equal(DispatchState.OutcomeUnknown, (await InvokeAsync(provider)).State);
        Assert.Equal(1, provider.Submissions);
        await ReconcileAsync(provider);

        Assert.Equal(DispatchState.Succeeded, (await InvokeAsync(provider)).State);
        Assert.Equal(1, provider.Submissions);
        Assert.Equal(1, provider.Lookups);
        var audit = await AuditAsync();
        Assert.Equal(new[] { "AttemptPrepared", "SubmissionOutcomeUnknown", "ReconciliationStarted",
            "ProviderAcceptanceReconciled" }, audit.Select(e => e.EventType));
        Assert.Equal(new long[] { 1, 2, 3, 4 }, audit.Select(e => e.Sequence));
        Assert.Equal($"provider-lookup/receipt-{Context.OperationId:N}", audit[^1].EvidenceReference);
    }

    [Fact]
    public void ToolSchemaContainsNoTrustedArguments()
    {
        var schema = Tool(new SimulatedMailProvider()).JsonSchema;
        Assert.Empty(schema.GetProperty("properties").EnumerateObject());
    }

    [Fact]
    public async Task RepeatedIntentDoesNotSendAgain()
    {
        await Store.AdmitAsync(Context, "ticket-42/reply-7", Command);
        var provider = new SimulatedMailProvider();
        Assert.Equal(DispatchState.Succeeded, (await InvokeAsync(provider)).State);
        Assert.Equal(DispatchState.Succeeded, (await InvokeAsync(provider)).State);
        Assert.Equal(1, provider.Submissions);
        Assert.Equal(new[] { "AttemptPrepared", "ProviderAcceptanceConfirmed" },
            (await AuditAsync()).Select(e => e.EventType));
    }

    [Fact]
    public async Task NewIntentCanSendIdenticalReply()
    {
        var second = Context with { OperationId = Guid.NewGuid() };
        await Store.AdmitAsync(second, "ticket-42/reply-7", Command);
        var provider = new SimulatedMailProvider();
        await InvokeAsync(provider);
        await InvokeAsync(provider, second);
        Assert.Equal(2, provider.Submissions);
        await using var db = Store.Open();
        Assert.Equal(2, await db.Operations.Select(o => o.ProviderKey).Distinct().CountAsync());
    }

    [Theory]
    [InlineData(SubmissionOutcome.RetryableRejection, DispatchState.RetryPending)]
    [InlineData(SubmissionOutcome.PermanentRejection, DispatchState.Rejected)]
    public async Task RejectionsAreNotAutomaticallyResubmitted(SubmissionOutcome outcome, DispatchState expected)
    {
        var provider = new SimulatedMailProvider { Outcome = outcome };
        Assert.Equal(expected, (await InvokeAsync(provider)).State);
        Assert.Equal(expected, (await InvokeAsync(provider)).State);
        await ReconcileAsync(provider);
        Assert.Equal(1, provider.Submissions);
        Assert.Equal(0, provider.Lookups);
    }

    [Fact]
    public async Task CallerCancellationStillPersistsUncertainty()
    {
        using var caller = new CancellationTokenSource();
        var provider = new SimulatedMailProvider { LoseResponse = true, CancelAfterAcceptance = caller };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(provider, token: caller.Token));

        Assert.Equal(DispatchState.OutcomeUnknown, (await Store.ReadStatusAsync(Context, default)).State);
        Assert.Equal("SubmissionOutcomeUnknown", (await AuditAsync())[^1].EventType);
        Assert.Equal(1, provider.Submissions);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("endpoint")]
    public async Task WrongDestinationBlocksSubmissionAndLookup(string mismatch)
    {
        var provider = new SimulatedMailProvider {
            AccountId = mismatch == "account" ? "other-account" : "test-mail-account",
            Endpoint = mismatch == "endpoint" ? "https://other-mail.example.test/v1" : "https://mail.example.test/v1"
        };
        var dispatchError = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(provider));
        var lookupError = await Assert.ThrowsAsync<InvalidOperationException>(() => ReconcileAsync(provider));

        Assert.Equal("Provider destination mismatch.", dispatchError.Message);
        Assert.Equal("Provider destination mismatch.", lookupError.Message);
        Assert.Equal(0, provider.Submissions);
        Assert.Equal(0, provider.Lookups);
        Assert.Equal("AttemptPrepared", Assert.Single(await AuditAsync()).EventType);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("requester")]
    public async Task ForeignContextCannotDispatch(string mismatch)
    {
        var foreign = mismatch == "tenant" ? Context with { TenantId = "tenant-b" }
            : Context with { RequesterId = "other-user" };
        var provider = new SimulatedMailProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(provider, foreign));
        Assert.Equal(0, provider.Submissions);
        Assert.Equal(DispatchState.Ready, (await Store.ReadStatusAsync(Context, default)).State);
        Assert.Empty(await AuditAsync());
    }
}
