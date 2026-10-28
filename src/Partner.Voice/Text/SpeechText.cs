using System.Text;

namespace Partner.Voice.Text;

/// <summary>
/// Strips everything a speech engine would read out loud verbatim: emoji, Markdown
/// emphasis/list/heading markers, links (keeping the label), code fences and inline code.
/// Without this the model's formatting is dictated character by character.
/// </summary>
public static class TtsTextCleaner
{
    public static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var result = new StringBuilder(text.Length);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var inFence = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            // Code fences: the whole block is dropped, not read aloud.
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            // Heading markers.
            line = line.TrimStart('#', '>', ' ');

            // List bullets.
            if (line.Length > 1 && (line[0] == '-' || line[0] == '*' || line[0] == '+') && line[1] == ' ')
            {
                line = line[2..];
            }

            // Links: keep the label, drop the URL.
            line = StripLinks(line);

            // Emphasis and inline code markers.
            line = line.Replace("**", string.Empty, StringComparison.Ordinal)
                .Replace("__", string.Empty, StringComparison.Ordinal)
                .Replace("*", string.Empty, StringComparison.Ordinal)
                .Replace("`", string.Empty, StringComparison.Ordinal);

            line = StripEmoji(line);

            if (line.Length > 0)
            {
                result.Append(line).Append(' ');
            }
        }

        return CollapseWhitespace(result.ToString()).Trim();
    }

    /// <summary>Readable text: single spaces, no trailing space.</summary>
    public static string CollapseWhitespace(string text)
    {
        var result = new StringBuilder(text.Length);
        var previousSpace = false;

        foreach (var ch in text)
        {
            var isSpace = char.IsWhiteSpace(ch);
            if (isSpace)
            {
                if (!previousSpace)
                {
                    result.Append(' ');
                }
            }
            else
            {
                result.Append(ch);
            }

            previousSpace = isSpace;
        }

        return result.ToString().Trim();
    }

    private static string StripLinks(string line)
    {
        var result = new StringBuilder(line.Length);
        var index = 0;

        while (index < line.Length)
        {
            var open = line.IndexOf('[', index);
            if (open < 0)
            {
                result.Append(line, index, line.Length - index);
                break;
            }

            var close = line.IndexOf(']', open);
            var openParen = close >= 0 && close + 1 < line.Length && line[close + 1] == '(' ? close + 1 : -1;
            var closeParen = openParen >= 0 ? line.IndexOf(')', openParen) : -1;

            if (close < 0 || openParen < 0 || closeParen < 0)
            {
                result.Append(line, index, line.Length - index);
                break;
            }

            result.Append(line, index, open - index);
            result.Append(line, open + 1, close - open - 1); // the label only
            index = closeParen + 1;
        }

        return result.ToString();
    }

    private static string StripEmoji(string line)
    {
        var result = new StringBuilder(line.Length);

        // Iterate by rune, not by char: astral emoji (U+1F389 and friends) are surrogate pairs in UTF-16,
        // so a per-char range check against 0x1F000 can never match them.
        foreach (var rune in line.EnumerateRunes())
        {
            var code = rune.Value;
            var isEmoji =
                (code >= 0x1F000 && code <= 0x1FAFF) // pictographs, symbols
                || (code >= 0x2600 && code <= 0x27BF) // misc symbols, dingbats
                || (code >= 0xFE00 && code <= 0xFE0F) // variation selectors
                || (code >= 0x1F1E6 && code <= 0x1F1FF) // regional indicators
                || code == 0x200D;                    // zero-width joiner

            if (!isEmoji)
            {
                result.Append(rune.ToString());
            }
        }

        return result.ToString();
    }
}

