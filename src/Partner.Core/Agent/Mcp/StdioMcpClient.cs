using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Partner.Core.Diagnostics;

namespace Partner.Core.Agent.Mcp;

/// <summary>
/// 基于子进程 stdin/stdout 的 MCP 客户端（设计文档 §2.6.3）。
/// <para>
/// 传输是<b>换行分隔</b>的 UTF-8 JSON-RPC 2.0（一行一个 JSON 对象），不是 LSP 的 Content-Length 分帧。
/// 请求并发：每个请求分配递增 id，响应按 id 匹配（与到达顺序无关）；stderr 持续排空并保留最后若干行，
/// 失败时拼进异常信息，避免子进程因管道写满而阻塞。
/// </para>
/// </summary>
public sealed class StdioMcpClient : IMcpClient
{
    private const string ProtocolVersion = "2024-11-05";
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);

    private readonly McpServerConfig _config;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly StderrTailBuffer _stderr = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _listLock = new(1, 1);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private volatile Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private long _nextId;
    private bool _disposed;

    private IReadOnlyList<McpToolDescriptor>? _cachedTools;
    private DateTimeOffset _cachedToolsAt;

    /// <param name="config">服务器配置。</param>
    /// <param name="clock">用于能力缓存 TTL 的时钟；默认 <see cref="TimeProvider.System"/>。</param>
    public StdioMcpClient(McpServerConfig config, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            var process = _process;
            if (process is null)
            {
                return false;
            }

            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public string ServerName => _config.Name;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _cachedTools = null;

            var process = new Process { StartInfo = BuildStartInfo() };
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException($"Failed to start MCP server '{_config.Name}'.");
                }
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                process.Dispose();
                throw new InvalidOperationException(
                    $"Failed to start MCP server '{_config.Name}' ({_config.Command}): {ex.Message}", ex);
            }

            _process = process;
            _stdoutTask = ReadStdoutAsync(process.StandardOutput, cts.Token);
            _stderrTask = _stderr.DrainAsync(process.StandardError, cts.Token);

            try
            {
                var parameters = new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "partner",
                        ["version"] = "1",
                    },
                };

                var response = await SendRequestAsync("initialize", parameters, ct).ConfigureAwait(false);
                EnsureNoError("initialize", response);

                await SendNotificationAsync("notifications/initialized", null, ct).ConfigureAwait(false);
            }
            catch
            {
                StopCore();
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken ct = default)
    {
        ThrowIfNotRunning();

        await _listLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cached = _cachedTools;
            if (cached is not null && _clock.GetUtcNow() - _cachedToolsAt < _config.CapabilityCacheTtl)
            {
                return cached;
            }

            var response = await SendRequestAsync("tools/list", new JsonObject(), ct).ConfigureAwait(false);
            EnsureNoError("tools/list", response);

            var tools = new List<McpToolDescriptor>();
            if (response.TryGetProperty("result", out var result)
                && result.TryGetProperty("tools", out var array)
                && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var tool in array.EnumerateArray())
                {
                    var name = tool.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                    var description = tool.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                    var schema = tool.TryGetProperty("inputSchema", out var s) ? s.GetRawText() : "{}";
                    tools.Add(new McpToolDescriptor(name, description, schema));
                }
            }

            _cachedTools = tools;
            _cachedToolsAt = _clock.GetUtcNow();
            return tools;
        }
        finally
        {
            _listLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<McpCallResult> CallToolAsync(string toolName, string argumentsJson, CancellationToken ct = default)
    {
        ThrowIfNotRunning();
        ArgumentException.ThrowIfNullOrEmpty(toolName);

        var arguments = string.IsNullOrWhiteSpace(argumentsJson)
            ? new JsonObject()
            : JsonNode.Parse(argumentsJson);

        var parameters = new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = arguments ?? new JsonObject(),
        };

        var response = await SendRequestAsync("tools/call", parameters, ct).ConfigureAwait(false);

        if (response.TryGetProperty("error", out var error))
        {
            return new McpCallResult(false, DescribeError(error), true);
        }

        if (!response.TryGetProperty("result", out var result))
        {
            return new McpCallResult(false, $"MCP server '{_config.Name}' returned no result for tool '{toolName}'.", true);
        }

        var isError = result.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new McpCallResult(!isError, JoinTextContent(result), isError);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            StopCore();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await StopAsync().ConfigureAwait(false);

        foreach (var task in new[] { _stdoutTask, _stderrTask })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 收尾读取任务的异常已经通过 FailAllPending 反馈给调用方。
            }
        }

        _cts?.Dispose();
        _writeLock.Dispose();
        _listLock.Dispose();
        _lifecycleLock.Dispose();
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var info = new ProcessStartInfo
        {
            FileName = _config.Command,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        foreach (var argument in _config.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrEmpty(_config.WorkingDirectory))
        {
            info.WorkingDirectory = _config.WorkingDirectory;
        }

        if (_config.Environment is not null)
        {
            foreach (var pair in _config.Environment)
            {
                info.Environment[pair.Key] = pair.Value;
            }
        }

        return info;
    }

    private void StopCore()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 进程可能已经退出；关闭 stdin 只是为了触发子进程的 EOF 退出。
        }

        try
        {
            if (!process.WaitForExit((int)ShutdownGrace.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // 进程已经退出或无法等待；无需再处理。
        }

        FailAllPending(new InvalidOperationException($"MCP server '{_config.Name}' is not running."));

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放。
        }

        process.Dispose();
    }

    private async Task<JsonElement> SendRequestAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        ThrowIfNotRunning();

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };

        if (parameters is not null)
        {
            request["params"] = parameters;
        }

        try
        {
            await WriteLineAsync(request.ToJsonString(), ct).ConfigureAwait(false);
            return await completion.Task.WaitAsync(_config.CallTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException(
                $"MCP request '{method}' to server '{_config.Name}' timed out after {_config.CallTimeout}.{_stderr.Tail()}");
        }
        catch (Exception)
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    private async Task SendNotificationAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        ThrowIfNotRunning();

        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };

        if (parameters is not null)
        {
            notification["params"] = parameters;
        }

        await WriteLineAsync(notification.ToJsonString(), ct).ConfigureAwait(false);
    }

    private async Task WriteLineAsync(string line, CancellationToken ct)
    {
        var process = _process ?? throw NotRunning();
        var stdin = process.StandardInput;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stdin.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await stdin.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Failed to write to MCP server '{_config.Name}': {ex.Message}", ex);
        }
        finally
        {
            _writeLock.Release();
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
                    DispatchLine(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            failure = ex;
        }
        finally
        {
            FailAllPending(failure ?? new InvalidOperationException(
                $"MCP server '{_config.Name}' closed its stdout."));
        }
    }

    private void DispatchLine(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out var idElement)
                || idElement.ValueKind != JsonValueKind.Number
                || !idElement.TryGetInt64(out var id))
            {
                return;
            }

            if (_pending.TryRemove(id, out var completion))
            {
                completion.TrySetResult(root.Clone());
            }
        }
    }

    private void FailAllPending(Exception error)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var completion))
            {
                completion.TrySetException(error);
            }
        }
    }

    private void ThrowIfNotRunning()
    {
        if (!IsRunning)
        {
            throw NotRunning();
        }
    }

    private InvalidOperationException NotRunning() =>
        new($"MCP server '{_config.Name}' is not running.");

    private static void EnsureNoError(string method, JsonElement response)
    {
        if (response.TryGetProperty("error", out var error))
        {
            var code = error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var c)
                && c.TryGetInt32(out var number)
                    ? $" (code {number})"
                    : string.Empty;

            throw new InvalidOperationException(
                $"MCP server returned an error for '{method}': {DescribeError(error)}{code}");
        }
    }

    private static string DescribeError(JsonElement error) =>
        error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)
            ? message.GetString() ?? string.Empty
            : error.ToString();

    private static string JoinTextContent(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type)
                && type.GetString() == "text"
                && item.TryGetProperty("text", out var text))
            {
                parts.Add(text.GetString() ?? string.Empty);
            }
        }

        return string.Join("\n", parts);
    }
}
