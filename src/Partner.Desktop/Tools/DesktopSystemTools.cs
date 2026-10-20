using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Partner.Core.Agent.Tools;
using Partner.Desktop.Interop;

namespace Partner.Desktop.Tools;

/// <summary>
/// The parts of the desktop that are not input: running commands and the clipboard.
/// <para>
/// These are the tools the middle approval gear stops. Everything here reaches past the window the model is
/// looking at — a command can delete a file the model never saw, a clipboard write changes what the user will
/// paste next.
/// </para>
/// </summary>
/// <summary>
/// Runs one command and returns its output. The sharpest tool here: it can do anything the user can do in a
/// shell, which is why the approval gears exist.
/// </summary>
public sealed class DesktopExecTool : ITool
{
    /// <summary>Long enough for a real command, short enough that a hung one does not park the turn.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Output beyond this is truncated: a runaway command must not fill the conversation.</summary>
    private const int MaxOutputChars = 8000;

    private readonly TimeSpan _timeout;

    /// <param name="timeout">
    /// Overridable so the timeout itself is testable.
    /// </param>
    public DesktopExecTool(TimeSpan? timeout = null) => _timeout = timeout ?? DefaultTimeout;

    public ToolSpec Spec { get; } = new(
        "desktop_exec",
        "执行一条命令并返回输出（cmd.exe 语义）。这是本机最强的能力：它能删除文件、改配置、跑脚本。"
        + "命令输出会被截断，超时 30 秒。",
        """
        {"type":"object","properties":{
          "command":{"type":"string","description":"要执行的命令行"},
          "workingDirectory":{"type":"string","description":"可选工作目录"}
        },"required":["command"],"additionalProperties":false}
        """);

    /// <summary>
    /// Runs one command and reports what it printed.
    /// <para>
    /// <b>The deadline covers the output, not just the process.</b> A command that leaves something behind
    /// holding the inherited pipe — <c>start /b</c>, a service launcher, anything that spawns a background
    /// child — makes <c>cmd.exe</c> exit immediately while the pipe stays open for as long as that child
    /// lives. Waiting for the <i>process</i> therefore returns at once and the read that follows blocks
    /// indefinitely. So the reads run under the same deadline as the wait, and each is drained
    /// incrementally rather than with <c>ReadToEndAsync</c>, which also keeps a runaway command from
    /// buffering megabytes before anything truncates them.
    /// </para>
    /// </summary>
    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var arguments = Json.Deserialize<ExecArguments>(argumentsJson);
        if (arguments is null || string.IsNullOrWhiteSpace(arguments.Command))
        {
            return new ToolResult(false, "缺少 command");
        }

        var info = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        info.ArgumentList.Add("/c");
        info.ArgumentList.Add(arguments.Command);

        if (!string.IsNullOrWhiteSpace(arguments.WorkingDirectory))
        {
            info.WorkingDirectory = arguments.WorkingDirectory;
        }

        using var process = new Process { StartInfo = info };

        // Started before the process so the budget covers starting it too: a launch that never completes is
        // exactly as capable of parking the turn as one that never exits.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);

        try
        {
            process.Start();
        }
        catch (Exception error)
        {
            return new ToolResult(false, $"无法启动命令：{error.Message}");
        }

        var outText = new OutputSink();
        var errText = new OutputSink();

        // Both pipes are drained concurrently and for the whole run. Draining only one deadlocks the other:
        // a child writing more than the pipe buffer holds blocks until somebody reads it.
        var outTask = outText.DrainAsync(process.StandardOutput, deadline.Token);
        var errTask = errText.DrainAsync(process.StandardError, deadline.Token);

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            KillQuietly(process);

            // Then close our ends of the pipes and wait for the drains to finish. With
            // `Kill(entireProcessTree: true)` above, a `start /b` survivor is reaped too, so the read observes
            // EOF and the handle goes with it. What this covers is the case the tree kill cannot reach: a
            // process that left the tree before the kill, or a launcher that outlives it. An anonymous pipe
            // read does not observe a cancelled token until the read itself completes, so without closing the
            // readers the drains would stay parked on them. Closing is deterministic; the wait is bounded so a
            // read that still will not return cannot reintroduce the hang this method exists to prevent.
            ClosePipes(process);
            await WaitForDrainsAsync(outTask, errTask).ConfigureAwait(false);

