public sealed class SimulatedMailProviderTests
{
    private static readonly ReplyCommand Command = new("customer@example.net", "Approved.");

    [Fact]
    public async Task ConcurrentSubmissionsAndLookupsPreserveAllResults()
    {
        const int count = 200;
        var provider = new SimulatedMailProvider();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = Enumerable.Range(0, count).Select(index => Task.Run(async () => {
            await start.Task;
            string key = $"operation-{index}";
            var submitted = await provider.SubmitAsync(Command, key, default);
            var found = await provider.LookupAsync(key, default);
            Assert.Equal(SubmissionOutcome.Accepted, submitted.Outcome);
            Assert.Equal(SubmissionOutcome.Accepted, found.Outcome);
            Assert.Equal(submitted.ProviderReference, found.ProviderReference);
            Assert.Equal($"receipt-{key}", found.ProviderReference);
            Assert.Equal($"provider-lookup/receipt-{key}", found.EvidenceReference);
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(workers);

        Assert.Equal(count, provider.Submissions);
        Assert.Equal(count, provider.Lookups);
    }

    [Fact]
    public async Task UniqueIntentsReceiveDistinctStableReceipts()
    {
        var provider = new SimulatedMailProvider();
        var first = await provider.SubmitAsync(Command, "intent-a", default);
        var second = await provider.SubmitAsync(Command, "intent-b", default);

        Assert.NotNull(first.ProviderReference);
        Assert.NotNull(second.ProviderReference);
        Assert.NotEqual(first.ProviderReference, second.ProviderReference);
        var firstLookup = await provider.LookupAsync("intent-a", default);
        var repeatedLookup = await provider.LookupAsync("intent-a", default);
        var secondLookup = await provider.LookupAsync("intent-b", default);
        Assert.Equal(first.ProviderReference, firstLookup.ProviderReference);
        Assert.Equal(firstLookup, repeatedLookup);
        Assert.Equal(second.ProviderReference, secondLookup.ProviderReference);
        Assert.NotEqual(firstLookup.EvidenceReference, secondLookup.EvidenceReference);
    }

    [Fact]
    public async Task CancelledCallsDoNotChangeSimulatorState()
    {
        var provider = new SimulatedMailProvider();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.SubmitAsync(Command, "operation-1", cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.LookupAsync("operation-1", cancelled.Token));

        Assert.Equal(0, provider.Submissions);
        Assert.Equal(0, provider.Lookups);
        var observation = await provider.LookupAsync("operation-1", default);
        Assert.Equal(SubmissionOutcome.Unknown, observation.Outcome);
        Assert.Null(observation.ProviderReference);
    }

    [Fact]
    public async Task LostResponseLeavesAcceptanceAvailableForLookup()
    {
        var provider = new SimulatedMailProvider { LoseResponse = true };
        await Assert.ThrowsAsync<SubmissionOutcomeUnknownException>(() =>
            provider.SubmitAsync(Command, "operation-1", default));

        var observation = await provider.LookupAsync("operation-1", default);
        Assert.Equal(SubmissionOutcome.Accepted, observation.Outcome);
        Assert.Equal("receipt-operation-1", observation.ProviderReference);
        Assert.Equal("provider-lookup/receipt-operation-1", observation.EvidenceReference);
        Assert.Equal(1, provider.Submissions);
        Assert.Equal(1, provider.Lookups);
    }
}
