using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Partner.Core.Agent;
using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Tools;
using Partner.Core.Contracts;
using Partner.Core.Memory;
using Partner.Core.Queue;
using Partner.Core.Sessions;

namespace Partner.Core.Tests.Agent;

public sealed class ToolMetricsTests
{
    private sealed record Measurement(string Instrument, double Value, KeyValuePair<string, object?>[] Tags);

    [Fact]
    public async Task Runner_records_bounded_categories_and_budget_without_payloads()
    {
        var measurements = new ConcurrentQueue<Measurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Partner.Core.Tools") meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
            measurements.Enqueue(new(instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue(new(instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue(new(instrument.Name, value, tags.ToArray())));
        listener.Start();

        var dir = Path.Combine(Path.GetTempPath(), "partner-tests", Guid.NewGuid().ToString("n"));
        try
        {
            var (store, _) = JsonlSessionStore.Open(dir);
            await using (store)
            {
                var tools = new InMemoryToolRegistry();
                tools.Register(new ResultTool("file_secret", new ToolResult(true, "never returned")));
                tools.Register(new ResultTool("mcp_private_server", new ToolResult(false, "version_conflict: secret result")));
                tools.Register(new ResultTool("web_secret", new ToolResult(false, "timeout after 1s; secret result")));
                var runner = new AgentTurnRunner(store, new Scripted(), tools, new Decisions(),
                    new DenyAllApprovalHandler(), new HookRegistry(), tokens: new ApproximateTokenCounter());
                await runner.RunAsync(new InputItem
                {
                    ItemId = Guid.NewGuid().ToString(), Seq = 1, Status = InputItemStatus.Running,
                    Source = InputSource.Text, Payload = "test", IdempotencyKey = Guid.NewGuid().ToString(),
                });
            }

            var snapshot = measurements.ToArray();
            Assert.Contains(snapshot, m => m.Instrument == "partner.tool.schema.tokens" && m.Value > 0);
            Assert.Contains(snapshot, m => m.Instrument == "partner.tool.result.tokens" && Tag(m, "family") == "mcp" && m.Value > 0);
            Assert.Contains(snapshot, m => m.Instrument == "partner.tool.duration" && Tag(m, "family") == "web" && m.Value >= 0);
            Assert.Contains(snapshot, m => m.Instrument == "partner.tool.approval.wait" && Tag(m, "family") == "file" && m.Value >= 0);
            Assert.Contains(snapshot, m => m.Instrument == "partner.tool.outcomes" && Tag(m, "family") == "file" && Tag(m, "outcome") == "denied");
            Assert.Contains(snapshot, m => m.Instrument == "partner.tool.outcomes" && Tag(m, "family") == "mcp" && Tag(m, "outcome") == "conflict");
            Assert.Contains(snapshot, m => m.Instrument == "partner.tool.outcomes" && Tag(m, "family") == "web" && Tag(m, "outcome") == "timeout_or_canceled");
            Assert.All(snapshot.SelectMany(m => m.Tags), tag =>
            {
                Assert.Contains(tag.Key, new[] { "family", "outcome" });
                Assert.DoesNotContain("secret", tag.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("private", tag.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
            });
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static string? Tag(Measurement measurement, string key) =>
        measurement.Tags.FirstOrDefault(tag => tag.Key == key).Value as string;

    private sealed class ResultTool(string name, ToolResult result) : ITool
    {
        public ToolSpec Spec { get; } = new(name, "test", "{\"type\":\"object\"}");
        public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default) => Task.FromResult(result);
    }

    private sealed class Decisions : IToolGovernance
    {
        public GovernanceDecision Decide(string toolName, string argumentsJson) =>
            new(toolName == "file_secret" ? ToolDecision.Ask : ToolDecision.Allow, "test");
    }

    private sealed class Scripted : ILlmClient
    {
        private int _calls;
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default) =>
            Task.FromResult(Interlocked.Increment(ref _calls) == 1
                ? new LlmResponse(null,
                [
                    new LlmToolCall("1", "file_secret", "{\"content\":\"secret argument\"}"),
                    new LlmToolCall("2", "mcp_private_server", "{\"content\":\"secret argument\"}"),
                    new LlmToolCall("3", "web_secret", "{\"content\":\"secret argument\"}"),
                ], "tool_calls")
                : new LlmResponse("done", [], "stop"));
    }
}
