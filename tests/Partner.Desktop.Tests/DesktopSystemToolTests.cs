using System.Diagnostics;
using Partner.Core.Agent.Tools;
using Partner.Core.Media;
using Partner.Desktop.Capture;
using Partner.Desktop.Perception;
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

            // The picture carries a grid whose labels are screen coordinates, and the answer says so. A model
            // that estimates a position from an unmarked picture is off by hundreds of pixels; this is the
            // reference it measures against instead.
            Assert.Contains("坐标网格", result.Content);
            Assert.Contains("刻度上的数字就是屏幕坐标", result.Content);

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

    /// <summary>
    /// A command that leaves a background process holding the inherited pipe must not outlive the tool's own
    /// timeout — and must not be reported as a success.
    /// <para>
    /// This is the hang, reproduced. <c>cmd.exe</c> exits immediately, so waiting for the <i>process</i> returns
    /// at once; the pipe stays open for as long as <c>ping</c> lives. The reads were the only unbounded awaits
    /// in the tool, so the call sat there for the child's whole lifetime with nothing to stop it — measured at
    /// 44s against a 30s budget, returning <c>ok=true</c>. Every turn that issued such a command looked frozen
    /// until the user killed the app, and because the queue is single-reader that took the whole queue with it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Exec_gives_up_on_a_command_whose_background_child_holds_the_pipe()
    {
        var tool = new DesktopExecTool(TimeSpan.FromSeconds(2));

        var started = Stopwatch.StartNew();
        var result = await tool.InvokeAsync("""{"command":"start /b ping -n 45 127.0.0.1 > nul & exit 0"}""");
        started.Stop();

        // The budget, plus room for the shell to actually start. Not 45 seconds: that is the whole point.
        Assert.True(
            started.Elapsed < TimeSpan.FromSeconds(20),
            $"the tool took {started.Elapsed.TotalSeconds:0}s to give up on a 2s budget");

        Assert.False(result.Ok);
        Assert.Contains("未结束", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exec_reports_what_a_command_printed_before_it_was_cut_off()
    {
        // Partial output is the evidence for why the model reached for the command; a timeout that discards it
        // leaves nothing to diagnose from.
        var tool = new DesktopExecTool(TimeSpan.FromSeconds(2));

        var result = await tool.InvokeAsync("""{"command":"echo before-the-hang & start /b ping -n 45 127.0.0.1 > nul & exit 0"}""");

        Assert.False(result.Ok);
        Assert.Contains("before-the-hang", result.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// A timed-out command reports that its pipes were closed, and does not hang doing it.
    /// <para>
    /// The claim in the answer has to be true, because a model reads it: "the process tree and the pipes are
    /// both closed" is what tells it the next command is safe to issue. The pipes are closed unconditionally on
    /// this path, and the wait for the drains is bounded, so neither can park the turn.
    /// </para>
    /// <para>
    /// Deliberately not a handle-count assertion. That was the first attempt and it was wrong twice over: the
    /// tree kill already reaps a <c>start /b</c> child, so nothing leaks, and the number itself is moved as much
    /// by a survivor's own handles and by when the GC happens to run as by anything the tool does — measured
    /// flat over ten timed-out calls in both the before and after of the pipe-close change. A test that asserted
    /// a bound there would have been pinning the noise.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Exec_a_timed_out_command_reports_that_it_closed_its_pipes()
    {
        var tool = new DesktopExecTool(TimeSpan.FromSeconds(1));

        var result = await tool.InvokeAsync("""{"command":"start /b ping -n 30 127.0.0.1 > nul & exit 0"}""");

        Assert.False(result.Ok);
        Assert.Contains("进程树与管道均已关闭", result.Content, StringComparison.Ordinal);
    }


    /// <summary>
    /// The output a command produces is bounded while it is being read, not after. A command that prints
    /// without end used to be buffered whole and only then truncated — so "output is truncated" was true of
    /// what reached the conversation and false of what the process held in memory.
    /// </summary>
    [Fact]
    public async Task Exec_truncates_a_command_that_prints_far_more_than_the_cap()
    {
        var tool = new DesktopExecTool(TimeSpan.FromSeconds(20));

        var result = await tool.InvokeAsync(
            """{"command":"for /L %i in (1,1,20000) do @echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"}""");

        Assert.Contains("已截断", result.Content, StringComparison.Ordinal);
        Assert.True(result.Content.Length < 20_000, $"the cap did not hold: {result.Content.Length} characters");
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

    /// <summary>
    /// The grid on a picture is labelled in screen coordinates, and the label has to describe the pixels it
    /// is drawn on. A window can move between being observed and being photographed — a restore, a drag, a
    /// snap — and the capture is taken where the window is <i>now</i>, so drawing the observation's rectangle
    /// over the new pixels shifts every number the model reads off the picture by however far it moved.
    /// Nothing fails: the picture is a valid picture of a valid window, and the model clicks empty space.
    /// </summary>
    [Fact]
    public async Task A_picture_is_labelled_with_where_the_window_was_actually_captured()
    {
        var workspace = Directory.CreateTempSubdirectory("partner-screenshot-moved-");
        try
        {
            using var automation = new DesktopAutomation();
            var tool = new DesktopScreenshotTool(automation, new FileMediaStore(workspace.FullName));

            var observation = automation.Observe();
            var target = observation.Entities.First(entity => !entity.IsMinimized && entity.Rect.Width > 100);

            // The capture lands the window somewhere else than the observation recorded — the moved case.
            var moved = target.Rect with { X = target.Rect.X + 137, Y = target.Rect.Y + 61 };
            var shifted = new DesktopAutomation(capture: new OffsetCapture(moved));
            shifted.Observe();

            var result = await new DesktopScreenshotTool(shifted, new FileMediaStore(workspace.FullName))
                .InvokeAsync($$"""{"entityId":"{{target.EntityId}}","marks":false}""");

            Assert.True(result.Ok, result.Content);

            // The mapping the model is told to use is the one the pixels are actually in.
            Assert.Contains($"屏幕 ({moved.X},{moved.Y})", result.Content);
            Assert.Contains($"({moved.X}+px", result.Content);

            // And the disagreement is stated, rather than leaving the model to trust a picture whose
            // labels quietly disagree with the window list it observed.
            Assert.Contains("截图时窗口已移动", result.Content);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    /// <summary>A capture that reports a window at a chosen rectangle, so the moved-window case is
    /// reproducible without moving the user's windows.</summary>
    private sealed class OffsetCapture(PixelRect where) : IScreenCapture
    {
        public Task<CaptureResult> CaptureWindowAsync(nint handle, CancellationToken ct = default) =>
            Task.FromResult(new CaptureResult
            {
                Ok = true,
                Width = where.Width,
                Height = where.Height,
                Bgra = new byte[where.Width * where.Height * 4],
                Method = "test.offset",
                ScreenRect = where,
            });

        public Task<CaptureResult> CaptureScreenRegionAsync(int x, int y, int width, int height, CancellationToken ct = default) =>
            CaptureWindowAsync(0, ct);
    }
}
