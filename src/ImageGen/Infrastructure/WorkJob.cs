namespace ImageGen.Infrastructure;

/// <summary>
/// A unit of work on a queue: the request, an optional progress hook the worker
/// reports to, and a completion signal the enqueuer can await.
/// </summary>
/// <typeparam name="TRequest">The work description (what to download, generate, …).</typeparam>
/// <typeparam name="TProgress">The progress snapshot type reported during the work.</typeparam>
/// <typeparam name="TResult">The value produced on success.</typeparam>
public sealed class WorkJob<TRequest, TProgress, TResult>
{
    private readonly TaskCompletionSource<TResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates a job for <paramref name="request"/>, optionally reporting to
    /// <paramref name="progress"/>.</summary>
    public WorkJob(TRequest request, IProgress<TProgress>? progress = null)
    {
        Request = request;
        Progress = progress;
    }

    /// <summary>The work to perform.</summary>
    public TRequest Request { get; }

    /// <summary>Progress hook invoked by the worker; surfaced to Spectre.Console by the caller.</summary>
    public IProgress<TProgress>? Progress { get; }

    /// <summary>Completes with the result on success, or faults on error/cancellation.</summary>
    public Task<TResult> Completion => _completion.Task;

    internal void SetCompleted(TResult result) => _completion.TrySetResult(result);
    internal void SetFailed(Exception ex) => _completion.TrySetException(ex);
    internal void SetCanceled(CancellationToken token) => _completion.TrySetCanceled(token);
}
