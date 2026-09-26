// Minimal MCP test-double server (design doc §2.6.3).
// Reads newline-delimited JSON-RPC 2.0 from stdin, writes responses to stdout.
// If PARTNER_MCP_TEST_LOG is set, every received method name is appended as one line to that file.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var logPath = Environment.GetEnvironmentVariable("PARTNER_MCP_TEST_LOG");

using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
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
        continue; // Not JSON: ignore (a real server would answer with -32700).
    }

    using (document)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("method", out var methodElement))
        {
            continue;
        }

        var method = methodElement.GetString() ?? string.Empty;
        LogMethod(logPath, method);

        // Notifications carry no id and must not be answered.
        if (!root.TryGetProperty("id", out var idElement)
            || idElement.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
        {
            continue;
        }

        var id = JsonNode.Parse(idElement.GetRawText());

        switch (method)
        {
            case "initialize":
                Write(stdout, id, new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "partner-test-mcp", ["version"] = "1" },
                });
                break;

            case "tools/list":
                Write(stdout, id, new JsonObject { ["tools"] = ToolList() });
                break;

            case "tools/call":
                HandleToolCall(stdout, id, root);
                break;

            default:
                WriteError(stdout, id, -32601, $"Method not found: {method}");
                break;
        }
    }
}

static void HandleToolCall(StreamWriter stdout, JsonNode? id, JsonElement root)
{
    var toolName = string.Empty;
    JsonElement arguments = default;
    var hasArguments = false;

    if (root.TryGetProperty("params", out var parameters))
    {
        if (parameters.TryGetProperty("name", out var nameElement))
        {
            toolName = nameElement.GetString() ?? string.Empty;
        }

        if (parameters.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object)
        {
            arguments = args;
            hasArguments = true;
        }
    }

    switch (toolName)
    {
        case "echo":
        {
            var text = hasArguments
                && arguments.TryGetProperty("text", out var textElement)
                && textElement.ValueKind == JsonValueKind.String
                    ? textElement.GetString() ?? string.Empty
                    : string.Empty;
            Write(stdout, id, new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["isError"] = false,
            });
            break;
        }

        case "fail":
            Write(stdout, id, new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "tool failed on purpose" }),
                ["isError"] = true,
            });
            break;

        case "hang":
            break; // Deliberately never responds.

        default:
            WriteError(stdout, id, -32602, $"Unknown tool: {toolName}");
            break;
    }
}

static JsonArray ToolList() => new(
    new JsonObject
    {
        ["name"] = "echo",
        ["description"] = "Echoes the text argument back.",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("text"),
        },
    },
    new JsonObject
    {
        ["name"] = "fail",
        ["description"] = "Always reports an error.",
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
    },
    new JsonObject
    {
        ["name"] = "hang",
        ["description"] = "Never responds.",
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
    });

static void Write(StreamWriter stdout, JsonNode? id, JsonNode result)
{
    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    stdout.Write(response.ToJsonString());
    stdout.Write('\n');
    stdout.Flush();
}

static void WriteError(StreamWriter stdout, JsonNode? id, int code, string message)
{
    var response = new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    stdout.Write(response.ToJsonString());
    stdout.Write('\n');
    stdout.Flush();
}

static void LogMethod(string? path, string method)
{
    if (string.IsNullOrEmpty(path))
    {
        return;
    }

    File.AppendAllText(path, method + "\n");
}
