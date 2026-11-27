using Partner.Core.Agent;
using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Llm;
using Partner.Core.Agent.Tools;
using Partner.Core.Contracts;
using Partner.Core.Queue;
using Partner.Core.Sessions;
using Partner.Core.Memory;
using Partner.Core.Tests.Sessions;

namespace Partner.Core.Tests.Agent;

internal sealed class FakeTool(string name, Func<string, CancellationToken, Task<ToolResult>>? impl = null) : ITool
{
    public ToolSpec Spec { get; } = new(name, $"fake tool {name}", "{\"type\":\"object\"}");

    public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default) =>
        impl is null
            ? Task.FromResult(new ToolResult(true, $"{name}({argumentsJson})"))
            : impl(argumentsJson, ct);
}

internal sealed class PreparedWriteTestTool(ToolResult? result = null, string name = "file_write") : IPreparedFileWriteTool
{
    private readonly ToolResult? _result = result;
    public int Commits { get; private set; }
    public int Disposals { get; private set; }
    public ToolSpec Spec { get; } = new(name, "test", "{\"type\":\"object\"}");
    public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default) =>
        throw new InvalidOperationException("must use prepared operation");
    public IPreparedFileWrite Prepare(string argumentsJson, CancellationToken ct) => new Operation(this);

    private sealed class Operation(PreparedWriteTestTool owner) : IPreparedFileWrite
    {
        public string TargetPath => "C:\\safe\\new.txt";
        public string ApprovalJson => "{\"path\":\"C:\\\\safe\\\\new.txt\"}";
        public Task<ToolResult> CommitAsync(CancellationToken ct)
        {
            owner.Commits++;
            return Task.FromResult(owner._result ?? new ToolResult(true, "created"));
        }
        public void Dispose() => owner.Disposals++;
    }
}

internal sealed class RecordingWriteApproval(bool answer) : IApprovalHandler
{
    public List<string> Requests { get; } = new();
    public Task<bool> RequestAsync(string toolName, string argumentsJson, CancellationToken ct = default)
    {
        Requests.Add(argumentsJson);
        return Task.FromResult(answer);
    }
}

internal sealed class RecordingHook(
    AgentPhase phase,
    string name,
    List<string> log,
    Func<HookContext, HookOutcome>? decide = null,
    int priority = 100) : IAgentHook
{
    public AgentPhase Phase => phase;

    public string Name => name;

    public int Priority => priority;

    public IReadOnlyList<string> Before => Array.Empty<string>();

    public IReadOnlyList<string> After => Array.Empty<string>();

    public ValueTask<HookOutcome> RunAsync(HookContext ctx, CancellationToken ct)
    {
        log.Add(name);
        return ValueTask.FromResult(decide?.Invoke(ctx) ?? HookOutcome.Continue);
    }
}

internal sealed class BlockingLlmClient : ILlmClient
{
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        throw new InvalidOperationException("unreachable");
    }
}

/// <summary>
/// Fails with a chosen exception, so a test can exercise one specific failure shape rather than a generic one.
/// </summary>
internal sealed class ThrowingLlmClient(string message, Exception? failure = null) : ILlmClient
{
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default) =>
        throw failure ?? new InvalidOperationException(message);
}

/// <summary>Records the gear each request arrived with, so a turn's gear can be asserted end to end.</summary>
internal sealed class GearRecordingLlmClient : ILlmClient
{

    private int _next;
    private readonly List<LlmResponse> _scripted;

    public GearRecordingLlmClient(params LlmResponse[] scripted) => _scripted = scripted.ToList();

    public List<string?> Seen { get; } = new();

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        // A turn may run several steps; the gear has to be the same on every one of them.
        Seen.Add(request.ReasoningEffort);

        // The last scripted response repeats, so a two-step turn does not need a third entry for its tail.
        var step = Math.Min(_next++, _scripted.Count - 1);
        return Task.FromResult(_scripted[step]);
    }
}

