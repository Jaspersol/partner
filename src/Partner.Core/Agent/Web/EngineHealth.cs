using System.Text.Json;
using System.Text.Json.Serialization;

namespace Partner.Core.Agent.Web;

/// <summary>One engine's health, for the settings page and for the answer's "who answered" block.</summary>
/// <param name="Id">Engine id.</param>
/// <param name="Name">Engine name.</param>
/// <param name="DisabledOff">The user switched it off — a setting, not a state this store keeps.</param>
/// <param name="Cooling">Currently in cooldown.</param>
/// <param name="Until">When the cooldown ends.</param>
/// <param name="Strikes">Consecutive failures. Drives the doubling, and survives across cooldowns.</param>
/// <param name="LastReason">The last failure, in words.</param>
public sealed record EngineHealthSnapshot(
    string Id,
    string Name,
    bool DisabledOff,
    bool Cooling,
    DateTimeOffset? Until,
    int Strikes,
    string? LastReason);

/// <summary>What is persisted per engine. Also the shape on disk, so a failure can be read by a person.</summary>
public sealed record EngineHealthRecord(
    string Id,
    int Strikes,
    DateTimeOffset? CoolingUntil,
    string? LastReason,
    DateTimeOffset? LastFailureAt);

/// <summary>The file's root, versioned so a later change to the shape can be told from this one.</summary>
public sealed record EngineHealthFile(int Version, EngineHealthRecord[] Engines);

/// <summary>
/// The circuit breaker. Sources fail in ways that are specific to a user's network — one engine may be
/// unreachable from where they are and fine everywhere else — so the capability adapts to the network it finds
/// instead of assuming one, and an engine that has started failing stops being waited for.
/// <para>
/// Three failure classes, because they are not equally strong evidence. A block page is deliberate and immediate.
/// A network error is usually the network and takes a second occurrence. Returning nothing while a companion
/// engine found plenty is the weakest signal — it may be an honest empty result — so it takes three, and only
/// with the companion as a control.
/// </para>
/// </summary>
public sealed class EngineHealth
{
    /// <summary>First cooldown. Doubles on each repeat offence.</summary>
    public static readonly TimeSpan BaseCooldown = TimeSpan.FromMinutes(5);

    /// <summary>The ceiling. Six hours is long enough to stop wasting time on a dead source today, short enough
    /// to try again tomorrow without the user having to do anything.</summary>
    public static readonly TimeSpan MaxCooldown = TimeSpan.FromHours(6);

    /// <summary>A deliberate interception is believed the first time.</summary>
    public const int BlockedStrikes = 1;

    /// <summary>Two in a row, because one network error is a network error.</summary>
    public const int NetworkStrikes = 2;

    /// <summary>
    /// Two in a row for a parser that stopped matching. The plan's risk table says a parse failure must reach the
    /// breaker rather than quietly become "no results", and a page redesign is not a transient condition.
    /// </summary>
    public const int UnparseableStrikes = 2;

    /// <summary>Three, and only with a companion that did find something.</summary>
    public const int SilentEmptyStrikes = 3;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dictionary<string, EngineHealthRecord> _records = new(StringComparer.Ordinal);
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    /// <param name="workspace">The workspace root. The file lands in its <c>web</c> subdirectory.</param>
    /// <param name="clock">Injected so cooldown expiry is testable without waiting five minutes.</param>
    public EngineHealth(string workspace, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);

        _path = Path.Combine(workspace, "web", "health.json");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Reads the state file, or starts empty. A corrupt file is discarded rather than fatal.</summary>
    public static EngineHealth Load(string workspace, Func<DateTimeOffset>? clock = null)
    {
        var health = new EngineHealth(workspace, clock);

        try
        {
            if (!File.Exists(health._path))
            {
                return health;
            }

            var file = JsonSerializer.Deserialize<EngineHealthFile>(File.ReadAllText(health._path), Json);
            foreach (var record in file?.Engines ?? [])
            {
                if (!string.IsNullOrWhiteSpace(record.Id))
                {
                    health._records[record.Id] = record;
                }
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            // A health file is a cache of observations. Losing it costs one extra failing attempt per engine.
            health._records.Clear();
        }

        return health;
    }

    /// <summary>Where the state file lives, for the settings page to show.</summary>
    public string FilePath => _path;

    /// <summary>
    /// Why an engine may not be asked right now, or null. The wording goes straight into the answer, so it says
    /// when rather than that.
    /// </summary>
    public string? Unavailable(string engineId)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(engineId, out var record) || record.CoolingUntil is not { } until)
            {
                return null;
            }

            var now = _clock();
            if (until <= now)
            {
                return null;
            }