            // Whatever arrived before the deadline is the evidence for why the model reached for this
            // command, so it is reported rather than thrown away.
            return new ToolResult(false,
                $"命令超过 {_timeout.TotalSeconds:0} 秒未结束，已终止（进程树与管道均已关闭）"
                + Describe(outText, errText, timedOut: true));
        }

        return new ToolResult(
            process.ExitCode == 0,
            $"exit={process.ExitCode}\n{Describe(outText, errText, timedOut: false)}");
    }

    private const string Nothing = "(无输出)";
    private const string TruncationMarker = "…（已截断）";

    /// <summary>
    /// What the command left behind, capped and labelled. The cap is applied <i>while</i> reading rather than
    /// after, so the bound holds for the process and not only for the conversation.
    /// </summary>
    private static string Describe(OutputSink stdout, OutputSink stderr, bool timedOut)
    {
        var outText = stdout.Text;
        var errText = stderr.Text;

        var combined = string.IsNullOrWhiteSpace(errText)
            ? outText
            : $"{outText}\n[stderr]\n{errText}";

        if (combined.Length == 0)
        {
            return timedOut ? "；无输出" : Nothing;
        }

        if ((stdout.Clipped || stderr.Clipped) && !combined.EndsWith(TruncationMarker, StringComparison.Ordinal))
        {
            combined += TruncationMarker;
        }

        return timedOut ? $"\n截断前的输出：\n{combined}" : combined;
    }

    /// <summary>
    /// One pipe's worth of output: kept while it is read, never past the cap.
    /// </summary>
    private sealed class OutputSink
    {
        private readonly StringBuilder _text = new();
        private readonly char[] _buffer = new char[1024];

        public bool Clipped { get; private set; }

        public string Text => _text.ToString();

        /// <summary>
        /// Reads to EOF under <paramref name="ct"/>. The token is required: an unbounded read lets a
        /// backgrounded command hold the pipe open forever.
        /// </summary>
        public async Task DrainAsync(StreamReader reader, CancellationToken ct)
        {
            while (true)
            {
                var read = await reader.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    return;
                }

                if (_text.Length < MaxOutputChars)
                {
                    _text.Append(_buffer, 0, Math.Min(read, MaxOutputChars - _text.Length));
                }

                Clipped |= _text.Length >= MaxOutputChars;
            }
        }
    }

    /// <summary>
    /// Closes this process's ends of the child's pipes, which is what unblocks a drain that is parked on a
    /// handle a survivor still holds. Failures are ignored for the same reason the kill is: the tool is already
    /// returning a timeout, and a pipe that is already closed is not news.
    /// </summary>
    private static void ClosePipes(Process process)
    {
        try
        {
            process.StandardOutput.Close();
            process.StandardError.Close();
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or ObjectDisposedException)
        {
            // Already closed, or the child never got far enough to have pipes.
        }
    }

    /// <summary>
    /// Waits for the drains to finish, but not forever: closing the readers normally ends them at once, and this
    /// bound is only here so that a read which still will not return cannot park the turn again.
    /// </summary>
    private static async Task WaitForDrainsAsync(params Task[] drains)
    {
        try
        {
            await Task.WhenAll(drains).WaitAsync(DrainCloseGrace).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Either the drain faulted on a closed reader or the grace ran out. Both are the timeout path's
            // business, not an error to raise over the timeout the caller is about to be told about.
        }
    }

    /// <summary>How long a drain may take to notice its reader was closed.</summary>
    private static readonly TimeSpan DrainCloseGrace = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Kills the command and everything it started. A failure here is not worth reporting: the tool is
    /// already returning a timeout, and the process is usually gone by the time we ask.
    /// </summary>
    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or not ours to kill.
        }
    }

    private sealed record ExecArguments(string? Command, string? WorkingDirectory);
}

