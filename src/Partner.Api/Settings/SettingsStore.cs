using System.Text.Json;
using System.Text.Json.Serialization;

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
            Model = incoming.Model with { ApiKey = ResolveKey(incoming.Model.ApiKey, stored.Model.ApiKey) },
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
