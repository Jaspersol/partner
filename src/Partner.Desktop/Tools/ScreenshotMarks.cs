using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Partner.Desktop.Perception;

namespace Partner.Desktop.Tools;

/// <summary>One numbered mark drawn on a screenshot, with the click point the server computed for it.</summary>
/// <param name="Index">The number printed on the picture.</param>
/// <param name="ElementId">The control it stands for, when it came from UI Automation — the model can act on
/// this directly instead of translating anything.</param>
/// <param name="Label">The control's name, for a human reading the log.</param>
/// <param name="ClickAt">Screen coordinate of the control's centre.</param>
public sealed record ScreenshotMark(int Index, string? ElementId, string Label, string? Role, PixelRect Rect, (int X, int Y) ClickAt);

/// <summary>
/// Draws the reference marks a model needs in order to <i>read</i> a position off a picture rather than
/// estimate it.
/// <para>
/// Estimating a pixel coordinate from a description of an image is a weak spot of every vision model, and the
/// measured failures are large: a real session asked for a music player's play button at screen (1280,1032)
/// and produced (957,948) — 334 px away, in empty sidebar background, while describing the button correctly
/// in words. The picture is not the problem; the absence of anything to measure against is.
/// </para>
/// <para>
/// So the picture carries two kinds of reference: a <b>grid</b> whose labels are screen coordinates (works for
/// icons, canvases and games, where nothing can be named), and <b>numbered marks</b> over the controls the
/// observation already found (where the server can hand over the coordinate outright).
/// </para>
/// </summary>
public static class ScreenshotMarks
{
    /// <summary>Split the longest side into about this many cells.</summary>
    private const int TargetCells = 8;

    /// <summary>Step sizes a person reads without arithmetic. The grid lands on round <i>screen</i>
    /// coordinates, so a label says "1250" rather than "1237.4".</summary>
    private static readonly double[] NiceSteps = { 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000 };

    // Frozen on purpose: these are shared by every screenshot, and screenshot calls arrive on whatever thread
    // the tool loop happens to be on. An unfrozen Freezable carries its creating thread's dispatcher, so the
    // second call from a different thread throws "belongs to a different thread than its parent Freezable".
    // Freezing detaches them from any thread.
    private static readonly Pen GridPen = FrozenPen(0x66, 0x00, 0xB0, 0xFF, 1);
    private static readonly Brush GridLabelBrush = Frozen(0xE6, 0x00, 0x20, 0x30);
    private static readonly Brush GridLabelBack = Frozen(0xCC, 0xE8, 0xF6, 0xFF);
    private static readonly Brush MarkBrush = Frozen(0xCC, 0xD8, 0x1B, 0x60);
    private static readonly Brush MarkTextBoxBrush = Brushes.White;
    private static readonly Pen MarkPen = FrozenPen(0xAA, 0xD8, 0x1B, 0x60, 1.5);
    private static readonly Typeface LabelFace = new("Segoe UI");