/// <summary>
/// Splits a stream of text into speakable sentences: sentence-ending punctuation first, soft breaks when a
/// fragment gets long, and a hard character cap for text that has no punctuation at all.
/// <para>
/// A fragment shorter than <c>minSentenceChars</c> is not a sentence of its own, so "嗯。" is read with whatever
/// follows instead of on its own. The length is measured from the previous sentence ending, not from the start:
/// a chain of short clauses has to add up before it is cut. The cap still wins when the text runs long — it then
/// cuts at the last sentence ending, else at the last soft break, else at the cap.
/// </para>
/// </summary>
public sealed class SpeechSegmenter
{
    private static readonly char[] SentenceEndings = { '。', '！', '？', '!', '?', '…', '.', ';', '；' };
    private static readonly char[] SoftBreaks = { '，', '、', '；', '：', ',', ':', ' ' };

    private readonly int _maxChars;
    private readonly int _minChars;
    private readonly StringBuilder _pending = new();

    public SpeechSegmenter(int maxChars = 40, int minSentenceChars = 10)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minSentenceChars);
        _maxChars = maxChars;
        _minChars = minSentenceChars;
    }

    public bool IsEmpty => _pending.Length == 0;

    /// <summary>Feeds a fragment and returns every complete sentence it produced, in order.</summary>
    public IReadOnlyList<string> Push(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        _pending.Append(text);
        var ready = new List<string>();

        while (TryTake(out var sentence))
        {
            ready.Add(sentence);
        }

        return ready;
    }

    /// <summary>Returns whatever is left, as a final fragment.</summary>
    public IReadOnlyList<string> Flush()
    {
        var text = _pending.ToString().Trim();
        _pending.Clear();
        return text.Length == 0 ? Array.Empty<string>() : new[] { text };
    }

    private bool TryTake(out string sentence)
    {
        sentence = string.Empty;

        if (_pending.Length == 0)
        {
            return false;
        }

        var text = _pending.ToString();

        // 1) A sentence ending, once the fragment it closes reaches the minimum length. A shorter one is not a
        //    sentence: the text keeps accumulating and the next ending is measured from the one skipped here, so
        //    "嗯。好。今天……" is one sentence until enough has been said.
        var previous = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (Array.IndexOf(SentenceEndings, text[i]) < 0)
            {
                continue;
            }

            var length = previous < 0 ? i + 1 : i - previous;
            if (length < _minChars)
            {
                previous = i;
                continue;
            }

            sentence = text[..(i + 1)].Trim();
            _pending.Remove(0, i + 1);

            if (sentence.Length == 0)
            {
                return TryTake(out sentence);
            }

            return true;
        }

        // 2) Too long: the last sentence ending (a short one — the loop above rejected every longer candidate),
        //    then a soft break, then a hard cut at the cap so the engine is never handed a wall of text.
        if (text.Length < _maxChars)
        {
            return false;
        }

        var limit = Math.Min(text.Length, _maxChars);
        var cut = LastSentenceEnding(text, limit);
        if (cut < 0)
        {
            cut = LastSoftBreak(text, limit);
        }

        if (cut < 0)
        {
            cut = _maxChars;
        }

        sentence = text[..cut].Trim();
        _pending.Remove(0, cut);
        return sentence.Length > 0;
    }

    /// <summary>Index just past the last sentence ending at or before <paramref name="limit"/>, or -1.</summary>
    private static int LastSentenceEnding(string text, int limit)
    {
        for (var i = limit - 1; i >= 0; i--)
        {
            if (Array.IndexOf(SentenceEndings, text[i]) >= 0)
            {
                return i + 1;
            }
        }

        return -1;
    }

    /// <summary>
    /// Index just past the last soft break at or before <paramref name="limit"/>, or -1.
    /// <para>
    /// A break one character after another break is not a break: cleaning turns a line ending in "，" into
    /// "， ", and "、," survives too. Cutting at the second one would leave the first as the head of the next
    /// sentence, so a run of breaks is cut after its first character.
    /// </para>
    /// </summary>
    private int LastSoftBreak(string text, int limit)
    {
        for (var i = limit - 1; i >= _maxChars / 2; i--)
        {
            if (Array.IndexOf(SoftBreaks, text[i]) < 0)
            {
                continue;
            }

            if (i > 0 && Array.IndexOf(SoftBreaks, text[i - 1]) >= 0)
            {
                continue;
            }

            return i + 1;
        }

        return -1;
    }
}
