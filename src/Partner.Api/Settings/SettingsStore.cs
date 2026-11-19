using System.Text.Json;
using System.Text.Json.Serialization;
using Partner.Api.Auth;
using Partner.Core.Agent.Llm;
using Partner.Voice.Tts;

namespace Partner.Api.Settings;

/// <summary>
/// Persists the runtime's editable settings. Writes go through a temp file + atomic replace so a crash cannot
/// leave a half-written configuration behind.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly byte[] RebaseKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    /// <summary>Opaque guard for fields outside the embedded settings pages, including write-only secrets.</summary>
    public static string RebaseGuard(RuntimeSettings settings)
    {
        var guarded = settings with
        {
            Agents = new AgentsSettings(),
            Mcp = new McpSettings(),
            Skills = new SkillSettings(),
            Memory = new MemorySettings(),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(guarded, Json);
        return Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(RebaseKey, bytes));
    }

    private readonly string _path;
    private readonly int _apiPort;
    private readonly Func<IReadOnlyCollection<string>>? _petModelIds;
    private int _revision = Random.Shared.Next(1, 1_000_000_000);
    private readonly object _gate = new();
    private RuntimeSettings? _snapshot;

    /// <summary>Serializes commits and their asynchronous runtime reconfiguration.</summary>
    public SemaphoreSlim ApplyGate { get; } = new(1, 1);

    /// <param name="path">Where <c>runtime.json</c> lives.</param>
    /// <param name="apiPort">The port the services listen on, which a token may be bound to.</param>
    /// <param name="petModelIds">
    /// The model ids currently imported, asked on every save so <c>pet.activeModelId</c> cannot name a model that is
    /// not there. A delegate because imports and deletes happen while the store is alive.
    /// </param>
    public SettingsStore(string path, int apiPort = 0, Func<IReadOnlyCollection<string>>? petModelIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _apiPort = apiPort;
        _petModelIds = petModelIds;
    }

    public string Path => _path;

    /// <summary>
    /// Bumped after every successful <see cref="Save"/>. The pet window polls for settings changes and cannot
    /// diff the whole document on every tick, so this counter is what tells it "read the new settings".
    /// </summary>
    public int Revision => Volatile.Read(ref _revision);

    public RuntimeSettings Load()
    {
        lock (_gate)
        {
            _snapshot ??= Freeze(ReadFile());
            return _snapshot;
        }
    }

    public (RuntimeSettings Settings, int Revision) Snapshot()
    {
        lock (_gate)
        {
            _snapshot ??= Freeze(ReadFile());
            return (_snapshot, _revision);
        }
    }

    /// <summary>Serializes an approved file commit with settings publication. The commit must be synchronous.</summary>
    internal bool TryCommitFileWrite(RuntimeSettings approvedSnapshot, Func<RuntimeSettings, bool> isAuthorized, Action commit, CancellationToken ct)
    {
        lock (_gate)
        {
            _snapshot ??= Freeze(ReadFile());
            if (!ReferenceEquals(_snapshot, approvedSnapshot) || !isAuthorized(_snapshot))
                return false;
            ct.ThrowIfCancellationRequested();
            commit();
            return true;
        }
    }

    /// <summary>Explicitly reloads hand-edited settings; malformed JSON uses the safe defaults.</summary>
    public RuntimeSettings Reload()
    {
        lock (_gate)
        {
            var loaded = Freeze(ReadFile());
            _snapshot = loaded;
            Interlocked.Increment(ref _revision);
            return loaded;
        }
    }

    private static IReadOnlyList<T> FrozenList<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());

    private static IReadOnlyDictionary<string, T> FrozenMap<T>(IEnumerable<KeyValuePair<string, T>> values) =>
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, T>(values.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));

    private static LocalEngineSettings FreezeEngine(LocalEngineSettings engine) => engine with
    {
        Arguments = FrozenList(engine.Arguments),
        Keywords = FrozenList(engine.Keywords),
    };

    // Copy and wrap every collection, including nested pet and MCP collections, at publication.
    private static RuntimeSettings Freeze(RuntimeSettings settings) => settings with
    {
        Model = settings.Model with
        {
            ReasoningGears = FrozenList(settings.Model.ReasoningGears),
            ReasoningLabels = FrozenMap(settings.Model.ReasoningLabels),
        },
        Desktop = settings.Desktop with { AllowedWindowTitles = FrozenList(settings.Desktop.AllowedWindowTitles) },
        Files = settings.Files with { Roots = FrozenList(settings.Files.Roots), WriteRoots = FrozenList(settings.Files.WriteRoots) },
        Web = settings.Web with { DisabledEngines = FrozenList(settings.Web.DisabledEngines) },
        Skills = settings.Skills with { Disabled = FrozenList(settings.Skills.Disabled) },
        Agents = settings.Agents with { SubAgents = FrozenList(settings.Agents.SubAgents) },
        Services = settings.Services with { Instances = FrozenList(settings.Services.Instances) },
        Mcp = settings.Mcp with
        {
            Servers = FrozenList(settings.Mcp.Servers.Select(server => server with { Arguments = FrozenList(server.Arguments) })),
            AllowedTools = FrozenList(settings.Mcp.AllowedTools),
        },
        Local = settings.Local with { Tts = FreezeEngine(settings.Local.Tts), Kws = FreezeEngine(settings.Local.Kws) },
        Pet = settings.Pet with
        {
            GreetingTimes = FrozenList(settings.Pet.GreetingTimes),
            Emotions = settings.Pet.Emotions with
            {
                NegationPrefixes = FrozenList(settings.Pet.Emotions.NegationPrefixes),
                Keywords = FrozenMap(settings.Pet.Emotions.Keywords.Select(pair =>
                    new KeyValuePair<string, IReadOnlyList<PetKeyword>>(pair.Key, FrozenList(pair.Value)))),
            },
            Profiles = FrozenMap(settings.Pet.Profiles.Select(pair => new KeyValuePair<string, PetModelProfile>(pair.Key,
                pair.Value with
                {
                    Motions = FrozenMap(pair.Value.Motions),
                    Expressions = FrozenMap(pair.Value.Expressions),
                    Parameters = FrozenMap(pair.Value.Parameters),
                    Procedural = FrozenMap(pair.Value.Procedural.Select(action =>
                        new KeyValuePair<string, PetProceduralAction>(action.Key, action.Value with
                        {
                            ExtraTracks = FrozenList(action.Value.ExtraTracks.Select(track => track with
                            {
                                Keys = FrozenList(track.Keys),
                            })),
                        }))),
                }))),
        },
    };

    private RuntimeSettings ReadFile()
    {
        if (!File.Exists(_path))
        {
            return RuntimeSettings.Default;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            var loaded = document.RootElement.Deserialize<RuntimeSettings>(Json) ?? RuntimeSettings.Default;
            return Repaired(loaded, document.RootElement);
        }
        catch (JsonException)
        {
            // A corrupt settings file must not take the runtime down; the defaults are safe and the file is
            // rewritten on the next save.
            return RuntimeSettings.Default;
        }
    }

    /// <summary>
    /// Fills in what a hand-edited or partial file leaves out. The built-in desktop agent is restored here rather
    /// than refused, because a file that cannot be loaded cannot be fixed from the UI either.
    /// </summary>
    private static RuntimeSettings Repaired(RuntimeSettings loaded, JsonElement raw) => loaded with
    {
        Agents = (loaded.Agents ?? new AgentsSettings()).WithBuiltins(),
        Services = loaded.Services.WithDefaults(),
        Files = loaded.Files ?? new FilesSettings(),
        Web = loaded.Web ?? new WebSettings(),
        Tts = LocalTtsMigrated(loaded),
        Output = SplitCharsMigrated(loaded.Output ?? new OutputSettings(), raw),
        Pet = loaded.Pet ?? new PetSettings(),
    };

    /// <summary>
    /// The two split lengths used to be stored under <c>input</c>, where they never belonged: they only decide how
    /// a reply is cut for read-aloud. A value saved there is carried over, unless the file already has the new
    /// location — then that one is what the user last saved.
    /// </summary>
    private static OutputSettings SplitCharsMigrated(OutputSettings output, JsonElement raw) => output with
    {
        MaxSplitChars = LegacySplitChars(raw, "maxSplitChars") ?? output.MaxSplitChars,
        MinSentenceChars = LegacySplitChars(raw, "minSentenceChars") ?? output.MinSentenceChars,
    };

    private static int? LegacySplitChars(JsonElement raw, string name)
    {
        if (raw.ValueKind != JsonValueKind.Object
            || !raw.TryGetProperty("input", out var legacy)
            || legacy.ValueKind != JsonValueKind.Object
            || !legacy.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var chars)
            || chars <= 0)
        {
            return null;
        }

        return raw.TryGetProperty("output", out var moved)
            && moved.ValueKind == JsonValueKind.Object
            && moved.TryGetProperty(name, out _)
                ? null
                : chars;
    }

    /// <summary>
    /// The managed local engine only serves /audio/speech, so a client aimed at that engine's own port speaks the
    /// local protocol whatever the stored value says; the native protocol against it would 404.
    /// </summary>
    private static TtsSettings LocalTtsMigrated(RuntimeSettings loaded)
    {
        var tts = loaded.Tts ?? new TtsSettings();
        var port = loaded.Local?.Tts?.Port ?? 0;
        return tts.Protocol == TtsProtocols.Native
            && Uri.TryCreate(tts.BaseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Port == port
                ? tts with { Protocol = TtsProtocols.Local }
                : tts;
    }

    /// <summary>
    /// Applies <paramref name="incoming"/> on top of what is stored and persists the result.
    /// <para>
    /// The API key is the one field that is not a plain replace: <see langword="null"/> keeps the stored key,
    /// an empty string clears it, and a value replaces it.
    /// </para>
    /// </summary>
    /// <exception cref="SettingsValidationException">The merged settings are not usable.</exception>
    public RuntimeSettings Save(RuntimeSettings incoming, int? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        lock (_gate)
        {
            if (expectedRevision.HasValue && expectedRevision.Value != _revision)
            {
                throw new SettingsConflictException(_revision);
            }
            return SaveLocked(incoming);
        }
    }

    /// <summary>Performs a local read-modify-write under the same lock as whole-document saves.</summary>
    public RuntimeSettings Update(Func<RuntimeSettings, RuntimeSettings?> update)
    {
        lock (_gate)
        {
            _snapshot ??= Freeze(ReadFile());
            var current = _snapshot;
            var incoming = update(current);
            return incoming is null ? current : SaveLocked(incoming);
        }
    }

    private RuntimeSettings SaveLocked(RuntimeSettings incoming)
    {
        _snapshot ??= Freeze(ReadFile());
        var stored = _snapshot;
        var gears = NormalizeGears(incoming.Model.ReasoningGears, incoming.Model.ReasoningEffort);
        var merged = incoming with
        {
            Revision = null,
            Agents = incoming.AgentsSpecified ? MergeAgentKeys(incoming.Agents, stored.Agents) : stored.Agents,
            Services = incoming.ServicesSpecified
                ? MergeServiceSecrets(incoming.Services, stored.Services)
                : stored.Services,
            Model = incoming.Model with
            {
                Protocol = LlmProtocols.Normalize(incoming.Model.Protocol),
                ApiKey = ResolveKey(incoming.Model.ApiKey, stored.Model.ApiKey),
                // The gear is free text by design (an endpoint may understand a word the menu does not offer),
                // so only its shape is normalized here — casing and stray spaces are not a different setting.
                ReasoningEffort = ReasoningEfforts.Normalize(incoming.Model.ReasoningEffort),

                // Normalize, drop blanks, and de-duplicate while keeping the user's order — the order is the menu
                // order. An empty list falls back to the built-in one. The default is re-added if the user dropped
                // it, so the value stays selectable in the picker.
                ReasoningGears = gears,
                ReasoningLabels = NormalizeLabels(incoming.Model.ReasoningLabels, gears),
            },

            Pet = incoming.Pet with { Emotions = KnownEmotions(incoming.Pet.Emotions) },
        };

        var errors = SettingsValidation.Validate(merged, stored, _apiPort, _petModelIds?.Invoke());
        if (errors.Count > 0)
        {
            throw new SettingsValidationException(errors);
        }

        // Freeze before writing so failures cannot publish a partial commit.
        var published = Freeze(merged);
        Write(published);
        _snapshot = published;
        Interlocked.Increment(ref _revision);
        return published;
    }

    /// <summary>
    /// Drops dictionary keys this build does not know. A file written by a later build carries an emotion this
    /// one has no columns for; refusing the save would brick the page, and keeping it would round-trip a value
    /// nothing can edit.
    /// </summary>
    private static PetEmotionDictionary KnownEmotions(PetEmotionDictionary emotions)
    {
        var unknown = emotions.Keywords.Keys.Where(key => !PetEmotions.Values.Contains(key, StringComparer.Ordinal)).ToList();
        if (unknown.Count == 0)
        {
            return emotions;
        }

        return emotions with
        {
            Keywords = emotions.Keywords
                .Where(entry => PetEmotions.Values.Contains(entry.Key, StringComparer.Ordinal))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// Sub-agent API keys follow the main model's rule — null keeps, empty clears — matched by agent id, and prompts
    /// that are only whitespace are stored as "use the default" rather than as an empty prompt.
    /// </summary>
    private static AgentsSettings MergeAgentKeys(AgentsSettings incoming, AgentsSettings stored) => incoming with
    {
        Main = incoming.Main with { Prompt = Blank(incoming.Main.Prompt) },
        SubAgents = incoming.SubAgents.Select(agent => agent with
        {
            Name = agent.Name?.Trim() ?? string.Empty,
            Prompt = Blank(agent.Prompt),
            Model = agent.Model is null
                ? null
                : agent.Model with
                {
                    Protocol = LlmProtocols.Normalize(agent.Model.Protocol),
                    ApiKeySet = false,
                    ApiKey = ResolveKey(
                        agent.Model.ApiKey,
                        stored.SubAgents.FirstOrDefault(existing => existing.Id == agent.Id)?.Model?.ApiKey),
                },
        }).ToList(),
    };

    /// <summary>
    /// Service tokens and upstream keys are write-only: an empty value on a write means "keep the stored one",
    /// matched by instance id. A newly created instance carries its generated token, so it survives unchanged.
    /// <para>
    /// A <b>new</b> id (the instance did not exist yet) with no token gets one generated here, so a settings PUT
    /// that adds an instance behaves exactly like <c>POST /api/services</c> instead of failing validation.
    /// </para>
    /// </summary>
    private static ServicesSettings MergeServiceSecrets(ServicesSettings incoming, ServicesSettings stored)
    {
        var normalized = incoming.WithDefaults();
        return normalized with
        {
            Instances = normalized.Instances.Select(service =>
            {
                var existing = stored.Instances.FirstOrDefault(s => s.Id == service.Id);
                return service with
                {
                    Token = string.IsNullOrEmpty(service.Token)
                        ? existing?.Token ?? TokenAuth.Generate()
                        : service.Token,
                    Upstream = service.Upstream with
                    {
                        ApiKey = string.IsNullOrEmpty(service.Upstream.ApiKey)
                            ? existing?.Upstream.ApiKey
                            : service.Upstream.ApiKey,
                    },
                };
            }).ToList(),
        };
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static string? ResolveKey(string? incoming, string? stored) => incoming switch
    {
        null => stored,
        "" => null,
        _ => incoming,
    };

    /// <summary>
    /// Trims, lower-cases and de-duplicates the gear list while keeping the user's order, which is the menu
    /// order. An entry blank after trimming is dropped, and an emptied list falls back to the built-in one.
    /// <para>
    /// The default gear is appended when the user removed it from the list, so the value the model is
    /// configured with always stays selectable in the picker.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> NormalizeGears(IReadOnlyList<string> gears, string defaultGear)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>(gears.Count + 1);

        foreach (var gear in gears.Append(defaultGear))
        {
            var normalized = ReasoningEfforts.Normalize(gear);
            if (normalized.Length > 0 && seen.Add(normalized))
            {
                kept.Add(normalized);
            }
        }

        return kept.Count > 0 ? kept : ReasoningEfforts.Values;
    }

    /// <summary>
    /// Keys are normalized like the gears they name, labels are trimmed, and a label for a word that is not in the
    /// list (or a blank one) is dropped.
    /// </summary>
    private static IReadOnlyDictionary<string, string> NormalizeLabels(
        IReadOnlyDictionary<string, string>? labels,
        IReadOnlyList<string> gears)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (labels is null)
        {
            return result;
        }

        foreach (var (key, label) in labels)
        {
            var gear = ReasoningEfforts.Normalize(key);
            var text = label?.Trim() ?? string.Empty;
            if (text.Length > 0 && gears.Contains(gear))
            {
                result[gear] = text;
            }
        }

        return result;
    }

    private void Write(RuntimeSettings settings)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));

        // Atomic replace: readers see either the old or the new file, never a partial one.
        if (File.Exists(_path))
        {
            File.Replace(temp, _path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temp, _path);
        }
    }
}

public sealed class SettingsConflictException(int currentRevision) : Exception("Settings have changed; reload before saving.")
{
    public int CurrentRevision { get; } = currentRevision;
}

/// <summary>Carries every validation failure at once, so a client can fix them in one pass.</summary>
public sealed class SettingsValidationException : Exception
{
    public SettingsValidationException(IReadOnlyList<string> errors)
        : base(string.Join("; ", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