    private static SolidColorBrush Frozen(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(byte alpha, byte red, byte green, byte blue, double thickness)
    {
        var pen = new Pen(Frozen(alpha, red, green, blue), thickness);
        pen.Freeze();
        return pen;
    }

    /// <summary>
    /// Overlays the grid and the element marks. <paramref name="window"/> is the window's screen rectangle —
    /// the frame is a capture of it, so this is the one conversion between picture pixels and screen pixels.
    /// </summary>
    /// <returns>The annotated picture, and the marks it printed, in the order they were numbered.</returns>
    public static (BitmapSource Annotated, IReadOnlyList<ScreenshotMark> Marks) Draw(
        BitmapSource source,
        PixelRect window,
        IReadOnlyList<UiElement> elements)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(elements);

        var width = source.PixelWidth;
        var height = source.PixelHeight;

        if (width <= 0 || height <= 0 || window.Width <= 0 || window.Height <= 0)
        {
            return (source, Array.Empty<ScreenshotMark>());
        }

        // Picture pixels per screen pixel. Below 1.0 when the picture was scaled down to fit the token budget.
        var scale = width / (double)window.Width;
        var verticalScale = height / (double)window.Height;

        var marks = Marks(elements, window);

        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, width, height));

            DrawGrid(context, window, scale, verticalScale, width, height);
            DrawMarks(context, marks, window, scale, verticalScale);
        }

        var annotated = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        annotated.Render(visual);
        annotated.Freeze();

        return (annotated, marks);
    }

    /// <summary>
    /// Numbers the controls that can be addressed by id, in reading order so the numbers are stable to talk
    /// about. Rects outside the window are skipped: the picture is a capture of the window, so they have no
    /// place on it.
    /// <para>
    /// Pure geometry, no imaging — which is what lets the capability gate assert the part the model consumes
    /// (every mark names a real control and carries that control's own click point) without dragging WPF into
    /// the smoke.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<ScreenshotMark> Marks(IReadOnlyList<UiElement> elements, PixelRect window)
    {
        var marks = new List<ScreenshotMark>();

        foreach (var element in elements.OrderBy(e => e.Rect.Y).ThenBy(e => e.Rect.X))
        {
            if (element.Rect.IsEmpty || !Inside(element.Rect, window) || !Clickable(element.Role))
            {
                continue;
            }

            marks.Add(new ScreenshotMark(
                marks.Count + 1,
                element.ElementId,
                element.Name,
                element.Role,
                element.Rect,
                element.Centre));
        }

        return marks;
    }

    /// <summary>
    /// Whether a mark on this element would invite a click that means something. A <c>Document</c> is the text
    /// surface of an editor: its rectangle is the whole content area, so "click its centre" is "click the middle
    /// of the document" — and in a Chromium window it is the only element there is, which would leave the mark
    /// list with a single entry that looks actionable and is not. It stays in the observation, where its value
    /// is what an agent reads to verify its own typing.
    /// </summary>
    private static bool Clickable(string role) =>
        !string.Equals(role, "Document", StringComparison.Ordinal);

    private static bool Inside(PixelRect rect, PixelRect window) =>
        rect.X < window.X + window.Width
        && rect.Y < window.Y + window.Height
        && rect.X + rect.Width > window.X
        && rect.Y + rect.Height > window.Y;

    private static void DrawGrid(
        DrawingContext context,
        PixelRect window,
        double scale,
        double verticalScale,
        int width,
        int height)
    {
        var step = NiceStep(Math.Max(window.Width, window.Height));

        var pen = GridPen;

        // Vertical lines, labelled along the top; the label is the screen x, which is the number the model
        // has to put into the tool call.
        for (var screenX = FirstMultiple(window.X, step); screenX <= window.X + window.Width; screenX += step)
        {
            var x = Math.Round((screenX - window.X) * scale) + 0.5;

            if (x < 0 || x > width)
            {
                continue;
            }

            context.DrawLine(pen, new Point(x, 0), new Point(x, height));
            DrawLabel(context, screenX.ToString("0", CultureInfo.InvariantCulture), new Point(x + 3, 2));
        }

        // Horizontal lines, labelled down the left.
        for (var screenY = FirstMultiple(window.Y, step); screenY <= window.Y + window.Height; screenY += step)
        {
            var y = Math.Round((screenY - window.Y) * verticalScale) + 0.5;

            if (y < 0 || y > height)
            {
                continue;
            }

            context.DrawLine(pen, new Point(0, y), new Point(width, y));
            DrawLabel(context, screenY.ToString("0", CultureInfo.InvariantCulture), new Point(3, y + 3));
        }
    }

    private static void DrawMarks(
        DrawingContext context,
        IReadOnlyList<ScreenshotMark> marks,
        PixelRect window,
        double scale,
        double verticalScale)
    {
        var outline = MarkPen;

        foreach (var mark in marks)
        {
            // Screen → picture, the same subtraction the grid does. Without it the marks are drawn at their
            // screen coordinates inside a picture whose origin is the window's corner: offset by however far
            // the window sits from the desktop origin, and off-picture entirely for one far down the screen.
            var box = new Rect(
                (mark.Rect.X - window.X) * scale,
                (mark.Rect.Y - window.Y) * verticalScale,
                mark.Rect.Width * scale,
                mark.Rect.Height * verticalScale);

            context.DrawRectangle(null, outline, box);

            var text = Text(mark.Index.ToString(CultureInfo.InvariantCulture), 11, MarkTextBoxBrush);
            var chip = new Rect(box.X - 1, box.Y - 1, text.Width + 6, text.Height + 4);

            context.DrawRectangle(MarkBrush, null, chip);
            context.DrawText(text, new Point(chip.X + 3, chip.Y + 2));
        }
    }

    /// <summary>
    /// A screen-coordinate label with an opaque chip behind it: over a photograph or a dark UI, plain text is
    /// unreadable exactly where the model needs it most.
    /// </summary>
    private static void DrawLabel(DrawingContext context, string text, Point at)
    {
        var formatted = Text(text, 11, GridLabelBrush);
        var chip = new Rect(at.X, at.Y, formatted.Width + 4, formatted.Height + 2);

        context.DrawRectangle(GridLabelBack, null, chip);
        context.DrawText(formatted, new Point(chip.X + 2, chip.Y + 1));
    }

    private static FormattedText Text(string text, double size, Brush brush) => new(
        text,
        CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight,
        LabelFace,
        size,
        brush,
        96);

    /// <summary>The first gridline at or after <paramref name="origin"/> when stepping from zero — so the
    /// lines land on multiples of the step in screen coordinates, which is what makes labels round numbers.</summary>
    private static int FirstMultiple(int origin, int step) => (int)(Math.Ceiling(origin / (double)step) * step);

    /// <summary>The smallest readable step that splits the longer side into about <see cref="TargetCells"/>.</summary>
    private static int NiceStep(int longestSide)
    {
        var wanted = longestSide / (double)TargetCells;

        foreach (var candidate in NiceSteps)
        {
            if (candidate >= wanted)
            {
                return (int)candidate;
            }
        }

        return (int)NiceSteps[^1];
    }
}
