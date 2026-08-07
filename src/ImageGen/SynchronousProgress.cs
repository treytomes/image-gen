namespace ImageGen;

/// <summary>
/// An <see cref="IProgress{T}"/> that invokes the handler synchronously on the
/// calling thread, unlike <see cref="Progress{T}"/> which marshals reports to the
/// captured <see cref="SynchronizationContext"/> or the thread pool.
///
/// This matters for CPU-bound work (image generation) that saturates every core:
/// the native library raises progress on its own worker thread, and posting to the
/// starved thread pool would delay every update until the work finished. Handling
/// inline lets the UI refresh in real time from the thread that has the CPU.
/// </summary>
public sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    /// <inheritdoc/>
    public void Report(T value) => handler(value);
}
