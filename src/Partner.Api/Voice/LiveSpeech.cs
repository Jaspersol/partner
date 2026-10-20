namespace Partner.Api.Voice;

/// <summary>
/// Bookkeeping for speaking a reply while the model is still writing it.
/// <para>
/// The live text only ever grows within one step, and the step's final text arrives later as a log event. This
/// remembers how much of the live text was already handed to the speaker, so the log event only contributes
/// what is left — otherwise the first sentences would be read out twice.
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
        // until the stream is told the step is over, and must not be mistaken for a new step.
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
        for (var i = text.Length - 1; i >= from; i--)
        {
            var c = text[i];

            if (Array.IndexOf(Boundaries, c) >= 0)
            {
                return i + 1;
            }

            // A full stop ends a sentence only when followed by a space: "3.5" and "v1.2" must stay whole.
            if (c == '.' && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
            {
                return i + 1;
            }
        }

        return -1;
    }
}