/// <summary>Reads the clipboard. Read-only, so it never needs approval.</summary>
public sealed class DesktopClipboardGetTool : ITool
{
    public ToolSpec Spec { get; } = new(
        "desktop_clipboard_get",
        "读取剪贴板里的文本（Ctrl+C 复制到的东西）。",
        """{"type":"object","properties":{},"additionalProperties":false}""");

    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        try
        {
            var text = await Clipboard.ReadAsync(ct).ConfigureAwait(false);
            return new ToolResult(true, string.IsNullOrEmpty(text) ? "(剪贴板为空或不是文本)" : text);
        }
        catch (Exception error)
        {
            return new ToolResult(false, $"读取剪贴板失败：{error.Message}");
        }
    }
}

/// <summary>Writes the clipboard. Changes what the user will paste next, so it is a dangerous tool.</summary>
public sealed class DesktopClipboardSetTool : ITool
{
    public ToolSpec Spec { get; } = new(
        "desktop_clipboard_set",
        "把文本写进剪贴板（随后可以用 Ctrl+V 粘贴）。",
        """
        {"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}
        """);

    public async Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default)
    {
        var arguments = Json.Deserialize<ClipboardArguments>(argumentsJson);
        if (arguments?.Text is null)
        {
            return new ToolResult(false, "缺少 text");
        }

        try
        {
            await Clipboard.WriteAsync(arguments.Text, ct).ConfigureAwait(false);
            return new ToolResult(true, $"已写入剪贴板（{arguments.Text.Length} 字符）");
        }
        catch (Exception error)
        {
            return new ToolResult(false, $"写入剪贴板失败：{error.Message}");
        }
    }

    private sealed record ClipboardArguments(string? Text);
}

/// <summary>
/// The Windows clipboard. It is a shared, single-owner resource: another process can hold it open, so every
/// call retries briefly instead of failing the tool on a transient lock.
/// </summary>
internal static class Clipboard
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    public static async Task<string> ReadAsync(CancellationToken ct)
    {
        await RetryAsync(() => NativeMethods.OpenClipboard(0), ct).ConfigureAwait(false);

        try
        {
            var handle = NativeMethods.GetClipboardData(CfUnicodeText);
            if (handle == 0)
            {
                return string.Empty;
            }

            var pointer = NativeMethods.GlobalLock(handle);
            if (pointer == 0)
            {
                return string.Empty;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer) ?? string.Empty;
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public static async Task WriteAsync(string text, CancellationToken ct)
    {
        await RetryAsync(() => NativeMethods.OpenClipboard(0), ct).ConfigureAwait(false);

        try
        {
            NativeMethods.EmptyClipboard();

            var bytes = (text.Length + 1) * 2;
            var handle = NativeMethods.GlobalAlloc(GmemMoveable, (nuint)bytes);
            if (handle == 0)
            {
                throw new InvalidOperationException("GlobalAlloc 失败");
            }

            var pointer = NativeMethods.GlobalLock(handle);
            if (pointer == 0)
            {
                NativeMethods.GlobalFree(handle);
                throw new InvalidOperationException("GlobalLock 失败");
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                Marshal.WriteInt16(pointer, text.Length * 2, 0);
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }

            if (NativeMethods.SetClipboardData(CfUnicodeText, handle) == 0)
            {
                NativeMethods.GlobalFree(handle);
                throw new InvalidOperationException("SetClipboardData 失败");
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    /// <summary>The clipboard is owned by one process at a time; a brief retry beats failing the tool.</summary>
    private static async Task RetryAsync(Func<bool> attempt, CancellationToken ct)
    {
        for (var tries = 0; tries < 20; tries++)
        {
            if (attempt())
            {
                return;
            }

            await Task.Delay(25, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("剪贴板被别的程序占用");
    }
}

/// <summary>Shared JSON options for the tool arguments.</summary>
internal static class Json
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static T? Deserialize<T>(string json) where T : class
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
