using System.Net;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Partner.Core.Agent.Web;

/// <summary>
/// Turning a page into text: the article extraction <c>web_fetch</c> returns, and the small helpers the HTML
/// engines need to read a result page.
/// <para>
/// The whole file is one heuristic, and it is written to fail loudly rather than silently: preference order first
/// (<c>article</c>, <c>main</c>, then the body minus the furniture), then a length check that says whether the
/// preferred block was really an article. A page whose <c>article</c> element holds navigation renders as
/// navigation if this trusts the tag alone.
/// </para>
/// </summary>
public static class HtmlText
{
    /// <summary>Below this many characters the preferred block is presumed to be furniture, not the article.</summary>
    private const int PreferredBlockMinChars = 200;

    private const string Furniture =
        "script, style, nav, header, footer, aside, form, noscript, iframe, svg, button, select, template, figure";

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "main", "li", "tr", "h1", "h2", "h3", "h4", "h5", "h6",
        "blockquote", "pre", "table", "ul", "ol", "dd", "dt", "dl", "br", "hr",
    };

    private static readonly HtmlParser Parser = new(new HtmlParserOptions
    {
        IsKeepingSourceReferences = false,
    });

    /// <summary>What a page yielded: its title, and its readable text.</summary>
    /// <param name="Title">The page title, or the empty string when the page has none.</param>
    /// <param name="Text">The extracted text, with paragraph breaks kept as newlines.</param>
    public sealed record Extract(string Title, string Text)
    {
        public static Extract Empty { get; } = new(string.Empty, string.Empty);
    }

    /// <summary>
    /// Reads a page. Never throws: a document that cannot be parsed comes back empty, and the caller decides
    /// whether "no text" means blocked, unparseable or genuinely blank.
    /// </summary>
    public static Extract Read(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return Extract.Empty;
        }

        IDocument document;
        try
        {
            document = Parser.ParseDocument(html);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Extract.Empty;
        }

        var title = TitleOf(document);

        foreach (var element in document.QuerySelectorAll(Furniture).ToArray())
        {
            element.Parent?.RemoveChild(element);
        }

        var root = MainBlock(document) ?? document.Body;
        if (root is null)
        {
            return new Extract(title, string.Empty);
        }

        var text = new StringBuilder(html.Length / 4);
        Walk(root, text);

        return new Extract(title, NormalizeLines(text.ToString()));
    }

    /// <summary>Drops tags and decodes entities. For the snippets an engine hands back, which arrive as HTML.</summary>
    public static string StripTags(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var text = new StringBuilder(html.Length);
        var inTag = false;

        foreach (var character in html)
        {
            if (character == '<')
            {
                inTag = true;
            }
            else if (character == '>')
            {
                inTag = false;
            }
            else if (!inTag)
            {
                text.Append(character);
            }
        }

        return Collapse(WebUtility.HtmlDecode(text.ToString()));
    }

    /// <summary>
    /// Whether a page is a wall rather than a result: a captcha, a consent gate, a login redirect, a JavaScript
    /// shell. Called by the HTML engines, which can see the page, and again by <c>BatchGate</c>, which sees the
    /// ones they missed.
    /// </summary>
    public static bool LooksLikeBlockPage(string? title, string? body)
    {
        var haystack = ((title ?? string.Empty) + "\n" + Truncate(body, 4000)).ToLowerInvariant();

        string[] signals =
        [
            "captcha", "验证码", "人机验证", "unusual traffic", "are you a robot", "不是机器人",
            "just a moment", "checking your browser", "enable javascript", "请开启 javascript",
            "需要开启 javascript", "cf-browser-verification", "attention required",
            "登录后继续", "sign in to continue", "consent to continue", "同意并继续", "accept all cookies",
            "challenge-platform", "ddg-captcha", "anomaly",
        ];

        return signals.Any(signal => haystack.Contains(signal, StringComparison.Ordinal));
    }

    /// <summary>The largest block a page offers as its article, or null when none looks like one.</summary>
    private static IElement? MainBlock(IDocument document)
    {
        IElement? best = null;
        var bestLength = 0;

        foreach (var selector in new[] { "article", "main", "[role=main]", "#content" })
        {
            foreach (var candidate in document.QuerySelectorAll(selector))
            {
                var length = candidate.TextContent.Length;
                if (length > bestLength)
                {
                    best = candidate;
                    bestLength = length;
                }
            }

            if (best is not null && bestLength >= PreferredBlockMinChars)
            {
                break;
            }
        }

        return bestLength >= PreferredBlockMinChars ? best : null;
    }

    private static string TitleOf(IDocument document)
    {
        var social = document.QuerySelector("meta[property='og:title'], meta[name='twitter:title']")
            ?.GetAttribute("content");
        if (!string.IsNullOrWhiteSpace(social))
        {
            return Collapse(social);
        }

        var heading = document.QuerySelector("h1")?.TextContent;
        if (!string.IsNullOrWhiteSpace(heading))
        {
            return Collapse(heading);
        }

        return Collapse(document.Title);
    }

    /// <summary>
    /// Walks the tree appending text, turning block boundaries into newlines. Reading <c>TextContent</c> instead
    /// would glue every paragraph to the next one, which reads as one long run-on sentence to the model.
    /// </summary>
    private static void Walk(INode node, StringBuilder text)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText textNode:
                    text.Append(Collapse(textNode.Data));
                    break;

                case IElement element:
                    var name = element.LocalName;
                    if (name == "br")
                    {
                        text.Append('\n');
                    }
                    else if (name is "li")
                    {
                        text.Append("\n- ");
                        Walk(element, text);
                        text.Append('\n');
                    }
                    else if (BlockTags.Contains(name))
                    {
                        text.Append('\n');
                        Walk(element, text);
                        text.Append('\n');
                    }
                    else
                    {
                        Walk(element, text);
                    }

                    break;
            }
        }
    }

    /// <summary>Whitespace runs become one space; every line is trimmed and blank lines are dropped.</summary>
    private static string NormalizeLines(string text)
    {
        var lines = text.Split('\n')
            .Select(line => Collapse(line))
            .Where(line => line.Length > 0);

        return string.Join('\n', lines);
    }

    private static string Collapse(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = text.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                text.Append(' ');
                pendingSpace = false;
            }

            text.Append(character);
        }

        return text.ToString();
    }

    private static string Truncate(string? value, int limit) =>
        value is null || value.Length <= limit ? value ?? string.Empty : value[..limit];
}
