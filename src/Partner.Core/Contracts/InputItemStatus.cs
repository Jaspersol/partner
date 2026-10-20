namespace Partner.Core.Contracts;

/// <summary>Lifecycle of a queue item.</summary>
public enum InputItemStatus
{
    Queued,
    Running,
    Done,
    Failed,
    Canceled,
}
