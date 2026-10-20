using System.Text.Json;
using System.Text.Json.Serialization;
using Partner.Core.Agent.Llm;
using Partner.Voice.Tts;

namespace Partner.Api.Settings;

/// <summary>
/// Persists the runtime's editable settings. Writes go through a temp file + atomic replace so a crash cannot
/// leave a half-written configuration behind — the same discipline the session log uses.
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
            return Repaired(JsonSerializer.Deserialize<RuntimeSettings>(File.ReadAllText(_path), Json)
                ?? RuntimeSettings.Default);
        }
        catch (JsonException)
        {
            // A corrupt settings file must not take the runtime down; the defaults are safe and the file is
            // rewritten on the next save.
            return RuntimeSettings.Default;
        }
    }

    /// <summary>
    /// Fills in what an older or hand-edited file leaves out. The built-in desktop agent is restored here rather
    /// than refused, because a file that cannot be loaded cannot be fixed from the UI either.
    /// </summary>
    private static RuntimeSettings Repaired(RuntimeSettings loaded) => loaded with
    {
        Agents = (loaded.Agents ?? new AgentsSettings()).WithBuiltins(),
        Files = loaded.Files ?? new FilesSettings(),
        Web = loaded.Web ?? new WebSettings(),
        Tts = LocalTtsMigrated(loaded),
    };

    /// <summary>
    /// An earlier default stored the speech client as <c>native</c> (POST /tts) while pointing it at the managed local
    /// engine, which only serves /audio/speech: every request came back 404. A client aimed at that engine's own port
    /// is the local one, whatever an old file says.
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
    /// an empty string clears it, and a value replaces it. Without that rule a full-object PUT would wipe the
    /// key every time a client edited an unrelated field.
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
                ApiKey = ResolveKey(incoming.Model.ApiKey, stored.Model.ApiKey),
                // The gear is free text by design (an endpoint may understand a word the menu does not offer),
                // so only its shape is normalized here — casing and stray spaces are not a different setting.
                ReasoningEffort = ReasoningEfforts.Normalize(incoming.Model.ReasoningEffort),

                // Same rule for the list: normalize, drop blanks, and de-duplicate while keeping the order
                // the user arranged — the order is the menu order, so preserving it is the point.
                // An empty list falls back to the built-in one: an empty menu is not a usable setting. The
                // default is re-added if the user dropped it, so the value stays selectable in the picker.
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
    /// order. An entry blank after trimming is dropped rather than stored as a gap in the menu, and an
    /// emptied list falls back to the built-in one: an empty menu is not a usable setting.
    /// <para>
    /// The default gear is appended when the user removed it from the list, so the value the model is
    /// configured with always stays selectable — otherwise renaming a gear would leave the default
    /// pointing at something the picker cannot show.
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
    /// list (or a blank one) is dropped: a name for nothing is clutter that would outlive the row it described.
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
