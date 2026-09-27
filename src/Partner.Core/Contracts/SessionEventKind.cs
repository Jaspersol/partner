namespace Partner.Core.Contracts;

/// <summary>
/// Kinds of records in the single session log (design doc §2.2, refined by §2.9.1 which fixes the
/// event sequence inside a turn).
/// <para>
/// Two families:
/// <list type="bullet">
///   <item><b>Content</b> — user-visible material that the projection renders.</item>
///   <item><b>Control</b> — turn/step boundaries. Written for auditability; never rendered.</item>
/// </list>
/// </para>
/// </summary>
public enum SessionEventKind
{
    // ---- content -------------------------------------------------------------------------------
    User,
    Assistant,
    System,
    Tool,
    Schedule,
    Summary,

    // ---- revisions -----------------------------------------------------------------------------
    /// <summary>A revision of an earlier event. Carries <c>EditedFromSeq</c>; never rendered as its own content.</summary>
    Edit,

    /// <summary>
    /// Removes history from the projection: <c>Meta["fromSeq"]</c>..<c>Meta["toSeq"]</c> (inclusive, an absent
    /// <c>toSeq</c> meaning "to the end") stops being rendered.
    /// <para>
    /// Written by compaction, which is lossless: the folded events stay in the log behind the summary. An
    /// explicit user removal does not write one of these — it goes through <c>ISessionStore.PurgeAsync</c>,
    /// which takes the content out of the log and leaves only a content-free marker for the range.
    /// </para>
    /// </summary>
    Retract,

    // ---- control (design doc §2.9.1) ------------------------------------------------------------
    TurnStart,
    StepStart,
    StepEnd,

    /// <summary>Must carry <c>Meta["reason"]</c> — one of completed / max-tokens / aborted / error.</summary>
    TurnEnd,

    ToolCall,
    ToolResult,
}

public static class SessionEventKindExtensions
{
    /// <summary>True when the projection renders this kind as conversation content.</summary>
    public static bool IsContent(this SessionEventKind kind) => kind switch
    {
        SessionEventKind.User => true,
        SessionEventKind.Assistant => true,
        SessionEventKind.System => true,
        SessionEventKind.Tool => true,
        SessionEventKind.Schedule => true,
        SessionEventKind.Summary => true,
        _ => false,
    };

    /// <summary>True for turn/step boundary markers.</summary>
    public static bool IsControl(this SessionEventKind kind) => kind switch
    {
        SessionEventKind.TurnStart => true,
        SessionEventKind.StepStart => true,
        SessionEventKind.StepEnd => true,
        SessionEventKind.TurnEnd => true,
        _ => false,
    };
}

/// <summary>Why a turn ended. Recorded in <c>TurnEnd</c> events; never left empty (design doc §2.9.1).</summary>
public static class TurnEndReason
{
    public const string Completed = "completed";
    public const string MaxTokens = "max-tokens";
    public const string Aborted = "aborted";
    public const string Error = "error";
}
