namespace Partner.Game.Reflex;

/// <summary>
/// The six state domains a reflex layer reads (design doc §2.7, borrowed from the reference design).
/// <para>
/// The <b>domain split</b> is the portable part — it is what keeps reflex rules from growing into an
/// unstructured pile of if-statements. The fields inside each domain are the ones this project's games need.
/// </para>
/// </summary>
public sealed record SelfState(double X, double Y, double Health, double MaxHealth, string? Holding);

public sealed record EnvironmentState(string TimeOfDay, int NearbyEntities, int NearbyPlayers, double LightLevel);

public sealed record SocialState(string? LastSpeaker, string? LastMessage, DateTimeOffset? LastMessageAt);

public sealed record ThreatState(double Score, DateTimeOffset? LastThreatAt, string? LastSource);

public sealed record AttentionState(string? LastSignalType, string? LastSignalSourceId, DateTimeOffset? LastSignalAt);

/// <summary>Autonomy carries what the reflex layer is currently doing on its own behalf.</summary>
public sealed record AutonomyState(bool ReflexEngaged, string? CurrentGoal, string? LastError);

public sealed record ReflexContextState(
    SelfState Self,
    EnvironmentState Environment,
    SocialState Social,
    ThreatState Threat,
    AttentionState Attention,
    AutonomyState Autonomy);

/// <summary>Mutable reflex state. Updates are cheap field swaps; <see cref="GetSnapshot"/> is what rules read.</summary>
public sealed class ReflexContext
{
    private SelfState _self = new(0, 0, 0, 0, null);
    private EnvironmentState _environment = new("unknown", 0, 0, 0);
    private SocialState _social = new(null, null, null);
    private ThreatState _threat = new(0, null, null);
    private AttentionState _attention = new(null, null, null);
    private AutonomyState _autonomy = new(false, null, null);

    public DateTimeOffset Now { get; private set; }

    public ReflexContextState GetSnapshot() => new(_self, _environment, _social, _threat, _attention, _autonomy);

    public void UpdateNow(DateTimeOffset now) => Now = now;

    public void UpdateSelf(SelfState self) => _self = self;

    public void UpdateEnvironment(EnvironmentState environment) => _environment = environment;

    public void UpdateSocial(SocialState social) => _social = social;

    public void UpdateThreat(ThreatState threat) => _threat = threat;

    public void UpdateAttention(AttentionState attention) => _attention = attention;

    public void UpdateAutonomy(AutonomyState autonomy) => _autonomy = autonomy;
}
