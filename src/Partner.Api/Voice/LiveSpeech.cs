namespace Partner.Api.Voice;

/// <summary>
/// Bookkeeping for speaking a reply while the model is still writing it.
/// <para>
/// Live text only grows within one step, and the step's final text arrives later as a log event. This remembers
/// how much of the live text was already handed to the speaker, so the log event contributes only what is left —
/// otherwise the first sentences would be read out twice.
/// </para>
/// <para>
/// Chunks end on a sentence boundary on purpose: text is cleaned (markdown, emoji) before it is spoken, and
/// cleaning a fragment that stops mid-token would differ from cleaning the whole.
/// </para>
/// </summary>
internal sealed class LiveSpeech
{
    private static readonly char[] Boundaries = { '。', '！', '？', '!', '?', '…', '；', ';', '\n' };

    private string? _itemId;
    private int _spoken;
    private string? _finished;

    /// <summary>Whether part of a step has been handed to the speaker and its log event has not arrived yet.</summary>
    public bool InProgress => _spoken > 0;

    /// <summary>
    /// The next speakable piece of <paramref name="text"/>: everything up to the last sentence boundary that has
    /// not been taken yet, or null when there is nothing complete.
    /// </summary>
    public string? NextChunk(string itemId, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        // The log event for this text was already spoken; the live copy (whole, or a stale prefix of it) lingers
        // until the stream reports the step is over and must not be mistaken for a new step.
        if (_finished is not null && _finished.StartsWith(text, StringComparison.Ordinal))
        {
            return null;
        }

        // A different item, or text shorter than what was taken, is a new step.
        if (!string.Equals(_itemId, itemId, StringComparison.Ordinal) || text.Length < _spoken)
        {
            _itemId = itemId;
            _spoken = 0;
        }

        _finished = null;

        var end = LastBoundary(text, _spoken);
        if (end <= _spoken)
        {
            return null;
        }

        var chunk = text[_spoken..end];
        _spoken = end;
        return chunk;
    }

    /// <summary>
    /// The step's final text arrived: returns the part not yet spoken and resets for the next step.
    /// </summary>
    public string TakeRemainder(string body)
    {
        var rest = _spoken <= body.Length ? body[_spoken..] : string.Empty;

        _finished = body;
        _itemId = null;
        _spoken = 0;
        return rest;
    }

    /// <summary>Index just past the last boundary at or after <paramref name="from"/>, or -1.</summary>
    private static int LastBoundary(string text, int from)
    {
        var last = -1;
        char fence = '\0';
        var inUrl = false;
        var lineStart = true;
        for (var i = 0; i < text.Length; i++)
        {
            if (lineStart)
            {
                var start = i;
                while (start < text.Length && text[start] is ' ' or '\t') start++;
                if (start + 2 < text.Length && text[start] is '`' or '~'
                    && text[start + 1] == text[start] && text[start + 2] == text[start])
                {
                    var end = text.IndexOf('\n', start);
                    if (end < 0) break;
                    if (fence == '\0') fence = text[start];
                    else if (fence == text[start]) fence = '\0';
                    i = end;
                    lineStart = true;
                    inUrl = false;
                    if (fence == '\0' && i >= from) last = i + 1;
                    continue;
                }
                lineStart = false;
            }

            var c = text[i];
            if (c == '\n') lineStart = true;
            if (fence != '\0') continue;
            if (!inUrl && (text.AsSpan(i).StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || text.AsSpan(i).StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            {
                inUrl = true;
                i += text[i + 4] == 's' || text[i + 4] == 'S' ? 7 : 6;
                continue;
            }
            if (inUrl)
            {
                if (!char.IsWhiteSpace(c) && c is not ('，' or '。' or '！' or '？' or '；' or '：' or '<' or '>' or '(' or ')' or '[' or ']'))
                    continue;
                inUrl = false;
            }
            if (i >= from && (Array.IndexOf(Boundaries, c) >= 0
                || (c == '.' && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1])))) last = i + 1;
        }
        return last;
    }
}
