public sealed class SimulatedMailProvider : IReplyProvider
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, SubmissionObservation> results = [];
    private int submissions;
    private int lookups;

    public string AccountId { get; init; } = "test-mail-account";
    public string Endpoint { get; init; } = "https://mail.example.test/v1";
    public bool LoseResponse { get; init; }
    public SubmissionOutcome Outcome { get; init; } = SubmissionOutcome.Accepted;
    public CancellationTokenSource? CancelAfterAcceptance { get; init; }
    public int Submissions { get { lock (gate) return submissions; } }
    public int Lookups { get { lock (gate) return lookups; } }

    public Task<SubmissionObservation> SubmitAsync(ReplyCommand command, string key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SubmissionObservation observation;
        lock (gate)
        {
            submissions++;
            // Each accepted operation has its own receipt, stable for this provider key.
            string? receipt = Outcome == SubmissionOutcome.Accepted ? $"receipt-{key}" : null;
            observation = new(Outcome, receipt,
                receipt is null ? $"provider-response/{key}/{Outcome}" : $"provider-response/{receipt}");
            results[key] = observation;
        }
        // Cancellation callbacks can run application code, so invoke them outside the lock.
        CancelAfterAcceptance?.Cancel();
        if (LoseResponse) throw new SubmissionOutcomeUnknownException();
        return Task.FromResult(observation);
    }

    public Task<SubmissionObservation> LookupAsync(string key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            lookups++;
            return Task.FromResult(results.TryGetValue(key, out var observation)
                ? observation with { EvidenceReference = observation.ProviderReference is { } receipt
                    ? $"provider-lookup/{receipt}" : $"provider-lookup/{key}/{observation.Outcome}" }
                : new SubmissionObservation(SubmissionOutcome.Unknown, null, "provider-lookup/not-found"));
        }
    }
}