            var minutes = Math.Max(1, (int)Math.Ceiling((until - now).TotalMinutes));
            return $"熔断中，还有约 {minutes} 分钟（{record.LastReason ?? "连续失败"}）";
        }
    }

    /// <summary>Applies one attempt to the state. Persists when anything changed, so a crash keeps the lesson.</summary>
    /// <param name="attempt">What the engine did.</param>
    /// <param name="companionAnswered">Whether another engine in the same batch found something.</param>
    /// <param name="save">False when the caller records a whole batch and saves once at the end (see <see cref="Save"/>).</param>
    /// <returns>Whether the state changed.</returns>
    public bool Record(EngineAttempt attempt, bool companionAnswered, bool save = true)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        var changed = false;

        lock (_gate)
        {
            var id = attempt.Engine.Traits.Id;

            if (attempt.Answered)
            {
                // One success is all the evidence needed that the source works again.
                if (_records.ContainsKey(id))
                {
                    _records.Remove(id);
                    changed = true;
                }
            }
            else
            {
                var (strikes, threshold, reason) = Classify(attempt, companionAnswered);
                if (strikes == 0)
                {
                    // No evidence: a thinned bucket, a caller cancellation, an honest empty result with nothing
                    // to compare it to. Recording these would break healthy engines for being unlucky.
                    return false;
                }

                _records.TryGetValue(id, out var existing);
                var count = (existing?.Strikes ?? 0) + strikes;
                var now = _clock();

                var cooling = count >= threshold
                    ? now + CooldownFor(count)
                    : existing?.CoolingUntil is { } until && until > now
                        ? until
                        : (DateTimeOffset?)null;

                _records[id] = new EngineHealthRecord(id, count, cooling, reason, now);
                changed = true;
            }
        }

        if (changed && save)
        {
            Save();
        }

        return changed;
    }

    /// <summary>
    /// Everything the settings page shows: every known engine, with the user's own switch taking precedence over
    /// whatever the breaker thinks.
    /// </summary>
    public IReadOnlyList<EngineHealthSnapshot> Snapshot(IEnumerable<string>? disabled = null)
    {
        var off = disabled is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(disabled, StringComparer.Ordinal);
        var now = _clock();

        lock (_gate)
        {
            return WebEngines.All
                .Select(engine =>
                {
                    var id = engine.Traits.Id;
                    _records.TryGetValue(id, out var record);
                    var cooling = record?.CoolingUntil is { } until && until > now;

                    return new EngineHealthSnapshot(
                        id,
                        engine.Traits.Name,
                        off.Contains(id),
                        cooling,
                        cooling ? record!.CoolingUntil : null,
                        record?.Strikes ?? 0,
                        record?.LastReason);
                })
                .ToList();
        }
    }

    /// <summary>Writes the state file: temp file, then atomic replace, the same discipline as the settings file.</summary>
    public void Save()
    {
        EngineHealthFile file;
        lock (_gate)
        {
            file = new EngineHealthFile(1, _records.Values.OrderBy(record => record.Id, StringComparer.Ordinal).ToArray());
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, Json));

            if (File.Exists(_path))
            {
                File.Replace(temp, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, _path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Failing to remember a cooldown must never fail the search that produced it.
        }
    }

    /// <summary>
    /// The whole policy, in one pure function: how many strikes this outcome is worth, how many it takes, and
    /// what to tell the user about it.
    /// </summary>
    private static (int Strikes, int Threshold, string Reason) Classify(EngineAttempt attempt, bool companionAnswered)
    {
        if (attempt.Result?.Status == ParseStatus.Blocked)
        {
            return (1, BlockedStrikes, attempt.Result.Note ?? "被拦截");
        }

        if (attempt.Result?.Status == ParseStatus.Unparseable)
        {
            return (1, UnparseableStrikes, $"页面结构不认识：{attempt.Result.Note ?? "解析失败"}");
        }

        if (attempt.Failure is { } failure)
        {
            if (failure.StartsWith("blocked", StringComparison.Ordinal))
            {
                return (1, BlockedStrikes, failure);
            }

            if (failure.StartsWith("unreachable", StringComparison.Ordinal)
                || failure.StartsWith("timeout", StringComparison.Ordinal))
            {
                return (1, NetworkStrikes, failure);
            }

            // denied:/too_large:/cancelled:/rate_limited:/invalid_args: are all either our own decision or the
            // question's shape. They are not the engine misbehaving, and counting them would grow the cooldown of
            // a perfectly good source.
            return (0, int.MaxValue, string.Empty);
        }

        if (attempt.Result?.Status == ParseStatus.Empty)
        {
            // Only suspicious next to a companion that found something; alone it is just an empty result.
            return companionAnswered
                ? (1, SilentEmptyStrikes, $"静默返回空（同伴源有结果）")
                : (0, int.MaxValue, string.Empty);
        }

        return (0, int.MaxValue, string.Empty);
    }

    private static TimeSpan CooldownFor(int strikes)
    {
        var seconds = BaseCooldown.TotalSeconds * Math.Pow(2, Math.Max(0, strikes - 1));
        return seconds >= MaxCooldown.TotalSeconds ? MaxCooldown : TimeSpan.FromSeconds(seconds);
    }
}
