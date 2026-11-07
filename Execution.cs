using System.ComponentModel;
using Microsoft.Extensions.AI;

public enum DispatchState
{
    Ready,
    InProgress,
    Succeeded,
    Rejected,
    RetryPending,
    OutcomeUnknown
}

public sealed record DispatchResult(Guid OperationId, DispatchState State);

public sealed record DispatchContext(
    string TenantId,
    string RequesterId,
    Guid OperationId);

public sealed record ReplyCommand(string Recipient, string Body);

public sealed record PreparedAttempt(
    Guid AttemptId,
    ReplyCommand Command,
    string ProviderKey,
    string ProviderAccount,
    string ProviderEndpoint);

public enum SubmissionOutcome { Accepted, RetryableRejection, PermanentRejection, Unknown }

public sealed record SubmissionObservation(
    SubmissionOutcome Outcome,
    string? ProviderReference,
    string? EvidenceReference = null);

public sealed class SubmissionOutcomeUnknownException : Exception;

public interface IReplyOperations
{
    Task<PreparedAttempt?> TryPrepareAttemptAsync(
        DispatchContext context, Guid attemptId, CancellationToken token);

    Task<DispatchResult> ReadStatusAsync(
        DispatchContext context, CancellationToken token);

    Task<DispatchResult> RecordObservationAsync(
        DispatchContext context, Guid attemptId,
        SubmissionObservation observation, CancellationToken token);
}

public interface IReplyProvider
{
    string AccountId { get; }
    string Endpoint { get; }

    Task<SubmissionObservation> SubmitAsync(
        ReplyCommand command, string idempotencyKey, CancellationToken token);

    Task<SubmissionObservation> LookupAsync(string idempotencyKey, CancellationToken token);
}

internal static class ProviderDestination
{
    internal static void EnsureMatches(
        IReplyProvider provider, string accountId, string endpoint)
    {
        if (provider.AccountId != accountId || provider.Endpoint != endpoint)
        {
            throw new InvalidOperationException("Provider destination mismatch.");
        }
    }
}

public sealed class SupportReplyTools(
    ReplyExecutor executor,
    DispatchContext context)
{
    [Description("Dispatches the stored support reply authorized for this operation.")]
    public Task<DispatchResult> SendApprovedReplyAsync(
        CancellationToken cancellationToken = default) =>
        executor.ExecuteAsync(context, cancellationToken);
}

public static class ReplyToolRegistration
{
    public static AIFunction Create(SupportReplyTools tools) =>
        AIFunctionFactory.Create(
            (Func<CancellationToken, Task<DispatchResult>>)
                tools.SendApprovedReplyAsync);
}

public sealed class ReplyExecutor(
    IReplyOperations operations,
    IReplyProvider provider)
{
    public async Task<DispatchResult> ExecuteAsync(
        DispatchContext context,
        CancellationToken cancellationToken)
    {
        Guid attemptId = Guid.NewGuid();
        PreparedAttempt? attempt = await operations.TryPrepareAttemptAsync(
            context, attemptId, cancellationToken);

        if (attempt is null)
        {
            return await operations.ReadStatusAsync(context, cancellationToken);
        }

        ProviderDestination.EnsureMatches(
            provider, attempt.ProviderAccount, attempt.ProviderEndpoint);

        SubmissionObservation observation;
        try
        {
            observation = await provider.SubmitAsync(
                attempt.Command, attempt.ProviderKey, cancellationToken);
        }
        catch (SubmissionOutcomeUnknownException)
        {
            observation = new(SubmissionOutcome.Unknown, null);
        }

        using var evidenceBudget = new CancellationTokenSource(
            TimeSpan.FromSeconds(2));

        return await operations.RecordObservationAsync(
            context, attemptId, observation, evidenceBudget.Token);
    }
}
