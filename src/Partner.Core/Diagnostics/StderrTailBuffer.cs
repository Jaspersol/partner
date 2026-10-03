namespace Partner.Core.Diagnostics;

/// <summary>
/// The last lines a child process wrote to stderr, kept so a failure can quote the reason the process itself
/// gave instead of only the exception that noticed it.
/// <para>
/// Bounded on purpose: a server that logs continuously would otherwise be piped into memory without limit.
/// The tail is what a diagnosis needs; the rest of it is in the process's own logs.
/// </para>
/// <para>
/// One implementation shared by the MCP stdio client (<c>Partner.Core</c>) and the game process adapter
/// (<c>Partner.Game</c>): the two pipe a child's stderr the same way, and a second copy is a second place for
/// the drain to be got wrong.
/// </para>
/// </summary>
public sealed class StderrTailBuffer
{
    /// <summary>How many lines to keep. Enough to quote the reason, short enough to stay in memory.</summary>
    public const int Capacity = 100;

    private readonly Queue<string> _lines = new();
    private readonly object _gate = new();

    /// <summary>
    /// Reads until the pipe closes. Both shutdown and a broken pipe are normal ends rather than failures —
    /// the caller's error path does not depend on this loop.
    /// </summary>
    public async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);

        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                lock (_gate)
                {
                    _lines.Enqueue(line);
                    while (_lines.Count > Capacity)
                    {
                        _lines.Dequeue();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // stderr closed; nothing downstream depends on it.
        }
    }

    /// <summary>
    /// The captured tail as a suffix to append to a failure message, or empty when nothing was captured. The
    /// leading space is deliberate, so callers concatenate it straight onto a sentence.
    /// </summary>
    public string Tail()
    {
        lock (_gate)
        {
            return _lines.Count == 0
                ? string.Empty
                : " stderr: " + string.Join(" | ", _lines);
        }
    }
}
