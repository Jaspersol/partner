using Partner.Core.Contracts;

namespace Partner.Core.Sessions;

/// <summary>
/// One entry of the projected session view. The view is derived from the append-only log
/// and can always be rebuilt from it (design doc §2.5.5 constraint 4).
/// </summary>
/// <param name="Seq">Seq of the event this entry represents (never a revision's own seq).</param>
/// <param name="Body">Effective body — the latest revision when the event has been edited.</param>
/// <param name="OriginalBody">Body as first written; non-null only when at least one revision was applied.</param>
/// <param name="RevisionCount">How many <see cref="SessionEventKind.Edit"/> events target this seq.</param>
public sealed record SessionViewItem(
    long Seq,
    DateTimeOffset At,
    SessionEventKind Kind,
    InputSource Source,
    string Body,
    string? OriginalBody,
    int RevisionCount);

/// <summary>Projection of the whole session log.</summary>
public sealed record SessionView(long LastSeq, IReadOnlyList<SessionViewItem> Items);
