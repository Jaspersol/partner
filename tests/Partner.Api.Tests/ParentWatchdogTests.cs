using System.Diagnostics;
using Partner.Api.Auth;

namespace Partner.Api.Tests;

/// <summary>
/// The parent watchdog is the difference between "the shell crashed" and "the shell crashed and left an API
/// holding the port and the SQLite lock". It is exercised by running the real executable, because the
/// behaviour lives in its entry point rather than in the composition root.
/// </summary>
public sealed class ParentWatchdogTests
{
    [Fact]
    public async Task The_api_exits_when_its_parent_closes_the_stdin_pipe()
    {
        await using var child = StartApi(watchParent: true);

        await WaitForReadinessAsync(child);

        // This is what a launcher dying looks like from the child's side.
        child.Process.StandardInput.Close();

        Assert.True(
            await ExitsWithinAsync(child.Process, TimeSpan.FromSeconds(15)),
            "the API kept running after its parent closed the pipe");
    }

    [Fact]
    public async Task The_api_keeps_running_when_the_watchdog_is_not_requested()
    {
        await using var child = StartApi(watchParent: false);

        await WaitForReadinessAsync(child);

        // A redirected stdin reaches EOF immediately under a harness; that must not be read as "parent gone".
        child.Process.StandardInput.Close();
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.False(child.Process.HasExited, "the API exited without being asked to watch its parent");
    }

    private static ApiProcess StartApi(bool watchParent)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "partner-watchdog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);

        var start = new ProcessStartInfo(ApiExecutable())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment =
            {
                [TokenAuth.EnvVar] = "watchdog-test-token",
            },
        };

        start.ArgumentList.Add(workspace);
        start.ArgumentList.Add("0");

        if (watchParent)
        {
            start.Environment["PARTNER_API_PARENT_WATCH"] = "1";
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException("could not start the API");
        return new ApiProcess(process, workspace);
    }

    private static async Task WaitForReadinessAsync(ApiProcess child)
    {
        // ASP.NET's own startup log shares stdout, so the readiness line is not necessarily first.
        for (var line = 0; line < 50; line++)
        {
            var text = await child.Process.StandardOutput.ReadLineAsync();
            if (text is null)
            {
                break;
            }

            if (text.Contains("listening on", StringComparison.Ordinal))
            {
                return;
            }
        }

        Assert.Fail("the API never reported readiness on stdout");
    }

    private static async Task<bool> ExitsWithinAsync(Process process, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);

        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Locates the built executable. The framework folder follows the project's target framework, and a stale
    /// one can linger after a retarget, so the newest build wins rather than a hardcoded folder name.
    /// </summary>
    private static string ApiExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Partner.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("could not locate the solution root from " + AppContext.BaseDirectory);
        }

        var debugRoot = Path.Combine(directory.FullName, "src", "Partner.Api", "bin", "Debug");

        if (!Directory.Exists(debugRoot))
        {
            throw new InvalidOperationException($"missing {debugRoot} — build the solution first");
        }

        var candidates = Directory
            .EnumerateDirectories(debugRoot)
            .Select(folder => Path.Combine(folder, "partner-api.exe"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        return candidates.Count > 0
            ? candidates[0]
            : throw new InvalidOperationException($"no partner-api.exe under {debugRoot} — build the solution first");
    }

    /// <summary>Kills the child if a test leaves it running, and removes its workspace.</summary>
    private sealed class ApiProcess(Process process, string workspace) : IAsyncDisposable
    {
        public Process Process { get; } = process;

        public async ValueTask DisposeAsync()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync();
            }

            Process.Dispose();

            if (!Directory.Exists(workspace))
            {
                return;
            }

            // Windows can hold the SQLite handle (memory.db-shm) for a moment after the process is gone, so a
            // single delete is a race: it fails the test in its own cleanup, which says nothing about the code.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    Directory.Delete(workspace, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 19)
                {
                    await Task.Delay(100);
                }
                catch (UnauthorizedAccessException) when (attempt < 19)
                {
                    await Task.Delay(100);
                }
            }
        }
    }
}
