using System.Text.Json;
using System.Text.Json.Serialization;
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

    private readonly string _path;

    public SettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public string Path => _path;

    public RuntimeSettings Load()
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
        Files = loaded.Files ?? new FilesSettings(),
        Web = loaded.Web ?? new WebSettings(),
        Tts = LocalTtsMigrated(loaded),
        Output = SplitCharsMigrated(loaded.Output ?? new OutputSettings(), raw),
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
    public RuntimeSettings Save(RuntimeSettings incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        var stored = Load();
        var gears = NormalizeGears(incoming.Model.ReasoningGears, incoming.Model.ReasoningEffort);
        var merged = incoming with
        {
            Agents = incoming.AgentsSpecified ? MergeAgentKeys(incoming.Agents, stored.Agents) : stored.Agents,
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
        };

        var errors = SettingsValidation.Validate(merged, stored);
        if (errors.Count > 0)
        {
            throw new SettingsValidationException(errors);
        }

        Write(merged);
        return merged;
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
