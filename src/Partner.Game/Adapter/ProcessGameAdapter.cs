using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Partner.Core.Diagnostics;

namespace Partner.Game.Adapter;

/// <summary>Configuration for <see cref="ProcessGameAdapter"/>.</summary>
public sealed record ProcessGameAdapterOptions
{
    public required string GameId { get; init; }

    /// <summary>Executable to spawn, e.g. <c>"dotnet"</c>.</summary>
    public required string Command { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    public string? WorkingDirectory { get; init; }

    /// <summary>Upper bound for a single observe/act round trip.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Game adapter over a child process (design doc §2.7): newline-delimited JSON on the child's
/// stdin/stdout, UTF-8, one request in flight at a time. The protocol is strictly
/// request/response, so a <see cref="SemaphoreSlim"/> serialises callers instead of letting
/// them interleave lines on the pipe.
/// </summary>
public sealed class ProcessGameAdapter : IGameAdapter
{
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);

    private readonly ProcessGameAdapterOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly StderrTailBuffer _stderr = new();
    private readonly object _sync = new();
    private readonly List<double> _latencySamples = new();

    private volatile Process? _process;
    private volatile bool _dead;
    private volatile bool _disposed;
    private CancellationTokenSource? _cts;
    private Task? _stdoutTask;
    private Task? _stderrTask;

    /// <summary>A response that is still awaited; the reader loop hands the next line to it.</summary>
    private TaskCompletionSource<string>? _pending;

    /// <summary>Responses abandoned by a timed-out call, still to arrive (or never).</summary>
    private int _orphans;

    public ProcessGameAdapter(ProcessGameAdapterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.GameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Command);

        if (options.CallTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.CallTimeout, "CallTimeout must be positive.");
        }

