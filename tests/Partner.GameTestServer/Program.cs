// Synthetic game test server (design doc §2.7).
// Reads newline-delimited JSON requests from stdin and answers one JSON object per line on stdout:
//   {"cmd":"observe"}                                  -> {"seq":N,"values":{...}}
//   {"cmd":"act","kind":"move","params":{"dx":"3"}}    -> {"ok":true,"detail":"moved","seq":N,"values":{...}}
//   {"cmd":"bye"}                                      -> exits cleanly (stdin EOF does too)
// Unknown commands answer {"ok":false,"detail":"unknown command: X"}.
//
// Flags:
//   --hang-on <cmd>   never answer <cmd> (used to exercise the adapter's call timeout)
//   --pid-file <path> write the host PID there (used to kill the child externally)
// If PARTNER_GAME_TEST_LOG is set, every received cmd is appended as one line to that file.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var logPath = Environment.GetEnvironmentVariable("PARTNER_GAME_TEST_LOG");
var hangOn = OptionValue(args, "--hang-on");
var pidFile = OptionValue(args, "--pid-file");

if (pidFile is not null)
{
    try
    {
        await File.WriteAllTextAsync(pidFile, Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
            .ConfigureAwait(false);
    }
    catch (IOException)
    {
        // The caller is gone; nothing to do.
    }
}

var world = new World();

using var stdin = new StreamReader(
    Console.OpenStandardInput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
using var stdout = new StreamWriter(
    Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
{
    AutoFlush = false,
    NewLine = "\n",
};

while (true)
{
    var line = await stdin.ReadLineAsync().ConfigureAwait(false);
    if (line is null)
    {
        break; // stdin EOF: exit cleanly.
    }

    if (line.Length == 0)
    {
        continue;
    }

    JsonDocument document;
    try
    {
        document = JsonDocument.Parse(line);
    }
    catch (JsonException)
    {
        await WriteAsync(stdout, Unknown("invalid json")).ConfigureAwait(false);
        continue;
    }

    using (document)
    {
        var root = document.RootElement;
        var cmd = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("cmd", out var cmdElement)
            && cmdElement.ValueKind == JsonValueKind.String
                ? cmdElement.GetString() ?? string.Empty
                : string.Empty;

        if (logPath is not null)
        {
            await LogAsync(logPath, cmd).ConfigureAwait(false);
        }

        if (hangOn is not null && cmd == hangOn)
        {
            await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
        }

        switch (cmd)
        {
            case "observe":
                await WriteAsync(stdout, world.Snapshot()).ConfigureAwait(false);
                break;

            case "act":
                await HandleActAsync(stdout, world, root).ConfigureAwait(false);
                break;

            case "bye":
                return;

            default:
                await WriteAsync(stdout, Unknown(cmd)).ConfigureAwait(false);
                break;
        }
    }
}

static async Task HandleActAsync(StreamWriter stdout, World world, JsonElement root)
{
    var kind = root.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String
        ? kindElement.GetString() ?? string.Empty
        : string.Empty;

    var parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement : default;

    if (!world.TryAct(kind, parameters, out var detail))
    {
        await WriteAsync(stdout, new JsonObject { ["ok"] = false, ["detail"] = detail }).ConfigureAwait(false);
        return;
    }

    var response = world.Snapshot();
    response["ok"] = true;
    response["detail"] = detail;
    await WriteAsync(stdout, response).ConfigureAwait(false);
}

static async Task WriteAsync(StreamWriter stdout, JsonObject payload)
{
    await stdout.WriteLineAsync(payload.ToJsonString()).ConfigureAwait(false);
    await stdout.FlushAsync().ConfigureAwait(false);
}

static async Task LogAsync(string path, string cmd)
{
    try
    {
        await File.AppendAllTextAsync(path, cmd + "\n").ConfigureAwait(false);
    }
    catch (IOException)
    {
        // Logging is best effort.
    }
}

static JsonObject Unknown(string cmd) =>
    new() { ["ok"] = false, ["detail"] = $"unknown command: {cmd}" };

static string? OptionValue(string[] arguments, string name)
{
    for (var i = 0; i < arguments.Length; i++)
    {
        if (arguments[i] == name)
        {
            return i + 1 < arguments.Length ? arguments[i + 1] : string.Empty;
        }

        if (arguments[i].StartsWith(name + "=", StringComparison.Ordinal))
        {
            return arguments[i][(name.Length + 1)..];
        }
    }

    return null;
}

/// <summary>A tiny 10x10 grid world: x/y in 0..9, hp in 0..20, enemies >= 0, tick++ per successful act.</summary>
internal sealed class World
{
    private const int GridSize = 9;
    private const int MaxHp = 20;

    private int _x;
    private int _y;
    private int _hp = MaxHp;
    private int _enemies = 1;
    private int _tick;
    private int _seq;

    /// <summary>The current state; every response carries the next monotonic seq.</summary>
    public JsonObject Snapshot() => new()
    {
        ["seq"] = ++_seq,
        ["values"] = Values(),
    };

    public bool TryAct(string kind, JsonElement parameters, out string detail)
    {
        switch (kind)
        {
            case "move":
                _x = Math.Clamp(_x + ReadInt(parameters, "dx"), 0, GridSize);
                _y = Math.Clamp(_y + ReadInt(parameters, "dy"), 0, GridSize);
                _hp = Math.Max(0, _hp);
                detail = "moved";
                break;

            case "attack":
                _enemies = Math.Max(0, _enemies - 1);
                detail = "attacked";
                break;

            case "eat":
                _hp = Math.Min(MaxHp, _hp + 5);
                detail = "ate";
                break;

            default:
                detail = $"unknown action: {kind}";
                return false;
        }

        _tick++;
        return true;
    }

    private JsonObject Values() => new()
    {
        ["x"] = _x.ToString(CultureInfo.InvariantCulture),
        ["y"] = _y.ToString(CultureInfo.InvariantCulture),
        ["hp"] = _hp.ToString(CultureInfo.InvariantCulture),
        ["enemies"] = _enemies.ToString(CultureInfo.InvariantCulture),
        ["tick"] = _tick.ToString(CultureInfo.InvariantCulture),
    };

    private static int ReadInt(JsonElement parameters, string key)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty(key, out var element))
        {
            return 0;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var text) => text,
            _ => 0,
        };
    }
}
