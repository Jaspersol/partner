namespace Partner.Smoke;

internal static partial class Program
{
    private static async Task ToolChecksAsync(string root, TimeProvider clock)
    {
        var policyPath = Path.Combine(root, "tools.policy.json");
        File.WriteAllText(policyPath, """
            {
              "version": 1,
              "tools": {
                "clock":      { "decision": "allow" },
                "rm.rf":      { "decision": "deny" },
                "file.write": { "decision": "allow", "rules": { "allowedPathPrefixes": ["${workspace}"] } }
              }
            }
            """);

        var policy = ToolPolicy.Load(policyPath, root);
        var governance = new PolicyToolGovernance(policy);

        Check("governance: policy allows a listed tool",
            governance.Decide("clock", "{}").Decision == ToolDecision.Allow);

        Check("governance: explicit deny is refused",
            governance.Decide("rm.rf", "{}").Decision == ToolDecision.Deny);

        Check("governance: unlisted tool is refused (fail-closed)",
            governance.Decide("never-registered", "{}").Decision == ToolDecision.Deny
            && governance.Decide("never-registered", "{}").Reason.Contains("fail-closed", StringComparison.Ordinal));

        // Build the arguments as real JSON: Windows paths must be escaped, not hand-concatenated.
        var insidePath = Path.Combine(root, "notes.txt");
        var outsidePath = Path.Combine(Path.GetTempPath(), "outside.txt");
        var insideArgs = JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = insidePath });
        var outsideArgs = JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = outsidePath });

        Check("governance: target parameter rules reject an out-of-scope target",
            governance.Decide("file.write", insideArgs).Decision == ToolDecision.Allow
            && governance.Decide("file.write", outsideArgs).Decision == ToolDecision.Deny,
            $"inside={governance.Decide("file.write", insideArgs).Decision} "
            + $"outside={governance.Decide("file.write", outsideArgs).Decision} ({outsidePath})");

        // Enforced inside a real turn: the model asks for a denied tool, the turn still completes.
        var (store5, _) = JsonlSessionStore.Open(root, clock);
        await using (store5)
        {
            var queue6 = new JsonlInputQueue(root, capacity: 8, clock: clock);
            await queue6.EnqueueAsync(new InputItem
            {
                ItemId = Guid.NewGuid().ToString("n"),
                Source = InputSource.Text,
                Payload = "delete everything",
                IdempotencyKey = "text:client1:msg-4",
            });
            var claim = await queue6.TakeAsync();

            var tools = new InMemoryToolRegistry();
            tools.Register(new StubTool("rm.rf", "deleted the world"));
            var llm = new ScriptedLlmClient(new[]
            {
                new LlmResponse(null, new[] { new LlmToolCall("c1", "rm.rf", "{}") }, "tool_calls"),
                new LlmResponse("i did not do that", Array.Empty<LlmToolCall>(), "stop"),
            });

            var runner = new AgentTurnRunner(
                store5, llm, tools, governance, new DenyAllApprovalHandler(), new HookRegistry());

            var governed = await runner.RunAsync(claim!);

            // Tool call/result are structured events, not rendered content: read the raw log.
            SessionEvent? toolResult = null;
            await foreach (var e in store5.ReadAllAsync())
            {
                if (e.Kind == SessionEventKind.ToolResult)
                {
                    toolResult = e;
                }
            }

            Check("governance: denial is enforced inside a real turn",
                governed.Reason == TurnEndReason.Completed
                && toolResult is not null
                && toolResult.Body.StartsWith("denied:", StringComparison.Ordinal),
                toolResult?.Body ?? "(no tool result)");

            await queue6.CompleteAsync(claim!.ItemId, InputItemStatus.Done);
            await queue6.DisposeAsync();
        }

        var mcpDll = FindMcpTestServer();
        if (mcpDll is null)
        {
            Check("mcp: test-double server is built", false,
                "tests/Partner.McpTestServer/bin/*/net10.0/Partner.McpTestServer.dll not found — run dotnet build first");
        }
        else
        {
            var mcp = new StdioMcpClient(new McpServerConfig
            {
                Name = "smoke-mcp",
                Command = "dotnet",
                Arguments = new[] { mcpDll },
                CallTimeout = TimeSpan.FromSeconds(10),
            });

            try
            {
                await mcp.StartAsync();
                var descriptors = await mcp.ListToolsAsync();
                Check("mcp: server starts and lists tools",
                    mcp.IsRunning && descriptors.Count == 3 && descriptors.Any(d => d.Name == "echo"),
                    string.Join(",", descriptors.Select(d => d.Name)));

                var echo = await mcp.CallToolAsync("echo", """{"text":"round trip"}""");
                Check("mcp: tool call round-trips through the client",
                    echo.Ok && echo.Content == "round trip",
                    $"ok={echo.Ok} content={echo.Content}");

                // The MCP tool goes through the same registry + governance + turn path as any local tool.
                var (store6, _) = JsonlSessionStore.Open(root, clock);
                await using (store6)
                {
                    var queue7 = new JsonlInputQueue(root, capacity: 8, clock: clock);
                    await queue7.EnqueueAsync(new InputItem
                    {
                        ItemId = Guid.NewGuid().ToString("n"),
                        Source = InputSource.Text,
                        Payload = "echo something",
                        IdempotencyKey = "text:client1:msg-5",
                    });
                    var claim = await queue7.TakeAsync();

                    var tools = new InMemoryToolRegistry();
                    foreach (var descriptor in descriptors)
                    {
                        tools.Register(new McpTool(mcp, descriptor));
                    }

                    var llm = new ScriptedLlmClient(new[]
                    {
                        new LlmResponse(null, new[] { new LlmToolCall("c1", "echo", """{"text":"from the model"}""") }, "tool_calls"),
                        new LlmResponse("done", Array.Empty<LlmToolCall>(), "stop"),
                    });

                    var mcpPolicy = new PolicyToolGovernance(ToolPolicy.Parse("""
                        { "tools": { "echo": { "decision": "allow" } } }
                        """));

                    var runner = new AgentTurnRunner(
                        store6, llm, tools, mcpPolicy, new DenyAllApprovalHandler(), new HookRegistry());

                    var mcpTurn = await runner.RunAsync(claim!);

                    SessionEvent? echoResult = null;
                    await foreach (var e in store6.ReadAllAsync())
                    {
                        if (e.Kind == SessionEventKind.ToolResult)
                        {
                            echoResult = e;
                        }
                    }

                    Check("mcp: mcp tool runs inside a governed turn",
                        mcpTurn.Reason == TurnEndReason.Completed
                        && echoResult is not null
                        && echoResult.Body == "from the model"
                        && echoResult.Meta!["ok"] == "true",
                        echoResult?.Body ?? "(no tool result)");

                    await queue7.CompleteAsync(claim!.ItemId, InputItemStatus.Done);
                    await queue7.DisposeAsync();
                }
            }
            finally
            {
                await mcp.StopAsync();
                await mcp.DisposeAsync();
            }
        }

    }
}
