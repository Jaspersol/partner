using System.Text.Json;
using Partner.Desktop.Input;

namespace Partner.Api.Settings;

/// <summary>
/// One grant of continuing latitude on a window: what the human said yes to, made durable for the next
/// <see cref="Steps"/> actions of the same window.
/// <para>
/// It is deliberately <b>not</b> the observation lease (<c>LeaseStore</c>): that one is about
/// <i>what the model saw</i> — identity, generation, TTL measured in seconds. This one is about
/// <i>what the human approved</i> — it carries no geometry and answers to nothing but the window handle,
/// the kinds the model asked for, and the human's patience measured in steps. Approval that had to be
/// re-asked for every single click was the friction this replaces; the danger classes never enter it, which
/// is what keeps it from becoming a general bypass.
/// </para>
/// </summary>
public sealed record TaskLeaseState(string EntityId, IReadOnlySet<string> Kinds, int RemainingSteps, DateTimeOffset ExpiresAt);

/// <summary>
/// Grants and enforces continuing approval, per window.
/// <para>
/// Lives on <see cref="ApiContext"/>, so a consumer restart does not erase what a human just granted —
/// the grant is a fact about the human, not about the turn.
/// </para>
/// </summary>
public sealed class TaskLeaseStore
{
    /// <summary>
    /// How much one approval buys: ten more steps on that window. Enough for a real GUI exchange (look,
    /// act, look, act…), short enough that a model wandering off the approved target is stopped quickly.
    /// </summary>
    public const int DefaultSteps = 10;

    /// <summary>How long a grant lives when nothing spends it. Longer than any approval conversation;
    /// shorter than "this session".</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    // Every ActionKind the act tool exposes: a grant is "as before, on this window", and the kinds arrive
    // from the call that earned the grant. The dangerous tools never query here at all, so listing them
    // would be decorative.
    private static readonly IReadOnlySet<string> EveryActKind = new HashSet<string>(
        Enum.GetNames<ActionKind>(), StringComparer.Ordinal);

    private readonly Dictionary<string, TaskLeaseState> _leases = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public TaskLeaseStore(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Records what was just approved. Called only from the approval path after a human said yes, so the
    /// window and kind come from the call the human read on the banner.
    /// </summary>
    public void Grant(string entityId, string argumentsJson, int? steps = null, TimeSpan? ttl = null)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return;
        }

        var now = _clock.GetUtcNow();
        _leases[entityId] = new TaskLeaseState(
            entityId,
            EveryActKind,
            steps ?? DefaultSteps,
            now + (ttl ?? DefaultTtl));
    }

    /// <summary>
    /// Whether this action may run under the grant the human gave, spending one step of it. Called from
    /// governance before anything executes, so a consumed step is a step something was about to do.
    /// </summary>
    public (bool Allowed, string Reason, int Remaining) Consume(string entityId, string kind, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return (false, "没有 entityId，本动作不在任何租约之内", 0);
        }

        if (!_leases.TryGetValue(entityId, out var lease))
        {
            return (false, $"没有 '{entityId}' 的任务租约", 0);
        }

        var now = _clock.GetUtcNow();
        if (now > lease.ExpiresAt)
        {
            _leases.Remove(entityId);
            return (false, "任务租约已过期", 0);
        }

        if (lease.RemainingSteps <= 0)
        {
            _leases.Remove(entityId);
            return (false, "任务租约的步数已用完", 0);
        }

        if (!lease.Kinds.Contains(kind))
        {
            return (false, $"'{kind}' 不在本租约允许的动作里", lease.RemainingSteps);
        }

        var remaining = lease.RemainingSteps - 1;
        _leases[entityId] = lease with { RemainingSteps = remaining };

        return (true, remaining > 0
            ? $"任务租约内（剩余 {remaining} 步）"
            : "任务租约内（最后一步）", remaining);
    }

    /// <summary>Peeks without spending: what the UI would show as "currently granted".</summary>
    public IReadOnlyList<TaskLeaseState> Snapshot() =>
        _leases.Values
            .Where(lease => lease.ExpiresAt > _clock.GetUtcNow() && lease.RemainingSteps > 0)
            .OrderBy(lease => lease.EntityId, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Reads the window and kind a call was aimed at, from its arguments. Tolerates malformed JSON by
    /// answering null rather than refusing: the caller (a banner or a governance check) can only overlay
    /// information onto what it can read.
    /// </summary>
    public static (string EntityId, string Kind)? TryTarget(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            var entityId = root.TryGetProperty("entityId", out var entity) && entity.ValueKind == JsonValueKind.String
                ? entity.GetString()
                : null;
            var kind = root.TryGetProperty("kind", out var candidate) && candidate.ValueKind == JsonValueKind.String
                ? candidate.GetString()
                : null;

            return (entityId is { Length: > 0 }, kind is { Length: > 0 }) switch
            {
                (true, true) => (entityId!, kind!),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
