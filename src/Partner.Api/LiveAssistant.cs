namespace Partner.Api;

/// <summary>
/// The turn in flight: what the model has written so far, and which queued item it belongs to.
/// <para>
/// A view of the running turn, not part of the session log. The log only ever holds the finished
/// <c>Assistant</c> event.
/// </para>
/// <para>
/// Reasoning is carried separately from the answer because it arrives first and finishes first; collapsing
/// them would leave the reader unable to tell "still thinking" from "hung".
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
