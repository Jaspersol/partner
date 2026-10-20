namespace Partner.Core.Agent.Tools;

/// <summary>
/// A tool's public description: the name, description, and JSON Schema exposed to the model.
/// </summary>
/// <param name="Name">Tool name (the unique key in the registry).</param>
/// <param name="Description">Usage description for the model.</param>
/// <param name="ParametersJsonSchema">JSON Schema for the arguments, as raw JSON text.</param>
public sealed record ToolSpec(string Name, string Description, string ParametersJsonSchema);

/// <summary>
/// What a tool hands back.
/// </summary>
/// <param name="Ok">Whether the tool did what it was asked.</param>
/// <param name="Content">The text the model reads (and that the log records).</param>
/// <param name="Images">
/// Pictures to show the model, as data URIs. A tool result is text in the log, so the bytes travel beside it:
/// the log keeps the id, the request carries the pixels. Only ever set when the configured model takes images.
/// </param>
public sealed record ToolResult(bool Ok, string Content, IReadOnlyList<string>? Images = null);
