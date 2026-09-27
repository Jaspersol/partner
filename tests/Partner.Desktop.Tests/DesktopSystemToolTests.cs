using Partner.Core.Agent.Tools;
using Partner.Core.Media;
using Partner.Desktop.Capture;
using Partner.Desktop.Tools;

namespace Partner.Desktop.Tests;

/// <summary>
/// The tools that reach past the GUI. They are the ones the middle approval gear stops, so their behaviour is
/// worth pinning without a desktop: what they do with bad input, and whether they report what they ran.
/// </summary>
public sealed class DesktopSystemToolTests
{
    [Fact]
    public async Task The_screenshot_result_names_the_window_the_mapping_and_the_call_to_make()
    {
        // What the model has to act on. The old message said "coordinates are relative to the window" and never
        // named the window — so the model sent coordinates alone and every click was refused, four times, until
        // the turn died on the step limit.
        var workspace = Directory.CreateTempSubdirectory("partner-screenshot-");
        try
        {
            var automation = new DesktopAutomation(capture: new GdiScreenCapture());
            var tool = new DesktopScreenshotTool(automation, new FileMediaStore(workspace.FullName));

            var observation = automation.Observe();
            var target = observation.Entities.First(entity => entity.IsForeground);

            var result = await tool.InvokeAsync($$"""{"entityId":"{{target.EntityId}}"}""");

            Assert.True(result.Ok, result.Content);

            // Which window, where it is, and how the picture maps onto it.
            Assert.Contains($"窗口 {target.EntityId} @ 屏幕 ({target.Rect.X},{target.Rect.Y})", result.Content);
            Assert.Contains($"({target.Rect.X}+px", result.Content);

            // And the call itself, so there is nothing left to guess.
            Assert.Contains($"\"entityId\":\"{target.EntityId}\"", result.Content);
            Assert.Contains("\"kind\":\"LeftClick\"", result.Content);
            Assert.Contains("屏幕坐标", result.Content);

            var image = Assert.Single(result.Images!);
            Assert.StartsWith("data:image/png;base64,", image);

            // The referent for a later bare-point click is the window that was shown.
            Assert.Equal(target.EntityId, automation.LastShownEntityId);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }
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
    public async Task A_launch_of_a_path_that_cannot_start_reports_the_reason()
    {
        // Not a throw: the model gets a result it can act on, and the turn continues. A path is taken literally,
        // so this is the shell refusing rather than the name not being found.
        var result = await new DesktopLaunchTool()
            .InvokeAsync("""{"target":"C:\\\\definitely\\\\missing\\\\app.exe"}""");

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
