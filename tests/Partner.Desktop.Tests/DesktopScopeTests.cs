using Partner.Core.Media;
using Partner.Desktop.Capture;
using Partner.Desktop.Input;
using Partner.Desktop.Perception;
using Partner.Desktop.Tools;
using Partner.Desktop.Windows;

namespace Partner.Desktop.Tests;
/// <summary>
/// The observation level under "window": the whole virtual desktop, where the taskbar, tray, notification
/// banners and desktop icons live. The window marks are the piece whose geometry could quietly be wrong —
/// a chip over a window the model cannot address, or nothing visible where a window is — so the tests pin
/// the marks against the same windows the act path would resolve, and the end-to-end path against a real
/// desktop.
/// </summary>
public sealed class DesktopScopeTests
{

    [Fact]
    public void Every_real_window_gets_a_mark_that_names_its_id_and_its_centre()
    {
        var desktop = new PixelRect(0, 0, 2560, 1440);
        var windows = new List<DesktopWindow>
        {
            new()
            {
                Handle = 0x1A2B, Title = "播放器", ProcessId = 100, ProcessName = "player",
                X = 100, Y = 100, Width = 400, Height = 300, IsForeground = true,
            },
            new()
            {
                Handle = 0x3C4D, Title = "音乐", ProcessId = 200, ProcessName = "music",
                X = 600, Y = 200, Width = 300, Height = 200, IsForeground = false,
            },
        };

        var marks = ScreenshotMarks.WindowMarks(windows, desktop);

        Assert.Equal(2, marks.Count);

        // Reading order (top to bottom, then left to right), numbered from one, and the id is what
        // desktop_act accepts.
        Assert.Equal("1a2b", marks[0].ElementId);
        Assert.Equal("3c4d", marks[1].ElementId);
        Assert.Equal("window", marks[0].Role);
        Assert.Equal((300, 250), marks[0].ClickAt);
        Assert.Equal((750, 300), marks[1].ClickAt);
        Assert.All(marks, mark => Assert.True(desktop.Contains(mark.ClickAt.X, mark.ClickAt.Y)));
    }

    [Fact]
    public void A_minimized_window_is_not_marked_the_stub_rect_has_no_pixels_to_show()
    {
        // Same trap as element marks: a chip drawn at -32000 invites a click into the void.
        var desktop = new PixelRect(0, 0, 2560, 1440);
        var windows = new List<DesktopWindow>
        {
            new()
            {
                Handle = 0x1A2B, Title = "已最小化", ProcessId = 100,
                X = -32000, Y = -32000, Width = 160, Height = 28, IsMinimized = true, IsForeground = false,
            },
        };

        var marks = ScreenshotMarks.WindowMarks(windows, desktop);

        Assert.Empty(marks);
    }


