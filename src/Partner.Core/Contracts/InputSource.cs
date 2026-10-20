namespace Partner.Core.Contracts;

/// <summary>
/// Where an input item came from. Three kinds a person or the clock can produce, plus
/// <see cref="Agent"/>, which only the runtime itself may enqueue.
/// </summary>
public enum InputSource
{
    /// <summary>Speech-to-text from the local microphone.</summary>
    Voice,

    /// <summary>Typed text.</summary>
    Text,

    /// <summary>Fired by the durable scheduler.</summary>
    Schedule,

    /// <summary>
    /// A sub-agent's report coming back to the main agent. Never accepted from the HTTP input endpoint: it is
    /// the runtime talking to itself, and letting a client forge it would put words in a sub-agent's mouth.
    /// </summary>
    Agent,
}
