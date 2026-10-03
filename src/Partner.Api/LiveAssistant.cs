namespace Partner.Api;

/// <summary>
/// The turn in flight: what the model has written so far, and which queued item it belongs to.
/// <para>
/// This is a view of the running turn, not part of the session log. It exists so the session view can show a
/// reply while it is being written; the log only ever holds the finished <c>Assistant</c> event.
/// </para>
/// <para>
/// Reasoning is carried separately from the answer because it arrives first and finishes first. Collapsing
/// them would make the wait before an answer look like the answer, and would leave the reader unable to tell
/// "it is still thinking" from "it is hung".
/// </para>
/// </summary>
/// <param name="ItemId">The queued item this turn belongs to.</param>
/// <param name="Text">Assistant text so far.</param>
/// <param name="Reasoning">Reasoning so far, when the model reports any.</param>
/// <param name="ReasoningSince">
/// When the current run of reasoning started, so the view can say "thinking for 4s" rather than only
/// "thinking". Null when the model is not currently thinking.
/// </param>
public sealed record LiveAssistant(string ItemId, string Text, string? Reasoning = null, DateTimeOffset? ReasoningSince = null);
