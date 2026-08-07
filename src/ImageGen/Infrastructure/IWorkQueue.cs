using System.Threading.Channels;

namespace ImageGen.Infrastructure;

/// <summary>A thread-safe queue of work jobs for a background service to drain.</summary>
public interface IWorkQueue<TRequest, TProgress, TResult>
{
    /// <summary>Enqueues work and returns a job whose <see cref="WorkJob{TRequest,TProgress,TResult}.Completion"/>
    /// task can be awaited and whose progress hook reports progress.</summary>
    WorkJob<TRequest, TProgress, TResult> Enqueue(TRequest request, IProgress<TProgress>? progress = null);

    /// <summary>Consumes queued jobs. Completes when the queue is closed and drained.</summary>
    IAsyncEnumerable<WorkJob<TRequest, TProgress, TResult>> ReadAllAsync(CancellationToken cancellationToken);

    /// <summary>Signals that no further jobs will be enqueued.</summary>
    void Complete();
}

/// <summary>
/// Unbounded <see cref="Channel{T}"/>-backed work queue. Register concrete closed
/// generic types (or thin derived aliases) as singletons.
/// </summary>
public class ChannelWorkQueue<TRequest, TProgress, TResult>
    : IWorkQueue<TRequest, TProgress, TResult>
{
    private readonly Channel<WorkJob<TRequest, TProgress, TResult>> _channel;

    /// <param name="singleReader">True when exactly one worker drains the queue,
    /// which lets the channel use a faster single-consumer path.</param>
    protected ChannelWorkQueue(bool singleReader)
    {
        _channel = Channel.CreateUnbounded<WorkJob<TRequest, TProgress, TResult>>(
            new UnboundedChannelOptions { SingleReader = singleReader, SingleWriter = false });
    }

    public WorkJob<TRequest, TProgress, TResult> Enqueue(TRequest request, IProgress<TProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var job = new WorkJob<TRequest, TProgress, TResult>(request, progress);

        if (!_channel.Writer.TryWrite(job))
            throw new InvalidOperationException("The work queue has been closed.");

        return job;
    }

    public IAsyncEnumerable<WorkJob<TRequest, TProgress, TResult>> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public void Complete() => _channel.Writer.TryComplete();
}
