namespace Partner.Core.Agent.Web;

/// <summary>
/// The settings the web layer reads, as a plain record rather than the API's settings type. The dependency runs
/// one way — the API knows the core, never the reverse — and this is the shape it hands across.
/// <para>
/// Read through a delegate on every search rather than captured at construction: a proxy or budget edit takes
/// effect on the next question, without restarting the consumer.
/// </para>
/// </summary>
/// <param name="Enabled">Whether the capability exists at all.</param>
/// <param name="TimeBudgetSeconds">The fanout's budget, counted from the first result rather than from the start.</param>
/// <param name="MaxResults">How many results the answer lists.</param>
/// <param name="Proxy">An http(s) proxy, or null to connect directly.</param>
/// <param name="DisabledEngines">Engine ids the user switched off.</param>
public sealed record WebOptions(
    bool Enabled,
    int TimeBudgetSeconds,
    int MaxResults,
    string? Proxy,
    IReadOnlySet<string> DisabledEngines)
{
    /// <summary>The bounds both the settings validation and the runtime clamp against.</summary>
    public const int MinBudgetSeconds = 3;

    public const int MaxBudgetSeconds = 60;

    public const int MinResults = 1;

    public const int MaxResultsCeiling = 20;

    /// <summary>
    /// On by default, as this project's owner asked for, and a deliberate departure from the plan's
    /// fail-closed default. The switch is visible next to the note that queries leave this machine; the tools
    /// are read-only and every request passes the address check, so what the default changes is reachability,
    /// not exposure.
    /// </summary>
    public static WebOptions Default { get; } = new(
        Enabled: true,
        TimeBudgetSeconds: 12,
        MaxResults: 8,
        Proxy: null,
        DisabledEngines: new HashSet<string>(StringComparer.Ordinal));

    public TimeSpan Budget => TimeSpan.FromSeconds(Math.Clamp(TimeBudgetSeconds, MinBudgetSeconds, MaxBudgetSeconds));

    public IReadOnlySet<string> Disabled => DisabledEngines ?? new HashSet<string>(StringComparer.Ordinal);
}
