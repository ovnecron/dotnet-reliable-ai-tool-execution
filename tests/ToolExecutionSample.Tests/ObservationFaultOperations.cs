sealed class ObservationFaultOperations(IReplyOperations normal, IReplyOperations faulty) : IReplyOperations
{
    public Task<PreparedAttempt?> TryPrepareAttemptAsync(DispatchContext context, Guid attemptId, CancellationToken token) =>
        normal.TryPrepareAttemptAsync(context, attemptId, token);
    public Task<DispatchResult> ReadStatusAsync(DispatchContext context, CancellationToken token) =>
        normal.ReadStatusAsync(context, token);
    public Task<DispatchResult> RecordObservationAsync(DispatchContext context, Guid attemptId,
        SubmissionObservation observation, CancellationToken token) =>
        faulty.RecordObservationAsync(context, attemptId, observation, token);
}