        _options = options;
    }

    /// <inheritdoc />
    public string GameId => _options.GameId;

    /// <inheritdoc />
    public bool IsAttached
    {
        get
        {
            var process = _process;
            return process is not null && !_dead && !HasExited(process);
        }
    }

    /// <inheritdoc />
    public LatencyStats Latency
    {
        get
        {
            double[] samples;
            lock (_sync)
            {
                samples = _latencySamples.ToArray();
            }

            if (samples.Length == 0)
            {
                return new LatencyStats(0, 0, 0, 0);
            }

            Array.Sort(samples);
            return new LatencyStats(
                Percentile(samples, 0.50),
                Percentile(samples, 0.99),
                samples[^1],
                samples.Length);
        }
    }

    /// <inheritdoc />
    public async Task AttachAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsAttached)
            {
                return;
            }

            var process = new Process { StartInfo = BuildStartInfo() };
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException($"Failed to start game '{GameId}'.");
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                process.Dispose();
                throw new InvalidOperationException(
                    $"Failed to start game '{GameId}' ({_options.Command}): {ex.Message}", ex);
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _process = process;
            _dead = false;
            process.StandardInput.NewLine = "\n"; // The protocol is newline-delimited: LF, not the platform default.
            lock (_sync)
            {
                _pending = null;
                _orphans = 0;
            }

            _stdoutTask = ReadStdoutAsync(process.StandardOutput, cts.Token);
            _stderrTask = _stderr.DrainAsync(process.StandardError, cts.Token);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task DetachAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DetachCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<GameStateSnapshot> ObserveAsync(CancellationToken ct = default) =>
        ObserveCoreAsync(ct);

    /// <inheritdoc />
    public async Task<GameActionOutcome> ActAsync(GameActionRequest action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        var parameters = new JsonObject();
        if (action.Parameters is not null)
        {
            foreach (var (key, value) in action.Parameters)
            {
                parameters[key] = value;
            }
        }

        var payload = new JsonObject
        {
            ["cmd"] = "act",
            ["kind"] = action.Kind,
            ["params"] = parameters,
        };

        var stopwatch = Stopwatch.StartNew();
        var line = await SendAsync("act", payload, ct).ConfigureAwait(false);
        stopwatch.Stop();

        using var document = ParseResponse("act", line);
        var root = document.RootElement;

        var ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
        var detail = root.TryGetProperty("detail", out var detailElement) && detailElement.ValueKind == JsonValueKind.String
            ? detailElement.GetString() ?? string.Empty
            : string.Empty;

        return new GameActionOutcome
        {
            Ok = ok,
            Detail = detail,
            LatencyMs = stopwatch.Elapsed.TotalMilliseconds,
            After = TryReadSnapshot(root, DateTimeOffset.UtcNow),
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await DetachAsync().ConfigureAwait(false);

        _gate.Dispose();
        _lifecycleLock.Dispose();
    }

    private async Task<GameStateSnapshot> ObserveCoreAsync(CancellationToken ct)
    {
        var payload = new JsonObject { ["cmd"] = "observe" };

        var stopwatch = Stopwatch.StartNew();
        var line = await SendAsync("observe", payload, ct).ConfigureAwait(false);
        stopwatch.Stop();

        using var document = ParseResponse("observe", line);
        var snapshot = TryReadSnapshot(document.RootElement, DateTimeOffset.UtcNow);
        return snapshot
            ?? throw new InvalidOperationException(
                $"Game '{GameId}' returned no state for 'observe': {line}{_stderr.Tail()}");
    }

    private async Task<string> SendAsync(string command, JsonObject payload, CancellationToken ct)
    {
        EnsureAttached();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            EnsureAttached();
            var process = _process!;

            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _pending = completion;
            }

            try
            {
                await WriteLineAsync(process, payload.ToJsonString(), ct).ConfigureAwait(false);
                var line = await completion.Task.WaitAsync(_options.CallTimeout, ct).ConfigureAwait(false);
                stopwatch.Stop();
                RecordLatency(stopwatch.Elapsed.TotalMilliseconds);
                return line;
            }
            catch (TimeoutException)
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_pending, completion))
                    {
                        _pending = null;
                        _orphans++;
                    }
                }

                throw new TimeoutException(
                    $"Game command '{command}' to '{GameId}' timed out after {_options.CallTimeout}.{_stderr.Tail()}");
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_pending, completion))
                    {
                        _pending = null;
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteLineAsync(Process process, string line, CancellationToken ct)
    {
        try
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _dead = true;
            throw new InvalidOperationException(
                $"Failed to write to game '{GameId}': {ex.Message}{_stderr.Tail()}", ex);
        }
    }

    private async Task ReadStdoutAsync(StreamReader reader, CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length > 0)
                {
                    Deliver(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            failure = ex;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                _dead = true;
                FailPending(failure is null
                    ? new InvalidOperationException($"Game '{GameId}' closed its stdout.{_stderr.Tail()}")
                    : new InvalidOperationException(
                        $"Game '{GameId}' closed its stdout: {failure.Message}{_stderr.Tail()}", failure));
            }
        }
    }

    /// <summary>Hands a response line to the awaited call; lines whose call already gave up are dropped.</summary>
    private void Deliver(string line)
    {
        TaskCompletionSource<string>? completion;
        lock (_sync)
        {
            if (_orphans > 0)
            {
                _orphans--;
                return;
            }

            completion = _pending;
            _pending = null;
        }

        completion?.TrySetResult(line);
    }

    private void FailPending(Exception error)
    {
        TaskCompletionSource<string>? completion;
        lock (_sync)
        {
            completion = _pending;
            _pending = null;
        }

        completion?.TrySetException(error);
    }

    private async Task DetachCoreAsync(CancellationToken ct)
    {
        var process = _process;
        _process = null;
        _dead = true;

        if (process is not null)
        {
            try
            {
                await process.StandardInput.WriteLineAsync("{\"cmd\":\"bye\"}").ConfigureAwait(false);
                await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
                // The child may already be gone; closing stdin below is enough.
            }

            try
            {
                process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Already closed.
            }

            try
            {
                if (!process.WaitForExit((int)ShutdownGrace.TotalMilliseconds))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Already exited or not killable.
            }

            process.Dispose();
        }

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        foreach (var task in new[] { _stdoutTask, _stderrTask })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.WaitAsync(ShutdownGrace).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or InvalidOperationException)
            {
                // A half-dead child must not block detach.
            }
        }

        _stdoutTask = null;
        _stderrTask = null;
        _cts?.Dispose();
        _cts = null;

        FailPending(new InvalidOperationException($"Game '{GameId}' is detached."));
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var info = new ProcessStartInfo
        {
            FileName = _options.Command,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        foreach (var argument in _options.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrEmpty(_options.WorkingDirectory))
        {
            info.WorkingDirectory = _options.WorkingDirectory;
        }

        return info;
    }

    private static JsonDocument ParseResponse(string command, string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Game returned a non-JSON response for '{command}': {line}", ex);
        }
    }

    private static GameStateSnapshot? TryReadSnapshot(JsonElement root, DateTimeOffset at)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("values", out var values)
            || values.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in values.EnumerateObject())
        {
            map[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
        }

        var seq = root.TryGetProperty("seq", out var seqElement) && seqElement.ValueKind == JsonValueKind.Number
            && seqElement.TryGetInt32(out var parsedSeq)
                ? parsedSeq
                : 0;

        return new GameStateSnapshot { Seq = seq, At = at, Values = map };
    }

    private void RecordLatency(double milliseconds)
    {
        lock (_sync)
        {
            _latencySamples.Add(milliseconds);
        }
    }

    private void EnsureAttached()
    {
        if (!IsAttached)
        {
            throw new InvalidOperationException(
                $"Game adapter '{GameId}' is not attached; call AttachAsync first.");
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

}
