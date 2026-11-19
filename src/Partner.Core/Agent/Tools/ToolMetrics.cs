using System.Diagnostics.Metrics;

namespace Partner.Core.Agent.Tools;

/// <summary>Aggregate tool measurements. Tags are bounded categories, never tool arguments or results.</summary>
internal static class ToolMetrics
{
    internal const string MeterName = "Partner.Core.Tools";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("partner.tool.duration", "ms");
    private static readonly Histogram<double> ApprovalWait = Meter.CreateHistogram<double>("partner.tool.approval.wait", "ms");
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>("partner.tool.outcomes");
    private static readonly Histogram<int> SchemaTokens = Meter.CreateHistogram<int>("partner.tool.schema.tokens", "{token}");
    private static readonly Histogram<int> ResultTokens = Meter.CreateHistogram<int>("partner.tool.result.tokens", "{token}");

    internal static string Family(string name) => name switch
    {
        _ when name.StartsWith("file_", StringComparison.Ordinal) => "file",
        _ when name.StartsWith("desktop_", StringComparison.Ordinal) => "desktop",
        _ when name.StartsWith("mcp_", StringComparison.Ordinal) => "mcp",
        _ when name.StartsWith("web_", StringComparison.Ordinal) => "web",
        _ when name.StartsWith("agent_", StringComparison.Ordinal) || name == "task_report" => "delegate",
        _ when name.StartsWith("schedule_", StringComparison.Ordinal) => "schedule",
        _ => "other",
    };

    internal static bool DurationEnabled => Duration.Enabled;
    internal static bool ApprovalWaitEnabled => ApprovalWait.Enabled;

    internal static void RecordDuration(string name, double milliseconds) =>
        Duration.Record(milliseconds, new KeyValuePair<string, object?>("family", Family(name)));

    internal static void RecordApprovalWait(string name, double milliseconds) =>
        ApprovalWait.Record(milliseconds, new KeyValuePair<string, object?>("family", Family(name)));

    internal static void RecordOutcome(string name, ToolResult result)
    {
        var outcome = result.Ok ? "success" : ClassifyFailure(result.Content);
        Outcomes.Add(1, new KeyValuePair<string, object?>("family", Family(name)),
            new KeyValuePair<string, object?>("outcome", outcome));
    }

    private static string ClassifyFailure(string content)
    {
        if (content.StartsWith("denied:", StringComparison.OrdinalIgnoreCase)
            || content.StartsWith("approval denied:", StringComparison.OrdinalIgnoreCase)
            || content.StartsWith("denied_file", StringComparison.OrdinalIgnoreCase)) return "denied";
        if (content.StartsWith("timeout", StringComparison.OrdinalIgnoreCase)
            || content.StartsWith("approval canceled or timed out", StringComparison.OrdinalIgnoreCase)) return "timeout_or_canceled";
        if (content.StartsWith("version_conflict", StringComparison.OrdinalIgnoreCase)
            || content.StartsWith("conflict", StringComparison.OrdinalIgnoreCase)) return "conflict";
        if (content.StartsWith("canceled", StringComparison.OrdinalIgnoreCase)) return "canceled";
        return "error";
    }

    internal static void RecordSchemaTokens(int tokens) => SchemaTokens.Record(tokens);

    internal static void RecordResultTokens(string name, int tokens) =>
        ResultTokens.Record(tokens, new KeyValuePair<string, object?>("family", Family(name)));

    internal static bool ResultTokensEnabled => ResultTokens.Enabled;
}
