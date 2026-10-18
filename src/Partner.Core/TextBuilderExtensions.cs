using System.Text;

namespace Partner.Core;

internal static class TextBuilderExtensions
{
    /// <summary>
    /// Appends a line ended by <c>\n</c> on every platform. <see cref="StringBuilder.AppendLine(string)"/> ends it with
    /// the platform's newline, and text that goes to a model (or into a log a test reads back) should not change
    /// shape depending on where it was built.
    /// </summary>
    public static StringBuilder Line(this StringBuilder builder, string text) => builder.Append(text).Append('\n');
}
