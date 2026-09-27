using Partner.Core.Agent.Tools;
using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>
/// The tools that reach past the GUI. They are the ones the middle approval gear stops, so their behaviour is
/// worth pinning without a desktop: what they do with bad input, and whether they report what they ran.
/// </summary>
public sealed class DesktopSystemToolTests
{
    [Fact]
    public async Task Exec_runs_a_command_and_reports_its_output_and_exit_code()
    {
        var tool = new DesktopExecTool();

        var result = await tool.InvokeAsync("""{"command":"echo desktop-exec-works"}""");

        Assert.True(result.Ok);
        Assert.Contains("desktop-exec-works", result.Content, StringComparison.Ordinal);
        Assert.Contains("exit=0", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exec_reports_a_failure_as_a_failure()
    {
        // A command that fails is not an exception; it is a result the model has to read.
        var result = await new DesktopExecTool().InvokeAsync("""{"command":"exit 3"}""");

        Assert.False(result.Ok);
        Assert.Contains("exit=3", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exec_without_a_command_says_so()
    {
        var result = await new DesktopExecTool().InvokeAsync("{}");

        Assert.False(result.Ok);
        Assert.Contains("command", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Launch_without_a_target_says_so()
    {
        var result = await new DesktopLaunchTool().InvokeAsync("{}");

        Assert.False(result.Ok);
        Assert.Contains("target", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_launch_that_cannot_start_reports_the_reason()
    {
        // Not a throw: the model gets a result it can act on, and the turn continues.
        var result = await new DesktopLaunchTool()
            .InvokeAsync("""{"target":"definitely-not-a-real-program-9f3a"}""");

        Assert.False(result.Ok);
        Assert.Contains("启动失败", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void The_clipboard_tools_are_classified_by_what_they_change()
    {
        // Reading is free, writing changes what the user will paste next.
        Assert.False(DesktopToolCatalog.IsDangerous("desktop_clipboard_get"));
        Assert.True(DesktopToolCatalog.IsDangerous("desktop_clipboard_set"));
        Assert.True(DesktopToolCatalog.IsReadOnly("desktop_clipboard_get"));
        Assert.False(DesktopToolCatalog.IsReadOnly("desktop_clipboard_set"));
    }
}
