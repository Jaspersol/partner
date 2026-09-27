namespace Partner.Core.Agent;

/// <summary>
/// Where a turn's live assistant text goes while the model is still writing.
/// <para>
/// Deliberately **not** the session log: a half-written reply is not an event of record. The turn still writes
/// one complete <c>Assistant</c> event from the response it returns, so a stream that dies halfway leaves a
/// turn without an answer rather than half an answer that later turns would read as complete.
/// </para>
/// </summary>
public interface IAssistantStream
{
    /// <summary>One increment of assistant text. Called on the turn's thread, in order.</summary>
    void OnAssistantDelta(string itemId, string delta);

    /// <summary>The turn is over: drop whatever was being shown, the log now has the real thing.</summary>
    void OnAssistantCompleted(string itemId);
}
