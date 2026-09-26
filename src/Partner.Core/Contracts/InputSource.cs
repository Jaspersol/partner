namespace Partner.Core.Contracts;

/// <summary>Where an input item came from. Exactly three kinds (see doc §2.2).</summary>
public enum InputSource
{
    /// <summary>Speech-to-text from the local microphone.</summary>
    Voice,

    /// <summary>Typed text.</summary>
    Text,

    /// <summary>Fired by the durable scheduler.</summary>
    Schedule,
}
