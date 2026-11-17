namespace Partner.Smoke;

/// <summary>Minimal tool used by the smoke run.</summary>
internal sealed class StubTool(string name, string answer) : ITool
{
    public ToolSpec Spec { get; } = new(name, $"stub tool returning {answer}", "{\"type\":\"object\"}");

    public Task<ToolResult> InvokeAsync(string argumentsJson, CancellationToken ct = default) =>
        Task.FromResult(new ToolResult(true, answer));
}

/// <summary>Hook that injects a prompt fragment, proving the injection channel works end to end.</summary>
internal sealed class InjectingHook : AgentHookBase
{
    public override AgentPhase Phase => AgentPhase.PreExecute;

    public override string Name => "smoke.inject";

    public override ValueTask<HookOutcome> RunAsync(HookContext ctx, CancellationToken ct)
    {
        ctx.Inject("brief", 10);
        return ValueTask.FromResult(HookOutcome.Continue);
    }
}

/// <summary>
/// Lets a scripted model that drives the desktop tools as the main agent do the same work as the desktop
/// sub-agent, through the real delegation path.
/// <para>
/// The main agent's side is fixed: a user message becomes a delegation of that text, a delegation result becomes a
/// short acknowledgement, and the sub-agent's report coming back becomes another. The sub-agent's side is the
/// wrapped model, with one adjustment — when it would end its turn with text, it reports success instead, which is
/// what a job needs to end. Nothing in between is changed, so the checks still exercise governance, approval,
/// leases and the tools exactly as they did.
/// </para>
/// </summary>
internal sealed class DesktopAgentShim(ILlmClient inner) : ILlmClient
{
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var isSubAgent = (request.Messages.FirstOrDefault()?.Content ?? string.Empty).Contains("## 汇报方式", StringComparison.Ordinal);
        var last = request.Messages[^1];

        if (!isSubAgent)
        {
            if (last.Role == "tool")
            {
                return new LlmResponse("交给桌面操控了。", [], "stop");
            }

            var text = last.Content ?? string.Empty;
            return text.StartsWith("[子 agent", StringComparison.Ordinal)
                ? new LlmResponse("收到子 agent 的汇报。", [], "stop")
                : new LlmResponse(
                    null,
                    [new LlmToolCall("delegate", "agent_delegate", System.Text.Json.JsonSerializer.Serialize(new { scenario = "desktop", goal = text }))],
                    "tool_calls");
        }

        if (last is { Role: "tool", Name: "task_report" })
        {
            return new LlmResponse("已汇报。", [], "stop");
        }

        var response = await inner.CompleteAsync(request, ct).ConfigureAwait(false);
        return response.ToolCalls.Count > 0
            ? response
            : new LlmResponse(
                null,
                [new LlmToolCall("report", "task_report", System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = "success",
                    summary = string.IsNullOrWhiteSpace(response.Text) ? "完成" : response.Text,
                    evidence = "脚本化模型的收尾",
                }))],
                "tool_calls");
    }
}

internal static partial class Program
{
    /// <summary>Synthetic voice components: the whole pipeline without a microphone, a speaker or a service.</summary>
    private sealed class SyntheticVoiceComponents(string transcript) : IVoiceComponents
    {
        public SyntheticAudioSource? LastSource { get; private set; }

        public SyntheticAudioSink? LastSink { get; private set; }

        public List<string> TtsRequests { get; } = new();

        public bool FailToOpenSink { get; set; }

        public IAudioSource OpenSource(VoiceInputSetting setting)
        {
            LastSource = new SyntheticAudioSource(16_000, "synthetic-mic");
            return LastSource;
        }

        public IAudioSink OpenSink(VoiceOutputSetting setting)
        {
            if (FailToOpenSink)
            {
                throw new InvalidOperationException("smoke: simulated device failure");
            }

            LastSink = new SyntheticAudioSink(setting.SampleRate, "synthetic-speaker");
            return LastSink;
        }

        public IReadOnlyList<string> ListInputDevices() => new[] { "synthetic-mic" };