/// <summary>Acceptance tests for the turn runner.</summary>
public sealed class AgentTurnRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "partner-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static InputItem Claimed(string payload = "hello", InputSource source = InputSource.Text) => new()
    {
        ItemId = "item-1",
        Seq = 1,
        Status = InputItemStatus.Running,
        Source = source,
        Payload = payload,
        IdempotencyKey = "text:client:1",
    };

    private static LlmResponse Say(string text, string finish = "stop") =>
        new(text, Array.Empty<LlmToolCall>(), finish);

    private static LlmResponse Call(string id, string name, string args = "{}") =>
        new(null, new[] { new LlmToolCall(id, name, args) }, "tool_calls");

    private async Task<List<SessionEvent>> RawAsync(ISessionStore store)
    {
        var events = new List<SessionEvent>();
        await foreach (var e in store.ReadAllAsync())
        {
            events.Add(e);
        }

        return events;
    }

    private AgentTurnRunner Build(
        ISessionStore session,
        ILlmClient llm,
        IToolRegistry? tools = null,
        IToolGovernance? governance = null,
        IApprovalHandler? approval = null,
        HookRegistry? hooks = null,
        AgentTurnOptions? options = null,
        IContextAssembler? assembler = null,
        IAssistantStream? stream = null,
        IToolFailureLog? failures = null) =>
        new(
            session,
            llm,
            tools ?? new InMemoryToolRegistry(),
            governance ?? new AllowListGovernance(Array.Empty<string>()),
            approval ?? new DenyAllApprovalHandler(),
            hooks ?? new HookRegistry(),
            assembler,
            options,
            stream,
            failures);

    private sealed class UnknownDecisionGovernance : IToolGovernance
    {
        public GovernanceDecision Decide(string toolName, string argumentsJson) => new((ToolDecision)999, "unknown");
    }

    private sealed class AskWriteGovernance : IToolGovernance
    {
        public GovernanceDecision Decide(string toolName, string argumentsJson) => new(ToolDecision.Ask, "approval required");
    }

    [Theory]
    [InlineData(InputSource.Text)]
    [InlineData(InputSource.Voice)]
    public async Task A_user_message_keeps_its_enqueue_time_when_replayed(InputSource source)
    {
        var sentAt = new DateTimeOffset(2026, 11, 27, 6, 30, 5, TimeSpan.Zero);
        var loggedAt = sentAt.AddMinutes(10);
        var (store, _) = JsonlSessionStore.Open(_dir, new FixedClock(loggedAt));
        await using (store)
        {
            var llm = new ScriptedLlmClient([Say("answer"), Say("next answer")]);
            var runner = Build(store, llm);
            await runner.RunAsync(Claimed("first question", source) with { EnqueuedAt = sentAt });
            await runner.RunAsync(Claimed("next question") with { ItemId = "item-2", Seq = 2, EnqueuedAt = loggedAt });

            var expected = FormattableString.Invariant($"[用户消息时间：{sentAt.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}]\nfirst question");
            Assert.Equal(expected, llm.Requests[0].Messages[^1].Content);
            Assert.Equal(expected, llm.Requests[1].Messages.First(message => message.Role == "user").Content);
            var firstUser = (await RawAsync(store)).First(entry => entry.Kind == SessionEventKind.User);
            Assert.Equal("first question", firstUser.Body);
            Assert.Equal(loggedAt, firstUser.At);
        }
    }

    [Fact]
    public async Task Scheduled_runs_keep_history_without_replaying_other_turns_or_distilling_memory()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        await using (var memory = new MemoryStore(Path.Combine(_dir, "memory.db")))
        {
            var calls = 0;
            var tools = new InMemoryToolRegistry();
            tools.Register(new FakeTool("check", (_, _) =>
            {
                calls++;
                return Task.FromResult(calls is 3 or 4
                    ? new ToolResult(false, calls == 3 ? "task-a failure" : "task-b failure")
                    : new ToolResult(true, "checked"));
            }));
            var script = Enumerable.Range(0, 5).SelectMany(i => new[] { Call($"call-{i}", "check"), Say($"receipt-{i}") });
            var llm = new ScriptedLlmClient(script);
            var hooks = new HookRegistry();
            hooks.Register(new MemoryDistilHook(memory, store));
            var assembler = new SessionWindowContextAssembler(store, memory: memory);
            var runner = Build(store, llm, tools, new AllowListGovernance(["check"]), hooks: hooks, assembler: assembler);
            for (var i = 0; i < 5; i++)
            {
                var task = i == 3 ? "b" : "a";
                await runner.RunAsync(Claimed(i == 4 ? "重新检查" : "检查", InputSource.Schedule) with
                {
                    ItemId = $"scheduled-{i}", Seq = i + 1, IdempotencyKey = $"schedule:{task}:occurrence-{i}",
                });
            }

            Assert.Equal(5, calls);
            Assert.Empty(await memory.ListAsync());
            var history = await RawAsync(store);
            Assert.Equal(5, history.Count(e => e.Kind == SessionEventKind.User));
            var prompt = string.Join("\n", llm.Requests[8].Messages.Select(m => m.Content));
            Assert.Contains("task-a failure", prompt);
            Assert.DoesNotContain("task-b failure", prompt);
            Assert.DoesNotContain("receipt-0", prompt);
            Assert.DoesNotContain("receipt-1", prompt);
        }
    }
    [Fact]
    public async Task Scheduled_http_evidence_closes_only_same_task_and_target_and_keeps_original_result()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var a = new string('a', 64);
            var b = new string('b', 64);
            var calls = 0;
            var tools = new InMemoryToolRegistry();
            tools.Register(new FakeTool("web_fetch", (args, _) =>
            {
                calls++;
                var status = args == "fail" ? 503 : 200;
                return Task.FromResult(new ToolResult(status == 200, args) { CheckEvidence = new(args == "other" ? b : a, status) });
            }));
            var script = new[] { "fail", "fail", "other", "ok", "ok" }
                .SelectMany((arg, i) => new[] { Call($"c{i}", "web_fetch", arg), Say("done") });
            var llm = new ScriptedLlmClient(script);
            var runner = Build(store, llm, tools, new AllowListGovernance(["web_fetch"]));
            for (var i = 0; i < 5; i++)
                await runner.RunAsync(Claimed("重新检查", InputSource.Schedule) with
                {
                    ItemId = $"s{i}", Seq = i + 1, IdempotencyKey = $"schedule:{(i == 1 ? "b" : "a")}:run{i}",
                });

            Assert.Equal(5, calls);
            var history = await RawAsync(store);
            var first = history.First(e => e.Kind == SessionEventKind.ToolResult);
            Assert.Equal("503", first.Meta!["checkStatus"]);
            Assert.Equal(a, first.Meta["checkTarget"]);
            Assert.Equal("fail", first.Body);
            string Prompt(int run) => string.Join("\n", llm.Requests[run * 2].Messages.Select(m => m.Content));
            Assert.DoesNotContain("HTTP 503", Prompt(1));
            Assert.Contains($"#{first.Seq}", Prompt(2));
            Assert.Contains($"#{first.Seq}", Prompt(3));
            Assert.DoesNotContain("HTTP 503", Prompt(4));
            Assert.DoesNotContain("fail", Prompt(4));
            Assert.Contains((await store.ProjectAsync()).Items, e => e.Seq == first.Seq && e.Body == "fail");
        }
    }

    [Fact]
    public async Task Scheduled_mixed_batch_and_retracted_resolution_recompute_visible_evidence()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var target = new string('a', 64);
            var tools = new InMemoryToolRegistry();
            tools.Register(new FakeTool("web_fetch", (args, _) => Task.FromResult(
                new ToolResult(args != "fail", args) { CheckEvidence = new(target, args == "fail" ? 503 : 200) })));
            var mixed = new LlmResponse(null, [new LlmToolCall("m1", "web_fetch", "fail"),
                new LlmToolCall("m2", "web_fetch", "ok")], "tool_calls");
            var llm = new ScriptedLlmClient([Call("c1", "web_fetch", "fail"), Say("done"), mixed, Say("done"),
                Call("c2", "web_fetch", "ok"), Say("done"), Call("c3", "web_fetch", "fail"), Say("done")]);
            var runner = Build(store, llm, tools, new AllowListGovernance(["web_fetch"]));
            InputItem Item(int i) => Claimed("检查", InputSource.Schedule) with
            {
                ItemId = $"s{i}", Seq = i + 1, IdempotencyKey = $"schedule:a:run{i}",
            };
            await runner.RunAsync(Item(0));
            await runner.RunAsync(Item(1));
            await runner.RunAsync(Item(2));
            var results = (await RawAsync(store)).Where(e => e.Kind == SessionEventKind.ToolResult).ToArray();
            Assert.Equal(4, results.Length);
            Assert.Equal("true", results[1].Meta!["checkBatchMixed"]);
            Assert.Equal("true", results[2].Meta!["checkBatchMixed"]);
            Assert.Contains($"#{results[0].Seq}", string.Join("\n", llm.Requests[4].Messages.Select(m => m.Content)));
            var assembler = new SessionWindowContextAssembler(store);
            async Task<string> Prompt() => string.Join("\n", (await assembler.AssembleInitialAsync(Item(3), [])).Select(m => m.Content));
            Assert.DoesNotContain("HTTP 503", await Prompt());
            await store.AppendAsync(new SessionEvent { Kind = SessionEventKind.Retract, Source = InputSource.Text,
                Body = "retract resolution", Meta = SessionRetraction.Single(results[3].Seq) });
            Assert.Contains($"#{results[0].Seq}", await Prompt());
            await store.AppendAsync(new SessionEvent { Kind = SessionEventKind.Edit, Source = InputSource.Text,
                EditedFromSeq = results[0].Seq, Body = "edited misleading 200" });
            Assert.Contains("HTTP 503", await Prompt());
            Assert.DoesNotContain("edited misleading", await Prompt());
            await store.PurgeAsync(results[0].Seq, results[0].Seq);
            Assert.DoesNotContain("HTTP 503", await Prompt());
            await runner.RunAsync(Item(4));
            var reopened = (await RawAsync(store)).Last(e => e.Kind == SessionEventKind.ToolResult);
            Assert.Contains($"#{reopened.Seq}", await Prompt());
        }
    }

    [Fact]
    public async Task Scheduled_ordinary_failure_is_a_clue_not_business_state_and_evidence_requires_scheduled_identity()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var tools = new InMemoryToolRegistry();
            tools.Register(new FakeTool("web_fetch", (_, _) => Task.FromResult(new ToolResult(false, "old failure")
                { CheckEvidence = new(new string('a', 64), 503) })));
            tools.Register(new FakeTool("ordinary", (_, _) => Task.FromResult(new ToolResult(false, "ordinary failure"))));
            var llm = new ScriptedLlmClient([Call("a", "web_fetch"), Say("done"), Call("b", "ordinary"), Say("done")]);
            var runner = Build(store, llm, tools, new AllowListGovernance(["web_fetch", "ordinary"]));
            await runner.RunAsync(Claimed());
            await runner.RunAsync(Claimed("检查", InputSource.Schedule) with
            {
                ItemId = "s1", IdempotencyKey = "schedule:a:1",
            });
            var results = (await RawAsync(store)).Where(e => e.Kind == SessionEventKind.ToolResult).ToArray();
            Assert.False(results[0].Meta!.ContainsKey("checkKind"));
            var assembler = new SessionWindowContextAssembler(store);
            var messages = await assembler.AssembleInitialAsync(Claimed("检查", InputSource.Schedule) with
            {
                ItemId = "s2", IdempotencyKey = "schedule:a:2",
            }, []);
            var prompt = string.Join("\n", messages.Select(m => m.Content));
            Assert.Contains("ordinary failure", prompt);
            Assert.Contains("不是业务状态", prompt);
            Assert.DoesNotContain("HTTP 503", prompt);
        }
    }


    [Fact]
    public async Task An_unknown_governance_decision_never_executes_the_tool()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var calls = 0;
            var registry = new InMemoryToolRegistry();
            registry.Register(new FakeTool("action", (_, _) =>
            {
                calls++;
                return Task.FromResult(new ToolResult(true, "executed"));
            }));
            var llm = new ScriptedLlmClient([Call("c1", "action"), Say("done")]);

            await Build(store, llm, registry, new UnknownDecisionGovernance()).RunAsync(Claimed());

            Assert.Equal(0, calls);
            var result = Assert.Single(await RawAsync(store), e => e.Kind == SessionEventKind.ToolResult);
            Assert.StartsWith("denied: unknown governance decision", result.Body);
        }
    }

    /// <summary>Collects what the runner reports while the model is still writing.</summary>
    private sealed class RecordingStream : IAssistantStream
    {
        public List<string> Deltas { get; } = new();

        public List<string> Completed { get; } = new();

        public List<string> ReasoningDeltas { get; } = new();

        public List<string> ReasoningCompleted { get; } = new();

        public void OnAssistantDelta(string itemId, string delta) => Deltas.Add(delta);

        public void OnAssistantCompleted(string itemId) => Completed.Add(itemId);

        public void OnReasoningDelta(string itemId, string delta) => ReasoningDeltas.Add(delta);

        public void OnReasoningCompleted(string itemId) => ReasoningCompleted.Add(itemId);
    }

    /// <summary>Streams reasoning and then an answer, the way a real reasoning model does.</summary>
    private sealed class ReasoningLlm : ILlmClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("this client only streams");

        public Task<LlmResponse> StreamAsync(LlmRequest request, Action<string> onText, CancellationToken ct = default) =>
            throw new InvalidOperationException("this client streams both channels");

        public async Task<LlmResponse> StreamWithReasoningAsync(
            LlmRequest request,
            Action<string> onText,
            Action<string> onReasoning,
            CancellationToken ct = default)
        {
            onReasoning("先确认时区");
            await Task.Yield();
            onReasoning("再换算");
            await Task.Yield();
            onText("下午三点");

            return new LlmResponse(
                "下午三点",
                Array.Empty<LlmToolCall>(),
                "stop",
                Reasoning: "先确认时区再换算");
        }
    }

    /// <summary>Streams its answer in pieces, the way a real transport does.</summary>
    private sealed class TricklingLlm(params string[] pieces) : ILlmClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("this client only streams");

        public async Task<LlmResponse> StreamAsync(
            LlmRequest request,
            Action<string> onText,
            CancellationToken ct = default)
        {
            foreach (var piece in pieces)
            {
                onText(piece);
                await Task.Yield();
            }

            return new LlmResponse(string.Concat(pieces), Array.Empty<LlmToolCall>(), "stop");
        }
    }

    [Fact]
    public async Task Reasoning_is_recorded_and_reported_live()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var stream = new RecordingStream();
            await Build(store, new ReasoningLlm(), stream: stream).RunAsync(Claimed());

            // Recorded, not merely shown: a thinking row that only existed while the model was writing it
            // would be gone exactly when a reader asks "what was it working out".
            var thinking = Assert.Single(await RawAsync(store), e => e.Kind == SessionEventKind.Thinking);
            Assert.Equal("先确认时区再换算", thinking.Body);

            // And it arrives before the answer, in pieces, on its own channel.
            Assert.Equal(new[] { "先确认时区", "再换算" }, stream.ReasoningDeltas);
            Assert.Single(stream.ReasoningCompleted);
        }
    }

    [Fact]
    public async Task A_model_that_does_not_think_records_no_thinking_event()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            await Build(store, new ScriptedLlmClient(new[] { Say("答") })).RunAsync(Claimed());

            // An empty thinking row is worse than none: it looks like the model considered something and
            // decided not to say.
            Assert.DoesNotContain(await RawAsync(store), e => e.Kind == SessionEventKind.Thinking);
        }
    }

    [Fact]
    public async Task Reasoning_stays_out_of_what_the_model_is_asked_next()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            // A stream host is what puts the turn on the streaming path; without one the runner takes the
            // buffered call, which is also a real path.
            await Build(store, new ReasoningLlm(), stream: new RecordingStream()).RunAsync(Claimed());

            var view = await store.ProjectAsync();

            // Visible to a reader, invisible to the model: it is working notes for the turn that produced it.
            // Feeding it back would spend window on a chain of thought that is already spent.
            Assert.Contains(view.Items, e => e.Kind == SessionEventKind.Thinking);
            // IsToolControl, not IsContent: the projection shows it, the assembler never reads it.
            Assert.True(SessionEventKind.Thinking.IsToolControl());
            Assert.False(SessionEventKind.Thinking.IsContent());
        }
    }

    [Theory]
    [InlineData("file_write", "{\"path\":\"a.txt\",\"content\":\"write-secret\"}", "write-secret")]
    [InlineData("file_edit", "{\"path\":\"a.txt\",\"old_text\":\"old-secret\",\"new_text\":\"new-secret\"}", "secret")]
    [InlineData("file_write", "{broken write-secret", "write-secret")]
    public async Task Unavailable_file_mutations_do_not_persist_arguments(string name, string args, string secret)
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var llm = new ScriptedLlmClient(new[] { Call("mutation-1", name, args), Say("not performed") });
            await Build(store, llm).RunAsync(Claimed());
        }

        // Read the durable session again, not the in-flight model conversation.
        var (reopened, _) = JsonlSessionStore.Open(_dir);
        await using (reopened)
        {
            var events = await RawAsync(reopened);
            var call = Assert.Single(events, e => e.Kind == SessionEventKind.ToolCall);
            var result = Assert.Single(events, e => e.Kind == SessionEventKind.ToolResult);
            Assert.Equal("{}", call.Meta!["args"]);
            Assert.Equal("mutation-1", call.Meta["id"]);
            Assert.Equal(call.Meta["id"], result.Meta!["id"]);
            Assert.Equal("false", result.Meta["ok"]);
            Assert.DoesNotContain(events, e => e.Body.Contains(secret, StringComparison.Ordinal)
                || (e.Meta?.Values.Any(value => value.Contains(secret, StringComparison.Ordinal)) ?? false));

            var view = await reopened.ProjectAsync();
            Assert.Equal("mutation-1", Assert.Single(view.Items, e => e.Kind == SessionEventKind.ToolCall).Meta!["id"]);
            Assert.Equal("mutation-1", Assert.Single(view.Items, e => e.Kind == SessionEventKind.ToolResult).Meta!["id"]);

            var messages = await new SessionWindowContextAssembler(reopened, contextTokens: 2048, reserveForOutput: 64)
                .AssembleInitialAsync(Claimed("next"), Array.Empty<string>());
            Assert.DoesNotContain(messages, m => (m.Content?.Contains(secret, StringComparison.Ordinal) ?? false)
                || (m.ToolCalls?.Any(c => c.ArgumentsJson.Contains(secret, StringComparison.Ordinal)) ?? false));
        }
    }

    [Theory]
    [InlineData("file_write")]
    [InlineData("file_edit")]
    public async Task Registered_file_mutations_cannot_bypass_the_missing_secure_pipeline(string name)
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var calls = 0;
            var tools = new InMemoryToolRegistry();
            tools.Register(new FakeTool(name, (args, _) =>
            {
                calls++;
                return Task.FromResult(new ToolResult(true, args));
            }));
            var llm = new ScriptedLlmClient(new[]
            {
                Call("mutation-1", name, "{\"content\":\"private-content\"}"),
                Say("not performed"),
            });

            await Build(store, llm, tools, new AllowListGovernance(new[] { name })).RunAsync(Claimed());

            Assert.Equal(0, calls);
            var events = await RawAsync(store);
            var result = Assert.Single(events, e => e.Kind == SessionEventKind.ToolResult);
            using var receipt = System.Text.Json.JsonDocument.Parse(result.Body);
            Assert.Equal("not_started", receipt.RootElement.GetProperty("status").GetString());
            Assert.Equal("false", result.Meta!["ok"]);
            Assert.DoesNotContain(events, e => e.Body.Contains("private-content", StringComparison.Ordinal)
                || (e.Meta?.Values.Any(value => value.Contains("private-content", StringComparison.Ordinal)) ?? false));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Prepared_file_write_asks_when_required_and_persists_no_preview(bool approved)
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var writer = new PreparedWriteTestTool();
            var tools = new InMemoryToolRegistry();
            tools.Register(writer);
            var approval = new RecordingWriteApproval(approved);
            var llm = new ScriptedLlmClient(new[]
            {
                Call("write-1", "file_write", "{\"path\":\"C:\\\\safe\\\\new.txt\",\"content\":\"private-content\"}"),
                Say("done"),
            });
            await Build(store, llm, tools, new AskWriteGovernance(), approval).RunAsync(Claimed());

            Assert.Single(approval.Requests);
            Assert.DoesNotContain("private-content", approval.Requests[0]);
            Assert.Equal(approved ? 1 : 0, writer.Commits);
            Assert.Equal(1, writer.Disposals);
            var events = await RawAsync(store);
            var result = Assert.Single(events, e => e.Kind == SessionEventKind.ToolResult);
            Assert.Equal(approved ? "true" : "false", result.Meta!["ok"]);
            using var receipt = System.Text.Json.JsonDocument.Parse(result.Body);
            Assert.Equal(approved ? "success" : "denied", receipt.RootElement.GetProperty("status").GetString());
            Assert.Equal("C:\\safe\\new.txt", receipt.RootElement.GetProperty("path").GetString());
            Assert.Contains(approved ? "approved" : "denied", result.Body);
            Assert.DoesNotContain(events, e => e.Body.Contains("private-content", StringComparison.Ordinal)
                || (e.Meta?.Values.Any(value => value.Contains("private-content", StringComparison.Ordinal)) ?? false));
        }
    }

    [Theory]
    [InlineData("file_write")]
    [InlineData("file_edit")]
    public async Task Prepared_file_operation_commits_without_approval_when_allowed(string name)
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var writer = new PreparedWriteTestTool(name: name);
            var tools = new InMemoryToolRegistry();
            tools.Register(writer);
            var approval = new RecordingWriteApproval(false);
            var llm = new ScriptedLlmClient([Call("write-1", name, "{}"), Say("done")]);
            await Build(store, llm, tools, new AllowListGovernance([name]), approval).RunAsync(Claimed());

            Assert.Empty(approval.Requests);
            Assert.Equal(1, writer.Commits);
            Assert.Equal(1, writer.Disposals);
            var result = Assert.Single(await RawAsync(store), e => e.Kind == SessionEventKind.ToolResult);
            using var receipt = System.Text.Json.JsonDocument.Parse(result.Body);
            Assert.Equal("success", receipt.RootElement.GetProperty("status").GetString());
            Assert.Equal("not_requested", receipt.RootElement.GetProperty("approval").GetString());
        }
    }

    [Theory]
    [InlineData("conflict: target already exists", "failed")]
    [InlineData("file write status unconfirmed; verify before retrying", "unconfirmed")]
    [InlineData("denied: file write authorization changed", "denied")]
    public async Task Prepared_write_records_commit_outcome_without_tool_text(string content, string status)
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var tools = new InMemoryToolRegistry();
            tools.Register(new PreparedWriteTestTool(new ToolResult(false, content)));
            var llm = new ScriptedLlmClient([Call("write-1", "file_write", "{\"content\":\"private-content\"}"), Say("done")]);
            await Build(store, llm, tools, new AskWriteGovernance(), new RecordingWriteApproval(true)).RunAsync(Claimed());
            var events = await RawAsync(store);
            var result = Assert.Single(events, e => e.Kind == SessionEventKind.ToolResult);
            using var receipt = System.Text.Json.JsonDocument.Parse(result.Body);
            Assert.Equal(status, receipt.RootElement.GetProperty("status").GetString());
            Assert.Equal("C:\\safe\\new.txt", receipt.RootElement.GetProperty("path").GetString());
            Assert.DoesNotContain("private-content", result.Body);
        }
    }

    [Fact]
    public async Task Refused_write_cannot_be_retried_in_the_same_turn()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var writer = new PreparedWriteTestTool();
            var tools = new InMemoryToolRegistry();
            tools.Register(writer);
            var approval = new RecordingWriteApproval(false);
            var llm = new ScriptedLlmClient([
                Call("first", "file_write", "{}"), Call("retry", "file_write", "{}"), Say("done")]);
            await Build(store, llm, tools, new AskWriteGovernance(), approval).RunAsync(Claimed());
            Assert.Single(approval.Requests);
            Assert.Equal(0, writer.Commits);
            var events = await RawAsync(store);
            var retry = Assert.Single(events, e => e.Kind == SessionEventKind.ToolResult && e.Meta?.GetValueOrDefault("id") == "retry");
            using var receipt = System.Text.Json.JsonDocument.Parse(retry.Body);
            Assert.Equal("not_started", receipt.RootElement.GetProperty("status").GetString());
        }
    }


    [Fact]
    public async Task A_failing_tool_is_counted_by_reason_not_by_call()
    {
        // The summary exists to answer "what keeps breaking", so the same failure twice is one row counted
        // twice — not two rows, which would be the session log with extra steps.
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var failures = new InMemoryToolFailureLog();
            var llm = new ScriptedLlmClient(new[]
            {
                Call("c1", "flaky"),
                Call("c2", "flaky"),
                Say("done"),
            });

            var tools = new InMemoryToolRegistry();
            tools.Register(new FakeTool("flaky", (_, _) =>
                Task.FromResult(new ToolResult(false, "file not found: a.txt"))));

            await Build(store, llm, tools, failures: failures).RunAsync(Claimed());

            var row = Assert.Single(failures.Top(10));
            Assert.Equal("flaky", row.Tool);
            Assert.Equal(2, row.Count);
        }
    }

    [Fact]
    public async Task A_command_that_ran_and_exited_non_zero_is_a_result_and_not_a_failure()
    {
        // The distinction the whole summary rests on: `grep` finding nothing, or `git` reporting a conflict,
        // is the answer to a question. Counting those would fill the summary with "the tool is broken" rows
        // for tools that are working exactly as asked.
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var failures = new InMemoryToolFailureLog();
            var llm = new ScriptedLlmClient(new[] { Call("c1", "bash"), Say("done") });

            var tools = new InMemoryToolRegistry();
            tools.Register(new FakeTool("bash", (_, _) =>
                Task.FromResult(new ToolResult(true, "exit 1\nfatal: not a git repository"))));

            // Allowed, so the call actually reaches the tool: the point is what happens *after* it ran.
            var allow = new AllowListGovernance(new[] { "bash" });

            await Build(store, llm, tools, governance: allow, failures: failures).RunAsync(Claimed());

            Assert.Empty(failures.Top(10));
            Assert.Equal(0, failures.Totals.Occurrences);
        }
    }

    [Fact]
    public async Task A_denied_call_is_counted_because_the_tool_never_ran()
    {
        // Governance refusing is not the tool failing, but from the reader's side nothing happened and the
        // model has to try again — that is worth seeing in the summary.
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var failures = new InMemoryToolFailureLog();
            var llm = new ScriptedLlmClient(new[] { Call("c1", "blocked"), Say("done") });

            var tools = new InMemoryToolRegistry();
            tools.Register(new FakeTool("blocked"));

            // Nothing is on the allow list, so governance refuses before the tool is reached.
            var deny = new AllowListGovernance(Array.Empty<string>());

            await Build(store, llm, tools, governance: deny, failures: failures).RunAsync(Claimed());

            var row = Assert.Single(failures.Top(10));
            Assert.Equal("blocked", row.Tool);
            Assert.Contains("denied", row.LastMessage, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_model_failure_is_recorded_with_what_to_do_and_the_id_to_quote()
    {
        // The body is one line and it is what a reader scans; the request id is what a person has to look up,
        // so it rides in the meta. Both have to survive — a failure that can only be re-created is not a
        // diagnosis.
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var llm = new ThrowingLlmClient("boom", ModelCallFailure.Rejected(
                "模型补全",
                System.Net.HttpStatusCode.Unauthorized,
                """{"error":{"message":"invalid api key"}}""",
                "req_9f2c"));

            // The runner records the failure and then rethrows: the log is a record, not a swallow.
            await Assert.ThrowsAsync<ModelCallFailure>(() => Build(store, llm).RunAsync(Claimed()));

            var events = await RawAsync(store);
            var failure = Assert.Single(
                events,
                e => e.Kind == SessionEventKind.System && e.Body.StartsWith("error: ", StringComparison.Ordinal));

            Assert.Contains("API Key", failure.Meta!["error.next"], StringComparison.Ordinal);
            Assert.Equal("req_9f2c", failure.Meta["error.requestId"]);
            Assert.Equal("rejected", failure.Meta["error.kind"]);
        }
    }

    [Fact]
    public async Task An_ordinary_exception_records_no_error_meta()
    {
        // The keys must not exist when there is nothing behind them: a reader who sees an empty `error.requestId`
        // has been told the provider gave an id, and it did not.
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Build(store, new ThrowingLlmClient("boom")).RunAsync(Claimed()));

            var failure = Assert.Single(
                await RawAsync(store),
                e => e.Kind == SessionEventKind.System && e.Body.StartsWith("error: ", StringComparison.Ordinal));

            Assert.True(failure.Meta is null || !failure.Meta.ContainsKey("error.kind"));
        }
    }

    [Fact]
    public async Task A_multi_line_reply_is_recorded_with_its_line_structure()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            // A reply is markdown, and its structure *is* the line breaks.
            const string Reply = "## 步骤\n\n- 打开书房的灯\n- 关掉客厅的灯\n\n> 完成后再回报一次";

            var runner = Build(store, new ScriptedLlmClient(new[] { Say(Reply) }));

            await runner.RunAsync(Claimed());

            var assistant = Assert.Single(await RawAsync(store), e => e.Kind == SessionEventKind.Assistant);
            Assert.Equal(Reply, assistant.Body);
        }
    }

    [Fact]
    public async Task A_tool_that_returns_a_picture_shows_it_to_the_model()
    {
        // The blind-app fallback: UI Automation cannot see Electron or a canvas, so the tool hands the model a
        // screenshot. A tool message carries text only, so the picture rides in the message after it — and the
        // log still holds just the text.
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var picture = "data:image/png;base64,iVBORw0KGgo=";
            var registry = new InMemoryToolRegistry();
            registry.Register(new FakeTool(
                "shot",
                (_, _) => Task.FromResult(new ToolResult(true, "截图 100×50（媒体 m1）", new[] { picture }))));

            var llm = new ScriptedLlmClient(new[]
            {
                Call("c1", "shot"),
                Say("看到了"),
            });

            var runner = Build(
                store,
                llm,
                tools: registry,
                governance: new AllowListGovernance(new[] { "shot" }));

            await runner.RunAsync(Claimed());

            // The second request is the one made after the tool ran.
            var after = llm.Requests[1].Messages;
            var withPicture = Assert.Single(after, message => message.Images is { Count: > 0 });
            Assert.Equal("user", withPicture.Role);
            Assert.Equal(picture, Assert.Single(withPicture.Images!));

            // The log records the text, never the bytes.
            var events = await RawAsync(store);
            var result = Assert.Single(events, e => e.Kind == SessionEventKind.ToolResult);
            Assert.Contains("媒体 m1", result.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("base64", string.Join("\n", events.Select(e => e.Body)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_tool_that_returns_no_picture_adds_no_message()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var registry = new InMemoryToolRegistry();
            registry.Register(new FakeTool("tick", (_, _) => Task.FromResult(new ToolResult(true, "ok"))));

            var llm = new ScriptedLlmClient(new[] { Call("c1", "tick"), Say("done") });

            await Build(store, llm, tools: registry, governance: new AllowListGovernance(new[] { "tick" }))
                .RunAsync(Claimed());

            Assert.DoesNotContain(llm.Requests[1].Messages, message => message.Images is { Count: > 0 });
        }
    }

    [Fact]
    public async Task Streaming_reports_pieces_and_still_logs_one_complete_answer()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var stream = new RecordingStream();
            var runner = Build(store, new TricklingLlm("你", "好"), stream: stream);

            var result = await runner.RunAsync(Claimed());

            // What the UI saw while it was being written.
            Assert.Equal(new[] { "你", "好" }, stream.Deltas);
            Assert.Contains(Claimed().ItemId, stream.Completed);

            // And what the session records: one complete answer, never the fragments.
            var events = await RawAsync(store);
            var assistant = Assert.Single(events, e => e.Kind == SessionEventKind.Assistant);
            Assert.Equal("你好", assistant.Body);
            Assert.Equal(1, result.AssistantMessages);
        }
    }

    [Fact]
    public async Task Turn_writes_events_in_the_documented_order()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var runner = Build(store, new ScriptedLlmClient(new[] { Say("hi there") }));

            var result = await runner.RunAsync(Claimed());

            var kinds = (await RawAsync(store)).Select(e => e.Kind).ToArray();
            Assert.Equal(
                new[]
                {
                    SessionEventKind.TurnStart,
                    SessionEventKind.StepStart,
                    SessionEventKind.User,
                    SessionEventKind.Assistant,
                    SessionEventKind.StepEnd,
                    SessionEventKind.TurnEnd,
                },
                kinds);

            Assert.Equal(TurnEndReason.Completed, result.Reason);
            Assert.Equal(1, result.Steps);
            Assert.Equal(1, result.AssistantMessages);

            var end = (await RawAsync(store)).Single(e => e.Kind == SessionEventKind.TurnEnd);
            Assert.Equal(TurnEndReason.Completed, end.Meta!["reason"]);
        }
    }

    [Fact]
    public async Task Turn_requires_a_claimed_running_item()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var runner = Build(store, new ScriptedLlmClient(new[] { Say("x") }));

            var queued = new InputItem
            {
                ItemId = "item-1",
                Seq = 1,
                Status = InputItemStatus.Queued,
                Source = InputSource.Text,
                Payload = "hello",
                IdempotencyKey = "text:client:1",
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(queued));
        }
    }

    [Fact]
    public async Task Tool_results_commit_in_model_order_not_completion_order()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var tools = new InMemoryToolRegistry();
            // "slow" is dispatched first but finishes last.
            tools.Register(new FakeTool("slow", async (_, ct) =>
            {
                await Task.Delay(80, ct);
                return new ToolResult(true, "slow-done");
            }));
            tools.Register(new FakeTool("fast", (_, _) => Task.FromResult(new ToolResult(true, "fast-done"))));

            var llm = new ScriptedLlmClient(new[]
            {
                new LlmResponse(null, new[] { new LlmToolCall("c1", "slow", "{}"), new LlmToolCall("c2", "fast", "{}") }, "tool_calls"),
                Say("all done"),
            });

            var runner = Build(store, llm, tools, new AllowListGovernance(new[] { "slow", "fast" }));
            await runner.RunAsync(Claimed());

            var events = await RawAsync(store);
            Assert.Equal(new[] { "slow", "fast" }, events.Where(e => e.Kind == SessionEventKind.ToolCall).Select(e => e.Body));
            Assert.Equal(new[] { "slow-done", "fast-done" },
                events.Where(e => e.Kind == SessionEventKind.ToolResult).Select(e => e.Body));

            // The second request must carry the tool results in model order too.
            var second = llm.Requests[1];
            var toolMessages = second.Messages.Where(m => m.Role == "tool").Select(m => m.Content).ToArray();
            Assert.Equal(new[] { "slow-done", "fast-done" }, toolMessages);
        }
    }

    [Fact]
    public async Task Tool_failure_is_fed_back_and_the_turn_continues()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var tools = new InMemoryToolRegistry();
            tools.Register(new FakeTool("boom", (_, _) => throw new InvalidOperationException("kaboom")));

            var llm = new ScriptedLlmClient(new[] { Call("c1", "boom"), Say("recovered") });
            var runner = Build(store, llm, tools, new AllowListGovernance(new[] { "boom" }));

            var result = await runner.RunAsync(Claimed());

            Assert.Equal(TurnEndReason.Completed, result.Reason);
            Assert.Equal(2, result.Steps);

            var events = await RawAsync(store);
            var toolResult = events.Single(e => e.Kind == SessionEventKind.ToolResult);
            Assert.Equal("false", toolResult.Meta!["ok"]);
            Assert.Contains("kaboom", toolResult.Body);

            var fedBack = llm.Requests[1].Messages.Single(m => m.Role == "tool");
            Assert.Contains("kaboom", fedBack.Content);
        }
    }

    [Fact]
    public async Task Tool_timeout_is_reported_as_a_result()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var tools = new InMemoryToolRegistry();
            tools.Register(new FakeTool("hang", async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new ToolResult(true, "never");
            }));

            var llm = new ScriptedLlmClient(new[] { Call("c1", "hang"), Say("ok") });
            var runner = Build(store, llm, tools, new AllowListGovernance(new[] { "hang" }),
                options: new AgentTurnOptions { ToolTimeout = TimeSpan.FromMilliseconds(40) });

            var result = await runner.RunAsync(Claimed());

            Assert.Equal(TurnEndReason.Completed, result.Reason);
            var toolResult = (await RawAsync(store)).Single(e => e.Kind == SessionEventKind.ToolResult);
            Assert.Equal("false", toolResult.Meta!["ok"]);
            Assert.Contains("timeout", toolResult.Body);
        }
    }

    [Fact]
    public async Task Denied_tool_is_a_result_not_an_exception()
    {
        var (store, _) = SessionStore();
        await using (store)
        {
            var tools = new InMemoryToolRegistry();
            tools.Register(new FakeTool("secret"));

            var llm = new ScriptedLlmClient(new[] { Call("c1", "secret"), Say("ok") });
            // Fail-closed governance: nothing is allowed.
            var runner = Build(store, llm, tools, new AllowListGovernance(Array.Empty<string>()));

            var result = await runner.RunAsync(Claimed());

            Assert.Equal(TurnEndReason.Completed, result.Reason);
            var toolResult = (await RawAsync(store)).Single(e => e.Kind == SessionEventKind.ToolResult);
            Assert.Equal("false", toolResult.Meta!["ok"]);
            Assert.Contains("denied", toolResult.Body);
        }
    }

    [Fact]
    public async Task Ask_without_approval_is_denied()
    {
        var (store, _) = SessionStore();
        await using (store)
        {
            var tools = new InMemoryToolRegistry();
            tools.Register(new FakeTool("maybe"));

            var llm = new ScriptedLlmClient(new[] { Call("c1", "maybe"), Say("ok") });
            var runner = Build(store, llm, tools, new AllowListGovernance(Array.Empty<string>(), new[] { "maybe" }));

            await runner.RunAsync(Claimed());

            var toolResult = (await RawAsync(store)).Single(e => e.Kind == SessionEventKind.ToolResult);
            Assert.Equal("false", toolResult.Meta!["ok"]);
            Assert.Contains("approval denied", toolResult.Body);
        }
    }

    [Fact]
    public async Task Unknown_tool_is_a_result()
    {
        var (store, _) = SessionStore();
        await using (store)
        {
            var llm = new ScriptedLlmClient(new[] { Call("c1", "ghost"), Say("ok") });
            var runner = Build(store, llm, governance: new AllowListGovernance(new[] { "ghost" }));

            await runner.RunAsync(Claimed());

            var toolResult = (await RawAsync(store)).Single(e => e.Kind == SessionEventKind.ToolResult);
            Assert.Contains("unknown tool", toolResult.Body);
        }
    }

    [Fact]
    public async Task Hooks_run_in_phase_order_and_finally_always_runs()
    {
        var (store, _) = SessionStore();
        await using (store)
        {
            var log = new List<string>();
            var hooks = new HookRegistry();
            hooks.Register(new RecordingHook(AgentPhase.PreDispatch, "pre-dispatch", log));
            hooks.Register(new RecordingHook(AgentPhase.PostDispatch, "post-dispatch", log));
            hooks.Register(new RecordingHook(AgentPhase.PreAgentBuild, "pre-build", log));
            hooks.Register(new RecordingHook(AgentPhase.PostAgentBuild, "post-build", log));
            hooks.Register(new RecordingHook(AgentPhase.PreExecute, "pre-execute", log));
            hooks.Register(new RecordingHook(AgentPhase.PostResponse, "post-response", log));
            hooks.Register(new RecordingHook(AgentPhase.Finally, "finally", log));

            var runner = Build(store, new ScriptedLlmClient(new[] { Say("done") }), hooks: hooks);
            await runner.RunAsync(Claimed());

            Assert.Equal(
                new[] { "pre-dispatch", "post-dispatch", "pre-build", "post-build", "pre-execute", "post-response", "finally" },
                log);
        }
    }

    [Fact]
    public async Task Injected_prompts_reach_the_system_message_and_the_session()
    {
        var (store, _) = SessionStore();
        await using (store)
        {
            var log = new List<string>();
            var hooks = new HookRegistry();
            hooks.Register(new RecordingHook(AgentPhase.PreExecute, "inject", log, ctx =>
            {
                ctx.Inject("voice style: be brief", 10);
                return HookOutcome.Continue;
            }));

            var llm = new ScriptedLlmClient(new[] { Say("ok") });
            var runner = Build(store, llm, hooks: hooks);
            await runner.RunAsync(Claimed());

            Assert.Contains("voice style: be brief", llm.Requests[0].Messages[0].Content);

            var system = (await RawAsync(store)).Single(e => e.Kind == SessionEventKind.System);
            Assert.Contains("voice style: be brief", system.Body);
        }
    }

    [Fact]
    public async Task ShortCircuit_skips_the_agent_entirely()
    {
        var (store, _) = SessionStore();
        await using (store)
        {
            var log = new List<string>();
            var hooks = new HookRegistry();
            hooks.Register(new RecordingHook(AgentPhase.PreExecute, "stop-here", log, _ => HookOutcome.ShortCircuit));

            var llm = new ScriptedLlmClient(Array.Empty<LlmResponse>());
            var runner = Build(store, llm, hooks: hooks);

            var result = await runner.RunAsync(Claimed());

            Assert.Equal(TurnEndReason.Completed, result.Reason);
            Assert.Empty(llm.Requests);
            Assert.Equal(0, result.Steps);

            var events = await RawAsync(store);
            Assert.DoesNotContain(events, e => e.Kind == SessionEventKind.Assistant);
            Assert.Equal("short-circuit", events.Single(e => e.Kind == SessionEventKind.TurnEnd).Meta!["detail"]);
        }
    }

    [Fact]
    public async Task SkipAgent_is_sticky_and_skips_the_agent_steps()
    {
        var (store, _) = SessionStore();
        await using (store)
        {
            var log = new List<string>();
            var hooks = new HookRegistry();
            hooks.Register(new RecordingHook(AgentPhase.PreDispatch, "skip", log, _ => HookOutcome.SkipAgent));
            hooks.Register(new RecordingHook(AgentPhase.PreExecute, "after-skip", log));

            var llm = new ScriptedLlmClient(Array.Empty<LlmResponse>());
            var runner = Build(store, llm, hooks: hooks);

            var result = await runner.RunAsync(Claimed());

            Assert.True(hooks.SkipAgent);
            Assert.Contains("after-skip", log); // sticky does not block later hooks
            Assert.Equal(0, result.Steps);
            Assert.Equal("skip-agent", (await RawAsync(store)).Single(e => e.Kind == SessionEventKind.TurnEnd).Meta!["detail"]);
        }
    }

    [Fact]
    public async Task Cancellation_records_aborted_and_runs_error_and_finally_hooks()
    {
        var (store, _) = SessionStore();
        await using (store)
        {
            var log = new List<string>();
            var hooks = new HookRegistry();
            hooks.Register(new RecordingHook(AgentPhase.OnError, "on-error", log));
            hooks.Register(new RecordingHook(AgentPhase.Finally, "finally", log));

            var runner = Build(store, new BlockingLlmClient(), hooks: hooks);

            using var cts = new CancellationTokenSource();
            var turn = runner.RunAsync(Claimed(), cts.Token);
            await Task.Delay(60);
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);

            var events = await RawAsync(store);
            var end = events.Single(e => e.Kind == SessionEventKind.TurnEnd);
            Assert.Equal(TurnEndReason.Aborted, end.Meta!["reason"]);
            Assert.Contains("finally", log);
        }
    }

    [Fact]
    public async Task Exception_records_error_content_and_turn_end_error()
    {
        var (store, _) = SessionStore();
        await using (store)
        {
            var log = new List<string>();
            var hooks = new HookRegistry();
            hooks.Register(new RecordingHook(AgentPhase.OnError, "on-error", log, ctx =>
            {
                ctx.Extras["seen-error"] = ctx.Error ?? "(none)";
                return HookOutcome.Continue;
            }));
            hooks.Register(new RecordingHook(AgentPhase.Finally, "finally", log));

            var runner = Build(store, new ThrowingLlmClient("model exploded"), hooks: hooks);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Claimed()));
            Assert.Equal("model exploded", ex.Message);

            var events = await RawAsync(store);
            Assert.Contains(events, e => e.Kind == SessionEventKind.System && e.Body.Contains("model exploded"));
            Assert.Equal(TurnEndReason.Error, events.Single(e => e.Kind == SessionEventKind.TurnEnd).Meta!["reason"]);
            Assert.Equal(new[] { "on-error", "finally" }, log);
        }
    }

    [Fact]
    public async Task Step_limit_is_recorded_as_budget_exhaustion()
    {
        var (store, _) = SessionStore();
        await using (store)
        {
            var tools = new InMemoryToolRegistry();
            tools.Register(new FakeTool("tick"));

            // Always asks for another tool call: the turn can only end on the step budget.
            var llm = new ScriptedLlmClient(new[]
            {
                Call("c1", "tick"), Call("c2", "tick"), Call("c3", "tick"),
            });

            var runner = Build(store, llm, tools, new AllowListGovernance(new[] { "tick" }),
                options: new AgentTurnOptions { MaxSteps = 2 });

            var result = await runner.RunAsync(Claimed());

            Assert.Equal(TurnEndReason.MaxTokens, result.Reason);
            Assert.Equal(2, result.Steps);

            var end = (await RawAsync(store)).Single(e => e.Kind == SessionEventKind.TurnEnd);
            Assert.Equal("step-limit", end.Meta!["detail"]);
        }
    }

    [Fact]
    public async Task A_desktop_toolset_is_given_the_family_budget()
    {
        // A real desktop task runs 20–40 look-act-look steps. The default toolset earns 32 with no test
        // or configuration, because the registry names the family.
        await using var store = new JsonlSessionStore(new FakeSink());

        var tools = new InMemoryToolRegistry();
        tools.Register(new FakeTool("desktop_act"));
        tools.Register(new FakeTool("desktop_observe"));

        var responses = new List<LlmResponse>();
        for (var i = 0; i < 12; i++)
        {
            responses.Add(Call($"c{i}", i % 2 == 0 ? "desktop_observe" : "desktop_act"));
        }
        responses.Add(Say("完成"));

        var runner = Build(
            store,
            new ScriptedLlmClient(responses),
            tools,
            new AllowListGovernance(new[] { "desktop_act", "desktop_observe" }));

        var result = await runner.RunAsync(Claimed());

        // Thirteen steps must complete rather than hit step-limit.
        Assert.Equal(TurnEndReason.Completed, result.Reason);
        Assert.Equal(13, result.Steps);
    }

    [Fact]
    public async Task A_plain_toolset_still_stops_at_the_default_budget()
    {
        // The larger ceiling only binds turns that were never going to finish quickly. Chat ends when the
        // answer is written, so a non-desktop toolset still stops at the base 8.
        await using var store = new JsonlSessionStore(new FakeSink());

        var tools = new InMemoryToolRegistry();
        tools.Register(new FakeTool("clock"));

        var responses = new List<LlmResponse>();
        for (var i = 0; i < 9; i++)
        {
            responses.Add(Call($"c{i}", "clock"));
        }
        responses.Add(Say("完成"));

        var runner = Build(
            store,
            new ScriptedLlmClient(responses),
            tools,
            new AllowListGovernance(new[] { "clock" }));

        var result = await runner.RunAsync(Claimed());

        Assert.Equal(TurnEndReason.MaxTokens, result.Reason);
        Assert.Equal(8, result.Steps);
    }

    [Fact]
    public void The_family_budget_pick_the_largest_registered_match()
    {
        var options = new AgentTurnOptions { MaxSteps = 8, ToolStepBudgets = new Dictionary<string, int> { ["desktop"] = 32 } };

        Assert.Equal(32, options.StepsAllowedFor(new[] { "desktop_act", "clock" }));
        Assert.Equal(32, options.StepsAllowedFor(new[] { "desktop_observe" }));

        // No match falls back to the base number, not to zero and not to refuse-to-run.
        Assert.Equal(8, options.StepsAllowedFor(new[] { "clock" }));
        Assert.Equal(8, options.StepsAllowedFor(Array.Empty<string>()));

        // A larger MaxSteps stays the ceiling: budgets raise, they do not lower.
        var raised = options with { MaxSteps = 48 };
        Assert.Equal(48, raised.StepsAllowedFor(new[] { "desktop_act" }));
    }

    [Fact]
    public void A_web_toolset_gets_room_for_a_search_then_several_reads()
    {
        // The web sub-agent runs search, fetch, fetch, … and needs 24 steps to report.
        var options = new AgentTurnOptions();

        Assert.Equal(24, options.StepsAllowedFor(new[] { "web_search", "web_fetch", "task_report" }));
        Assert.Equal(8, options.StepsAllowedFor(new[] { "clock" }));
    }

    [Fact]
    public async Task Flush_failure_surfaces_and_the_turn_is_not_marked_successful()
    {
        // Buffered store: appends alone are not durable, so the runner's own end-of-turn barrier
        // must fail loudly.
        var sink = new FakeSink();
        var store = new JsonlSessionStore(sink, flushOnAppend: false);
        await using (store)
        {
            sink.FailNextFlush();
            var runner = Build(store, new ScriptedLlmClient(new[] { Say("hi") }));

            await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(Claimed()));
            Assert.True(store.Faulted);
        }
    }

    [Fact]
    public async Task Per_append_flush_failure_also_surfaces()
    {
        var sink = new FakeSink();
        var store = new JsonlSessionStore(sink, flushOnAppend: true);
        await using (store)
        {
            sink.FailNextFlush();
            var runner = Build(store, new ScriptedLlmClient(new[] { Say("hi") }));

            await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(Claimed()));
            Assert.True(store.Faulted);
        }
    }

    [Fact]
    public async Task Buffered_mode_turn_is_durable_only_after_the_turn_completes()
    {
        var sink = new FakeSink();
        var store = new JsonlSessionStore(sink, flushOnAppend: false);
        await using (store)
        {
            var runner = Build(store, new ScriptedLlmClient(new[] { Say("hi") }));
            var result = await runner.RunAsync(Claimed());

            Assert.Equal(TurnEndReason.Completed, result.Reason);
            Assert.Equal(0, store.Faulted ? 1 : 0); // not faulted

            // Everything the turn wrote is durable once it returned.
            var durable = sink.ReadLines().Count;
            Assert.True(durable >= 6, $"expected >= 6 durable records, got {durable}");
        }
    }

    [Fact]
    public async Task A_turn_uses_the_gear_its_message_carried_on_every_step()
    {
        // A tool loop runs several completions for one message. If only the first step saw the gear, the
        // answer would be reasoned at one level and its follow-up at another.
        var (store, _) = SessionStore();
        await using (store)
        {
            var tools = new InMemoryToolRegistry();
            tools.Register(new FakeTool("tick"));

            var llm = new GearRecordingLlmClient(Call("c1", "tick"), Say("done"));
            var runner = Build(store, llm, tools, new AllowListGovernance(new[] { "tick" }));

            await runner.RunAsync(Claimed() with { ReasoningEffort = "high" });

            Assert.Equal(new[] { "high", "high" }, llm.Seen);
        }
    }

    [Fact]
    public async Task A_turn_without_a_gear_leaves_the_choice_to_the_configured_default()
    {
        // Voice and scheduled turns carry no gear. They must arrive as "no choice", which is what lets the
        // client fall back — null is not the same as off, which would drop the parameter entirely.
        var (store, _) = SessionStore();
        await using (store)
        {
            var llm = new GearRecordingLlmClient(Say("hi"));
            var runner = Build(store, llm);

            await runner.RunAsync(Claimed());

            Assert.Equal(new string?[] { null }, llm.Seen);
        }
    }

    private (JsonlSessionStore Store, long Repaired) SessionStore() => JsonlSessionStore.Open(_dir);
    [Fact]
    public async Task RetriedTurnDoesNotLogTheMessageTwice()
    {
        var (store, _) = JsonlSessionStore.Open(_dir);
        await using (store)
        {
            var runner = Build(store, new ConstantLlm("好的"));

            await runner.RunAsync(Claimed("我喜欢深色主题"));

            // Exactly what a restart does: the same item, run again from the beginning.
            await runner.RunAsync(Claimed("我喜欢深色主题"));

            var users = (await RawAsync(store)).Where(e => e.Kind == SessionEventKind.User).ToList();

            // One message, logged once — and it is the *same* event, not a copy: the seq the memory guard keys
            // on has to be stable across a retry, or the fact is stored twice and recalled as two pieces of
            // independent evidence.
            var only = Assert.Single(users);
            Assert.Equal("我喜欢深色主题", only.Body);
        }
    }

    private sealed class ConstantLlm(string text) : ILlmClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default) =>
            Task.FromResult(new LlmResponse(text, Array.Empty<LlmToolCall>(), "stop"));

        public Task<LlmResponse> StreamAsync(
            LlmRequest request,
            Action<string> onText,
            CancellationToken ct = default)
        {
            onText(text);
            return Task.FromResult(new LlmResponse(text, Array.Empty<LlmToolCall>(), "stop"));
        }
    }
}
