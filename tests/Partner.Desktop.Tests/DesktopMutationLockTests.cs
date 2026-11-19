using Partner.Core.Agent.Tools;
using Partner.Desktop.Tools;
using Partner.Desktop.Interop;

namespace Partner.Desktop.Tests;

public sealed class DesktopMutationLockTests
{
    [Fact]
    public async Task Mutating_tools_from_independent_instances_share_the_gate_but_reads_do_not()
    {
        using var firstAutomation = new DesktopAutomation();
        using var secondAutomation = new DesktopAutomation();
        ITool[] mutations =
        [
            new DesktopActTool(firstAutomation, DesktopToolPolicy.Any, null!),
            new DesktopWindowTool(secondAutomation, DesktopToolPolicy.Any),
            new DesktopLaunchTool(),
            new DesktopExecTool(),
            new DesktopClipboardSetTool(),
        ];

        using var holder = await DesktopMutationLock.AcquireAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = mutations.Select(tool => tool.InvokeAsync("{}", cancellation.Token)).ToArray();
        try
        {
            Assert.All(pending, task => Assert.False(task.IsCompleted));
            var read = await new DesktopWaitTool(secondAutomation).InvokeAsync("{}").WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(read.Ok);
        }
        finally
        {
            cancellation.Cancel();
        }

        foreach (var task in pending)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
    }

    [Fact]
    public async Task Timed_out_caller_does_not_release_a_still_running_mutation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> WorkAsync()
        {
            using var held = await DesktopMutationLock.AcquireAsync();
            started.SetResult();
            await finish.Task;
            return 42;
        }

