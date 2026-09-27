namespace Partner.Api;

/// <summary>
/// The assistant text of the turn in flight: what the model has written so far, and which queued item it
/// belongs to.
/// <para>
/// This is a view of the running turn, not part of the session log. It exists so the session view can show a
/// reply while it is being written; the log only ever holds the finished <c>Assistant</c> event.
/// </para>
/// </summary>
public sealed record LiveAssistant(string ItemId, string Text);