        public IReadOnlyList<string> ListOutputDevices() => new[] { "synthetic-speaker" };

        public IAsrClient CreateAsr(AsrOptions options) => new ScriptedAsr(transcript);

        public ITtsClient CreateTts(TtsOptions options) => new ScriptedTts(TtsRequests);

        private sealed class ScriptedAsr(string text) : IAsrClient
        {
            public Task<string> TranscribeAsync(float[] pcm, CancellationToken ct = default) => Task.FromResult(text);

            public async IAsyncEnumerable<string> TranscribeStreamAsync(
                float[] pcm,
                [EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return text;
                await Task.CompletedTask;
            }

            public void Dispose()
            {
            }
        }

        private sealed class ScriptedTts(List<string> requests) : ITtsClient
        {
            public async IAsyncEnumerable<float[]> SynthesizeAsync(
                string text,
                [EnumeratorCancellation] CancellationToken ct = default)
            {
                requests.Add(text);
                yield return new float[2400];
                await Task.CompletedTask;
            }

            public void Dispose()
            {
            }
        }
    }
    /// <summary>
    /// A minimal OpenAI-compatible endpoint over a raw socket: enough to prove the production client works
    /// against a real HTTP hop, without depending on a provider being installed on this machine.
    /// </summary>
    private sealed class FakeChatProvider : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _reply;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public FakeChatProvider(int port, string reply)
        {
            _reply = reply;
            _listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            _listener.Start();
            _serving = Task.Run(ServeAsync);
        }

        public string? RequestPath { get; private set; }

        public string? SawAuthorization { get; private set; }

        /// <summary>The last request body, so the message shape sent to a provider can be asserted.</summary>
        public string? LastRequestBody { get; private set; }

