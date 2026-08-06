using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImageGen.Infrastructure;

/// <summary>
/// Hosted background service that drains a work queue with a pool of worker loops,
/// settling each job's completion (result / cancellation / failure). Subclasses
/// supply the queue, the worker count, and the work itself.
/// </summary>
public abstract class QueueWorkerService<TRequest, TProgress, TResult>(
    IWorkQueue<TRequest, TProgress, TResult> queue,
    ILogger logger) : BackgroundService
{
    /// <summary>A human-readable name for the service, used in log messages.</summary>
    protected abstract string ServiceName { get; }

    /// <summary>Number of jobs processed concurrently. Clamped to at least 1.</summary>
    protected virtual int WorkerCount => 1;

    /// <summary>Performs the work for one job. Implementations may be async or wrap
    /// synchronous native work; throwing propagates to the job's completion.</summary>
    protected abstract Task<TResult> ExecuteJobAsync(
        WorkJob<TRequest, TProgress, TResult> job, CancellationToken cancellationToken);

    /// <summary>Optional short description of a job for log messages (e.g. a file name).</summary>
    protected virtual string Describe(WorkJob<TRequest, TProgress, TResult> job) => "job";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Math.Max(1, WorkerCount);
        logger.LogInformation("{Service} started with {Workers} worker(s).", ServiceName, workers);

        var loops = Enumerable
            .Range(0, workers)
            .Select(_ => WorkerLoopAsync(stoppingToken))
            .ToArray();

        await Task.WhenAll(loops).ConfigureAwait(false);
        logger.LogInformation("{Service} stopped.", ServiceName);
    }

    private async Task WorkerLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                await ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task ProcessJobAsync(
        WorkJob<TRequest, TProgress, TResult> job, CancellationToken stoppingToken)
    {
        var what = Describe(job);
        try
        {
            logger.LogInformation("{Service}: starting {What}", ServiceName, what);
            var result = await ExecuteJobAsync(job, stoppingToken).ConfigureAwait(false);
            job.SetCompleted(result);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("{Service}: canceled {What}", ServiceName, what);
            job.SetCanceled(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Service}: failed {What}", ServiceName, what);
            job.SetFailed(ex);
        }
    }
}
