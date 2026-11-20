namespace Partner.Core.Memory;

/// <summary>Retention timing for records that have gone quiet.</summary>
public static class MemoryLifecycle
{
    /// <summary>How long a record may go unrecalled before it drops a tier.</summary>
    public static readonly TimeSpan IdleBeforeDemotion = TimeSpan.FromDays(90);

}