        var work = WorkAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => work.WaitAsync(TimeSpan.FromMilliseconds(50)));
            using var cancel = new CancellationTokenSource();
            var next = DesktopMutationLock.AcquireAsync(cancel.Token).AsTask();
            Assert.False(next.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        }
        finally
        {
            finish.TrySetResult();
        }

        Assert.Equal(42, await work.WaitAsync(TimeSpan.FromSeconds(2)));
        using var after = await DesktopMutationLock.AcquireAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Exception_and_repeated_disposal_do_not_lose_or_duplicate_the_permit()
    {
        async Task FailAsync()
        {
            using var held = await DesktopMutationLock.AcquireAsync();
            throw new InvalidOperationException("failed action");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(FailAsync);
        var first = await DesktopMutationLock.AcquireAsync();
        first.Dispose();
        first.Dispose();
        using var second = await DesktopMutationLock.AcquireAsync();
        using var cancel = new CancellationTokenSource();
        var waiting = DesktopMutationLock.AcquireAsync(cancel.Token).AsTask();
        Assert.False(waiting.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task Permission_is_rechecked_after_waiting_for_the_desktop()
    {
        var enabled = true;
        using var automation = new DesktopAutomation();
        ITool[] tools =
        [
            new DesktopExecTool(isEnabled: () => enabled),
            new DesktopActTool(automation, DesktopToolPolicy.Any, null!, isEnabled: () => enabled),
            new DesktopWindowTool(automation, DesktopToolPolicy.Any, () => enabled),
            new DesktopClipboardSetTool(() => enabled),
            new DesktopLaunchTool(isEnabled: () => enabled),
        ];
        using var holder = await DesktopMutationLock.AcquireAsync();
        var pending = tools.Select(tool => tool.InvokeAsync("{}")).ToArray();
        var injectedLaunch = new DesktopLaunchTool().InvokeWhenEnabledAsync("{}", () => enabled);
        Assert.False(injectedLaunch.IsCompleted);
        Assert.All(pending, task => Assert.False(task.IsCompleted));
        enabled = false;
        holder.Dispose();

        var results = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.All(results, result => Assert.False(result.Ok));
        Assert.Contains("命令执行已关闭", results[0].Content, StringComparison.Ordinal);
        Assert.All(results.Skip(1), result => Assert.Contains("桌面能力未启用", result.Content, StringComparison.Ordinal));
        var injectedResult = await injectedLaunch.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(injectedResult.Ok);
        Assert.Contains("桌面能力未启用", injectedResult.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revoked_governance_prevents_every_queued_mutating_tool_from_executing()
    {
        var authorized = true;
        using var automation = new DesktopAutomation();
        ITool[] tools =
        [
            new DesktopActTool(automation, DesktopToolPolicy.Any, null!),
            new DesktopWindowTool(automation, DesktopToolPolicy.Any),
            new DesktopLaunchTool(),
            new DesktopExecTool(),
            new DesktopClipboardSetTool(),
        ];

        using var holder = await DesktopMutationLock.AcquireAsync();
        using var authorization = ToolExecutionAuthorization.Enter(() => authorized);
        var pending = tools.Select(tool => tool.InvokeAsync("{}")).ToArray();
        try
        {
            Assert.All(pending, task => Assert.False(task.IsCompleted));
            authorized = false;
        }
        finally
        {
            holder.Dispose();
        }

        foreach (var task in pending)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Contains("原审批不再有效", error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_or_revoked_queued_command_never_creates_its_output(bool revoke)
    {
        var output = Path.Combine(Path.GetTempPath(), $"partner-mutation-{Guid.NewGuid():N}.txt");
        var command = System.Text.Json.JsonSerializer.Serialize(new { command = $"echo executed > \"{output}\"" });
        using var cancellation = new CancellationTokenSource();
        var authorized = true;
        using var holder = await DesktopMutationLock.AcquireAsync();
        using var authorization = ToolExecutionAuthorization.Enter(() => authorized);
        var pending = new DesktopExecTool().InvokeAsync(command, cancellation.Token);
        try
        {
            Assert.False(pending.IsCompleted);
            if (revoke)
            {
                authorized = false;
            }
            else
            {
                cancellation.Cancel();
            }
        }
        finally
        {
            holder.Dispose();
        }

        try
        {
            if (revoke)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.Contains("原审批不再有效", error.Message, StringComparison.Ordinal);
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
            }

            Assert.False(File.Exists(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Governance_revoked_after_gate_acquisition_still_prevents_command_execution()
    {
        var output = Path.Combine(Path.GetTempPath(), $"partner-mutation-{Guid.NewGuid():N}.txt");
        var command = System.Text.Json.JsonSerializer.Serialize(new { command = $"echo executed > \"{output}\"" });
        var checks = 0;
        using var authorization = ToolExecutionAuthorization.Enter(() => ++checks == 1);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new DesktopExecTool().InvokeAsync(command));
            Assert.Contains("原审批不再有效", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Cancellation_after_gate_acquisition_still_prevents_command_execution()
    {
        var output = Path.Combine(Path.GetTempPath(), $"partner-mutation-{Guid.NewGuid():N}.txt");
        var command = System.Text.Json.JsonSerializer.Serialize(new { command = $"echo executed > \"{output}\"" });
        using var cancellation = new CancellationTokenSource();
        var checks = 0;
        var tool = new DesktopExecTool(isEnabled: () =>
        {
            if (++checks == 2)
            {
                cancellation.Cancel();
            }
            return true;
        });
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.InvokeAsync(command, cancellation.Token));
            Assert.False(File.Exists(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    private sealed class LiveDesktopTheoryAttribute : TheoryAttribute
    {
        public LiveDesktopTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("PARTNER_DESKTOP_LIVE") != "1")
            {
                Skip = "live desktop test: set PARTNER_DESKTOP_LIVE=1 to run";
            }
        }
    }

    [LiveDesktopTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clipboard_write_waiting_for_system_clipboard_honors_cancellation_and_revocation(bool revoke)
    {
        using var release = new ManualResetEventSlim();
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = Task.Run(() =>
        {
            var acquired = NativeMethods.OpenClipboard(0);
            opened.SetResult(acquired);
            if (!acquired)
            {
                return;
            }

            try { release.Wait(TimeSpan.FromSeconds(5)); }
            finally { NativeMethods.CloseClipboard(); }
        });

        try
        {
            Assert.True(await opened.Task.WaitAsync(TimeSpan.FromSeconds(2)), "Could not hold the clipboard for the test");
            using var cancellation = new CancellationTokenSource();
            var authorized = true;
            using var authorization = ToolExecutionAuthorization.Enter(() => authorized);
            var pending = new DesktopClipboardSetTool().InvokeAsync("""{"text":"should not be written"}""", cancellation.Token);
            Assert.False(pending.IsCompleted);
            if (revoke)
            {
                authorized = false;
            }
            else
            {
                cancellation.Cancel();
            }
            release.Set();
            await owner.WaitAsync(TimeSpan.FromSeconds(2));

            if (revoke)
            {
                var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(result.Ok);
                Assert.Contains("原审批不再有效", result.Content, StringComparison.Ordinal);
            }
            else
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
            }
        }
        finally
        {
            release.Set();
            await owner.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }
}