    [Fact]
    public async Task The_desktop_picture_is_recorded_under_an_id_the_act_can_map()
    {
        var workspace = Directory.CreateTempSubdirectory("partner-scope-desktop-");
        try
        {
            using var automation = new DesktopAutomation();
            var tool = new DesktopScreenshotTool(automation, new FileMediaStore(workspace.FullName));

            var result = await tool.InvokeAsync("""{"scope":"desktop"}""");

            Assert.True(result.Ok, result.Content);

            var image = Assert.Single(result.Images!);
            Assert.StartsWith("data:image/png;base64,", image);

            // The picture the server owns the mapping for — recorded before the answer went out.
            var mediaId = ExtractMediaId(result.Content);
            Assert.False(string.IsNullOrEmpty(mediaId), $"no media id in: {result.Content[..120]}");
            Assert.NotNull(mediaId);
            var mapped = automation.PictureOf(mediaId!);
            Assert.NotNull(mapped);
            Assert.Equal("desktop", mapped!.EntityId);
            var desktop = VirtualDesktop.Current();
            Assert.Equal(new PixelRect(desktop.X, desktop.Y, desktop.Width, desktop.Height), mapped.ScreenRect);

            // And the referent for a later bare point is not any window: a desktop picture has no window.
            Assert.Equal("desktop", automation.LastShownEntityId);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task The_picture_names_the_windows_it_marked_so_a_window_needs_no_second_observe()
    {
        var workspace = Directory.CreateTempSubdirectory("partner-scope-marks-");
        try
        {
            using var automation = new DesktopAutomation();
            var tool = new DesktopScreenshotTool(automation, new FileMediaStore(workspace.FullName));

            var result = await tool.InvokeAsync("""{"scope":"desktop"}""");
            Assert.True(result.Ok, result.Content);

            // At least one real window is listable on any interactive desktop, and every listed id must
            // be one the observation would return too — the model addresses it and the act accepts.
            var observation = automation.Observe();
            var knownIds = observation.Entities
                .Select(entity => entity.EntityId)
                .ToHashSet(StringComparer.Ordinal);

            for (var i = 1; i <= 12; i++)
            {
                var marker = $"标记 {i} =";
                if (!result.Content.Contains(marker, StringComparison.Ordinal))
                {
                    break;
                }

                var after = result.Content[(result.Content.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
                var id = after[(after.IndexOf("entityId ", StringComparison.Ordinal) + "entityId ".Length)..].Split('，')[0];
                Assert.True(knownIds.Contains(id), $"mark {i} names id '{id}' which no observation lists");
            }
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The act side of the contract: px/py on a desktop picture is a bare screen point. A guard refusal
    /// (a background window, a key press) is the safe way to drive it end to end — nothing is injected.
    /// </summary>
    [Fact]
    public async Task Px_py_on_a_desktop_picture_resolves_to_the_window_under_the_point()
    {
        var workspace = Directory.CreateTempSubdirectory("partner-scope-act-");
        try
        {
            var policy = DesktopToolPolicy.Any;
            using var automation = new DesktopAutomation();
            var observation = automation.Observe();

            var target = observation.Entities.FirstOrDefault(entity =>
                !entity.IsForeground && !entity.IsMinimized && entity.Rect.Width > 100);
            if (target is null)
            {
                // No background window in this session: the desktop picture path itself is covered above,
                // and nothing here may move the pointer to manufacture one.
                return;
            }

            var desktop = VirtualDesktop.Current();
            var shotTool = new DesktopScreenshotTool(automation, new FileMediaStore(workspace.FullName));
            // Full native resolution: a downscaled picture (a 2560-wide desktop squeezed into 1280) cannot
            // avoid a couple of pixels of double-rounding in the px/py round trip; asking for as many
            // picture pixels as there are screen pixels makes the map ±1 px by rounding alone.
            var shot = await shotTool.InvokeAsync("""{"scope":"desktop","maxDimension":4096}""");
            Assert.True(shot.Ok, shot.Content);

            var mediaId = ExtractMediaId(shot.Content);
            Assert.NotNull(mediaId);
            var picture = automation.PictureOf(mediaId!);
            Assert.NotNull(picture);

            // The point is the window's centre, expressed in picture pixels the way the formula the
            // answer carries describes — this is the arithmetic the server exists to own.
            var centreX = target.Rect.X + target.Rect.Width / 2;
            var centreY = target.Rect.Y + target.Rect.Height / 2;
            var px = (int)Math.Round((centreX - picture!.ScreenRect.X) * picture.PictureWidth / (double)picture.ScreenRect.Width);
            var py = (int)Math.Round((centreY - picture.ScreenRect.Y) * picture.PictureHeight / (double)picture.ScreenRect.Height);

            var actTool = new DesktopActTool(automation, policy, new FileMediaStore(workspace.FullName), supportsImages: true);
            var result = await actTool.InvokeAsync(
                $$"""{"kind":"KeyPress","virtualKey":67,"px":{{px}},"py":{{py}},"mediaId":"{{mediaId}}","feedback":"off"}""");

            // The point resolved (or was refused for reasons the pipeline names out loud), and no input
            // was injected — which is what makes this test safe to run on the user's desktop. The one
            // hard assertion on the mapping: the coordinate the answer names is the centre this test
            // asked the picture for. (When two windows cover the point — a neighbour, the taskbar — the
            // refusal lists candidates instead of picking one, which is the same contract the bare
            // x/y path keeps.)
            Assert.False(result.Ok, result.Content);

            // The one hard mapping check: the coordinate the answer names is the centre this test asked
            // the picture for (±1 picture-pixel of double rounding).
            var named = System.Text.RegularExpressions.Regex.Match(
                result.Content, """屏幕坐标 \((\d+),(\d+)\)""");
            Assert.True(named.Success, $"no resolved coordinate in: {result.Content[..120]}");
            Assert.InRange(int.Parse(named.Groups[1].Value), centreX - 1, centreX + 1);
            Assert.InRange(int.Parse(named.Groups[2].Value), centreY - 1, centreY + 1);

            Assert.True(
                result.Content.Contains("不在前台", StringComparison.Ordinal)
                || result.Content.Contains("不落在唯一一个已观察窗口内", StringComparison.Ordinal),
                result.Content);
            if (result.Content.Contains("不在前台", StringComparison.Ordinal))
            {
                Assert.Contains(target.Label, result.Content, StringComparison.Ordinal);
            }
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    private static string? ExtractMediaId(string content)
    {
        var match = System.Text.RegularExpressions.Regex.Match(content, "媒体 ([0-9a-f]{8,})");
        return match.Success ? match.Groups[1].Value : null;
    }
}
