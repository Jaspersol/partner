using Partner.Core.Agent.Tools;

namespace Partner.Desktop.Tools;

/// <summary>
/// Serializes mutations of the process-wide interactive desktop across tool instances and HTTP callers.
/// Readers do not take this lock. The owner holds it until the actual operation has finished, even if
/// its caller stops waiting; cancellation only removes a waiter or cancels work that supports it.
/// </summary>
public static class DesktopMutationLock
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfCancelledOrUnauthorized(cancellationToken);
        }
        catch
        {
            Gate.Release();
            throw;
        }

        return new Releaser();
    }

    internal static void ThrowIfCancelledOrUnauthorized(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ToolExecutionAuthorization.ThrowIfChanged();
    }

    private sealed class Releaser : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Gate.Release();
            }
        }
    }
}
