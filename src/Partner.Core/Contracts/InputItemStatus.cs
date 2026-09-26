namespace Partner.Core.Contracts;

/// <summary>Lifecycle of a queue item (see doc §2.2).</summary>
public enum InputItemStatus
{
    Queued,
    Running,
    Done,
    Failed,
    Canceled,
}
