using System.Text.Json;
using System.Text.Json.Serialization;

namespace Partner.Api.Settings;

/// <summary>
/// Voice settings as the UI sees them (design doc §2.8.11 / §2.8.12). Deliberately a plain DTO: the API
/// project must not depend on the voice layer's audio stack just to expose a form.
/// </summary>
public sealed record VoiceSettingsDto
{
    public bool InputEnabled { get; init; }

    public string? InputDeviceId { get; init; }

    public double SilenceThreshold { get; init; } = 0.03;

    public double UtteranceGapMs { get; init; } = 1200;

    public double SentenceGapMs { get; init; } = 450;

    public double WindowSeconds { get; init; } = 8;

    public bool AsrStream { get; init; }

    public bool OutputEnabled { get; init; }

    public string? OutputDeviceId { get; init; }

    public double Speed { get; init; } = 1.0;

    public double GapMs { get; init; }

    public double DrainMs { get; init; } = 400;

    public bool MuteDuringTts { get; init; } = true;

    public static VoiceSettingsDto Default { get; } = new();
}

/// <summary>
/// Persists the UI-editable settings. Writes go through a temp file + atomic replace so a crash cannot leave
/// a half-written configuration behind — the same discipline the session log uses.
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

    public VoiceSettingsDto Load()
    {
        if (!File.Exists(_path))
        {
            return VoiceSettingsDto.Default;
        }

        try
        {
            return JsonSerializer.Deserialize<VoiceSettingsDto>(File.ReadAllText(_path), Json)
                ?? VoiceSettingsDto.Default;
        }
        catch (JsonException)
        {
            // A corrupt settings file must not take the UI down; the defaults are safe and the file is
            // rewritten on the next save.
            return VoiceSettingsDto.Default;
        }
    }

    public void Save(VoiceSettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

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
