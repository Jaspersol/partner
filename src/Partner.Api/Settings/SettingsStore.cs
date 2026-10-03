using System.Text.Json;
using System.Text.Json.Serialization;
using Partner.Core.Agent.Llm;

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
            return JsonSerializer.Deserialize<RuntimeSettings>(File.ReadAllText(_path), Json)
                ?? RuntimeSettings.Default;
        }
        catch (JsonException)
        {
            // A corrupt settings file must not take the runtime down; the defaults are safe and the file is
            // rewritten on the next save.
            return RuntimeSettings.Default;
        }
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
        var merged = incoming with
        {
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
                ReasoningGears = NormalizeGears(
                    incoming.Model.ReasoningGears,
                    incoming.Model.ReasoningEffort),
            },
        };

        var errors = SettingsValidation.Validate(merged);
        if (errors.Count > 0)
        {
            throw new SettingsValidationException(errors);
        }

        Write(merged);
        return merged;
    }

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