        /// <summary>Every request line seen, in order — the only way to see where a stuck turn stopped.</summary>
        public List<string> Requests { get; } = new();

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                using (client)
                {
                    await AnswerAsync(client).ConfigureAwait(false);
                }
            }
        }

        private async Task AnswerAsync(TcpClient client)
        {
            var stream = client.GetStream();

            // Read the request framing byte by byte, deliberately without a StreamReader: a buffered reader
            // pre-reads into its own buffer, so a later read straight from the stream would wait forever for
            // bytes the reader had already swallowed. Content-Length also counts bytes, not characters.
            var header = new List<byte>();
            var single = new byte[1];

            while (true)
            {
                var read = await stream.ReadAsync(single);
                if (read == 0)
                {
                    return;
                }

                header.Add(single[0]);

                if (header.Count >= 4
                    && header[^4] == (byte)'\r' && header[^3] == (byte)'\n'
                    && header[^2] == (byte)'\r' && header[^1] == (byte)'\n')
                {
                    break;
                }
            }

            var lines = System.Text.Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
            RequestPath = lines[0];
            Requests.Add(RequestPath);

            var length = 0;

            foreach (var line in lines.Skip(1))
            {
                if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                {
                    SawAuthorization = line["Authorization:".Length..].Trim();
                }

                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    length = int.Parse(line["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture);
                }
            }

            var body = new byte[length];
            var filled = 0;
            while (filled < length)
            {
                var chunk = await stream.ReadAsync(body.AsMemory(filled, length - filled));
                if (chunk == 0)
                {
                    break;
                }

                filled += chunk;
            }

            LastRequestBody = System.Text.Encoding.UTF8.GetString(body, 0, filled);

            // A streamed completion is answered frame by frame with a pause between them, so the session view
            // can be observed while the reply is still arriving. No Content-Length: SSE ends at EOF, which is
            // also what lets each frame out before the next one exists.
            if (LastRequestBody.Contains("\"stream\":true", StringComparison.Ordinal))
            {
                await AnswerStreamAsync(stream).ConfigureAwait(false);
                return;
            }

            // One socket serves both the completion and the listing, so the proxy is exercised for real.
            var payload = RequestPath?.Contains("/models", StringComparison.Ordinal) == true
                ? "{\"data\":[{\"id\":\"smoke-model-a\"},{\"id\":\"smoke-model-b\"}]}"
                : RequestPath?.Contains("/tokenize", StringComparison.Ordinal) == true
                    ? "{\"tokens\":[1,2,3,4,5,6,7]}"
                : System.Text.Json.JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new
                        {
                            message = new { role = "assistant", content = _reply },
                            finish_reason = "stop",
                        },
                    },
                });

            var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
            var head = System.Text.Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");

            await stream.WriteAsync(head);
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
        }

        /// <summary>
        /// Answers a streamed completion as SSE: one frame per piece, released with a pause between them.
        /// <para>
        /// The pauses are the point — they are what makes "the reply is visible before the turn ends"
        /// observable rather than a claim about code that merely parses frames quickly.
        /// </para>
        /// </summary>
        private async Task AnswerStreamAsync(NetworkStream stream)
        {
            var head = System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);

            // The provider's own reply, in pieces: the concatenation is what the buffered path would have
            // returned, so the checks that assert the reply text are asserting the same answer.
            const int Chunk = 2;

            for (var i = 0; i < _reply.Length; i += Chunk)
            {
                var piece = _reply.Substring(i, Math.Min(Chunk, _reply.Length - i));
                var frame = System.Text.Json.JsonSerializer.Serialize(new
                {
                    choices = new[] { new { delta = new { content = piece } } },
                });

                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"data: {frame}\n\n")).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);

                // Long enough for a poll to land between two pieces, short enough not to slow the suite down.
                await Task.Delay(60).ConfigureAwait(false);
            }

            var finish = System.Text.Json.JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { }, finish_reason = "stop" } },
            });

            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"data: {finish}\n\n")).ConfigureAwait(false);
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("data: [DONE]\n\n")).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();

            try
            {
                _serving.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The listener was stopped mid-accept; nothing to report.
            }

            _stop.Dispose();
        }
    }
    /// <summary>
    /// A model that observes and then acts on what it saw: the entity id of a real window is machine-specific,
    /// so a fixed script cannot name one. It picks a window that is *not* in front, which makes the keystroke a
    /// refusal the guards produce — the test never types into the user's desktop.
    /// </summary>
    private sealed class ObserveThenActLlm(string targetTitle) : ILlmClient
    {
        private readonly List<LlmRequest> _requests = new();

        /// <summary>A copy of the recorded requests, taken under the lock.</summary>
        public IReadOnlyList<LlmRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToList();
                }
            }
        }

        /// <summary>
        /// The ReAct shape, driven by the conversation: look, act on what was seen, then answer. The target is
        /// the background window this run created, so the keystroke is refused by the guards.
        /// </summary>
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            List<LlmRequest> seen;
            lock (_requests)
            {
                _requests.Add(request);
                seen = _requests.ToList();
            }

            var toolResults = request.Messages.Count(message => message.Role == "tool");

            return Task.FromResult(toolResults switch
            {
                0 => new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c1", "desktop_observe", "{}") },
                    "tool_calls"),
                1 => new LlmResponse(
                    null,
                    new[]
                    {
                        new LlmToolCall(
                            "c2",
                            "desktop_act",
                            $"{{ \"entityId\": \"{PickTargetOf(seen, targetTitle)}\", \"kind\": \"KeyPress\", \"virtualKey\": 67 }}"),
                    },
                    "tool_calls"),
                2 => new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c3", "desktop_screenshot", $"{{ \"entityId\": \"{PickTargetOf(seen, targetTitle)}\" }}") },
                    "tool_calls"),
                // What models actually do after looking: click the coordinates, with no window id. A pointer move
                // rather than a click, so the check never presses anything on the user's desktop.
                3 => new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c4", "desktop_act", $"{{ \"kind\": \"MovePointer\", \"x\": {PickCentreOf(seen, targetTitle).X}, \"y\": {PickCentreOf(seen, targetTitle).Y} }}") },
                    "tool_calls"),
                // A point no window covers: the refusal has to say which windows exist, or the model can only
                // repeat the same call — which is exactly what it did, until the turn hit the step limit.
                4 => new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c5", "desktop_act", "{ \"kind\": \"MovePointer\", \"x\": -100000, \"y\": -100000 }") },
                    "tool_calls"),
                _ => new LlmResponse("好吧。", Array.Empty<LlmToolCall>(), "stop"),
            });
        }

        public Task<LlmResponse> StreamAsync(LlmRequest request, Action<string> onText, CancellationToken ct = default) =>
            CompleteAsync(request, ct);
    }
    /// <summary>
    /// A model whose script is: act, act again on the same window, then try to leave the GUI. The first act is
    /// approved, and the <see cref="TaskLeaseStore"/> decides whether the second runs without approval. The
    /// keystrokes target the background window this run owns. The third act is <c>desktop_exec</c>, which a
    /// grant does not cover.
    /// </summary>
    private sealed class LeaseThenActLlm(string targetTitle) : ILlmClient
    {
        private readonly List<LlmRequest> _requests = new();

        /// <summary>A copy of the recorded requests, taken under the lock.</summary>
        public IReadOnlyList<LlmRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToList();
                }
            }
        }

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            List<LlmRequest> seen;
            lock (_requests)
            {
                _requests.Add(request);
                seen = _requests.ToList();
            }

            var toolResults = request.Messages.Count(message => message.Role == "tool");

            return Task.FromResult(toolResults switch
            {
                0 => new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c1", "desktop_observe", "{}") },
                    "tool_calls"),
                1 => new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c2", "desktop_act", $"{{ \"entityId\": \"{PickTargetOf(seen, targetTitle)}\", \"kind\": \"KeyPress\", \"virtualKey\": 67 }}") },
                    "tool_calls"),
                // ■ The lease probe: same window, same kind. Approved once above, so this runs without
                // approval — that is exactly what the check reads back from the server state.
                2 => new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c3", "desktop_act", $"{{ \"entityId\": \"{PickTargetOf(seen, targetTitle)}\", \"kind\": \"KeyPress\", \"virtualKey\": 67 }}") },
                    "tool_calls"),
                // What a grant must never cover: a command that leaves the GUI asks every time.
                3 => new LlmResponse(
                    null,
                    new[] { new LlmToolCall("c4", "desktop_exec", "{ \"command\": \"echo hi\" }") },
                    "tool_calls"),
                _ => new LlmResponse("好吧。", Array.Empty<LlmToolCall>(), "stop"),
            });
        }

        public Task<LlmResponse> StreamAsync(LlmRequest request, Action<string> onText, CancellationToken ct = default) =>
            CompleteAsync(request, ct);
    }
    /// <summary>Minimal reflex behavior for the smoke run.</summary>
    private sealed class Behavior(
        string id,
        Func<ReflexApi, CancellationToken, Task> run,
        Func<ReflexContextState, double> score) : IReflexBehavior
    {
        public string Id { get; } = id;

        public IReadOnlyList<string> Modes { get; } = Array.Empty<string>();

        public double CooldownMs => 0;

        public bool When(ReflexContextState context, ReflexApi api) => true;

        public double Score(ReflexContextState context, ReflexApi api) => score(context);

        public Task RunAsync(ReflexApi api, CancellationToken ct) => run(api, ct);
    }
    /// <summary>Clock the smoke can move forward to exercise lease expiry.</summary>
    internal sealed class OffsetClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
    /// <summary>A capture that pre-reports one positioned frame for any window, so the render of an act
    /// answer is drift-free to test without engaging the user's screen.</summary>
    private sealed class PositionEchoCapture(PixelRect at) : IScreenCapture
    {
        public Task<CaptureResult> CaptureWindowAsync(nint handle, CancellationToken ct = default) => Task.FromResult(
            new CaptureResult
            {
                Ok = true,
                Width = at.Width,
                Height = at.Height,
                Bgra = new byte[at.Width * at.Height * 4],
                Method = "smoke.stub",
                ScreenRect = at,
            });

        public Task<CaptureResult> CaptureScreenRegionAsync(int x, int y, int width, int height, CancellationToken ct = default) =>
            CaptureWindowAsync(0, ct);
    }
}
