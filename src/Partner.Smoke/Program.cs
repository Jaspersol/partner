namespace Partner.Smoke;

/// <summary>
/// Smoke harness: runs the real end-to-end path (three input kinds → one FIFO queue →
/// single consumer → one session log → projection), including a restart, and asserts the
/// required invariants. Exits non-zero on the first failed check.
/// <para>
/// The capability manifest is generated from these runs: nothing here reads configuration to decide
/// whether something works.
/// </para>
/// </summary>
internal static partial class Program
{
    private static readonly List<(string Name, bool Passed, string? Detail, bool Skipped)> Results = new();
    private static int _failures;

    private static async Task<int> Main(string[] args)
    {
        var options = SmokeOptions.Parse(args);
        var root = Path.Combine(Path.GetTempPath(), "partner-smoke", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        Console.WriteLine($"workspace: {root}");

        try
        {
            await RunAsync(root);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"UNHANDLED: {ex}");
            _failures++;
        }

        var manifest = BuildManifest();
        WriteJson(options.ManifestPath, manifest);
        Console.WriteLine($"manifest: {options.ManifestPath}");

        var gateOk = true;

        if (options.UpdateBaselinePath is not null)
        {
            WriteJson(options.UpdateBaselinePath, manifest);
            Console.WriteLine($"baseline updated: {options.UpdateBaselinePath}");
        }

        if (options.VerifyBaselinePath is not null)
        {
            gateOk = VerifyBaseline(options.VerifyBaselinePath, manifest);
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "SMOKE OK" : $"SMOKE FAILED ({_failures} check(s))");

        // A failed run keeps its workspace so the state can be inspected (the path is printed above); a
        // passing run must not leak ~25 MB per invocation into the temp directory.
        if (_failures == 0)
        {
            TryDeleteWorkspace(root);
        }
        else
        {
            Console.WriteLine($"workspace kept for inspection: {root}");
        }

        if (!gateOk)
        {
            return 1;
        }

        return _failures == 0 ? 0 : 1;
    }

    private static void TryDeleteWorkspace(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException ex)
        {
            // Best effort: a leftover workspace is not worth failing an otherwise green run over.
            Console.WriteLine($"workspace left behind ({ex.GetType().Name}): {root}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"workspace left behind ({ex.GetType().Name}): {root}");
        }
    }

    /// <summary>Fails the build when a capability disappears or drops a level.</summary>
    private static bool VerifyBaseline(string path, CapabilityManifest current)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"GATE FAILED: baseline '{path}' not found — create it with --update-baseline <path>");
            return false;
        }

        CapabilityManifest? baseline;
        try
        {
            baseline = JsonSerializer.Deserialize<CapabilityManifest>(File.ReadAllText(path), CapabilityManifest.Json);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"GATE FAILED: baseline '{path}' is not readable: {ex.Message}");
            return false;
        }

        if (baseline is null)
        {
            Console.Error.WriteLine($"GATE FAILED: baseline '{path}' is empty");
            return false;
        }

        var report = CapabilityManifest.Compare(baseline, current);

        foreach (var line in report.Improvements)
        {
            Console.WriteLine($"  GATE  {line}");
        }

        foreach (var line in report.Added)
        {
            Console.WriteLine($"  GATE  {line} (update the baseline to lock it in)");
        }

        // Not fatal, and not hidden: a capability nobody looked at is named on every run, so the green build
        // cannot be read as "everything was proven" when part of it was opted out.
        foreach (var line in report.NotVerified)
        {
            Console.WriteLine($"  GATE  {line}");
        }

        if (report.Ok)
        {
            var suffix = report.NotVerified.Count > 0
                ? $", {report.NotVerified.Count} not verified this run"
                : string.Empty;
            Console.WriteLine($"  GATE  PASS  no capability regressed ({current.Capabilities.Count} capabilities{suffix})");
            return true;
        }

        foreach (var line in report.Regressions)
        {
            Console.Error.WriteLine($"  GATE  FAIL  {line}");
        }

        return false;
    }

    private sealed record SmokeOptions(
        string ManifestPath,
        string? VerifyBaselinePath,
        string? UpdateBaselinePath)
    {
        public static SmokeOptions Parse(string[] args)
        {
            var manifest = Path.Combine(Directory.GetCurrentDirectory(), "capabilities", "manifest.json");
            string? verify = null;
            string? update = null;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--manifest" when i + 1 < args.Length:
                        manifest = args[++i];
                        break;
                    case "--verify-baseline" when i + 1 < args.Length:
                        verify = args[++i];
                        break;
                    case "--update-baseline" when i + 1 < args.Length:
                        update = args[++i];
                        break;
                    default:
                        // Backwards-compatible positional form: a single path is the manifest path.
                        if (!args[i].StartsWith("--", StringComparison.Ordinal))
                        {
                            manifest = args[i];
                        }

                        break;
                }
            }

            return new SmokeOptions(manifest, verify, update);
        }
    }

    private static async Task RunAsync(string root)
    {
        var clock = TimeProvider.System;

        WarnIfPartnerAppIsRunning();
        await SessionQueueChecksAsync(root, clock);
        await MemoryChecksAsync(root, clock);
        await ToolChecksAsync(root, clock);
        await DesktopChecksAsync(root);
        await VoiceChecksAsync(root, clock);
        await GameChecksAsync();
        await ApiChecksAsync(root, clock);
        await MinimizedWindowChecksAsync(root);
    }




    /// <summary>
    /// The desktop sub-agent's conversation(s) as one <c>/api/session</c>-shaped document, oldest job first. The main
    /// session holds only the delegation and the report; the tool calls and results the desktop checks look at live
    /// in the jobs' own sessions.
    /// </summary>
    private static async Task<JsonElement> JobSessionAsync(HttpClient http)
    {
        var jobs = (await http.GetFromJsonAsync<JsonElement>("/api/agent-jobs")).GetProperty("jobs").EnumerateArray()
            .Select(job => job.GetProperty("id").GetString()!)
            .Reverse()
            .ToList();

        var items = new List<string>();
        foreach (var id in jobs)
        {
            var session = await http.GetFromJsonAsync<JsonElement>($"/api/agent-jobs/{id}/session");
            // The report call is how the job ends, not something the desktop did; the checks look at the desktop's.
            items.AddRange(session.GetProperty("items").EnumerateArray()
                .Where(item => !(item.TryGetProperty("meta", out var meta)
                    && meta.ValueKind == JsonValueKind.Object
                    && meta.TryGetProperty("name", out var name)
                    && name.GetString() == "task_report"))
                .Select(item => item.GetRawText()));
        }

        return JsonDocument.Parse($"{{\"items\":[{string.Join(",", items)}]}}").RootElement.Clone();
    }

    /// <summary>Waits until no sub-agent job is running, so the next request is not refused as busy.</summary>
    private static async Task WaitJobsIdleAsync(HttpClient http)
    {
        for (var attempt = 0; attempt < 1200; attempt++)
        {
            var jobs = (await http.GetFromJsonAsync<JsonElement>("/api/agent-jobs")).GetProperty("jobs").EnumerateArray();
            if (!jobs.Any(job => job.GetProperty("status").GetString() == "running"))
            {
                return;
            }

            await Task.Delay(25);
        }
    }


    /// <summary>
    /// The entity id a scripted model acts on, read from its observations. With <paramref name="titleMarker"/>
    /// it is the window whose title contains the marker, or an id that cannot resolve when there is none;
    /// without it, a background window if there is one.
    /// </summary>
    private static string PickTargetOf(IEnumerable<LlmRequest> requests, string? titleMarker = null)
    {
        var observed = requests
            .SelectMany(request => request.Messages)
            .Where(message => message.Role == "tool" && message.Content is not null)
            .Select(message => message.Content!)
            .LastOrDefault(content => content.Contains("\"entities\"", StringComparison.Ordinal));

        if (observed is null)
        {
            return "1";
        }

        // The observation carries a trailing note (the blind-Electron hint) after the JSON; the pick
        // reads only the JSON document, which ends at the last '}' before the note.
        var jsonEnd = observed.LastIndexOf('}');
        using var document = JsonDocument.Parse(observed[..(jsonEnd + 1)]);
        var entities = document.RootElement.GetProperty("entities").EnumerateArray()
            // A minimized window is a 160×28 stub at -32000: nothing can be clicked or seen in it, so a
            // model that reads the observation picks a real one.
            .Where(entity => !(entity.TryGetProperty("minimized", out var small) && small.GetBoolean()))
            .ToList();

        if (entities.Count == 0)
        {
            return "1";
        }

        if (titleMarker is not null)
        {
            var owned = entities.FirstOrDefault(entity =>
                entity.TryGetProperty("title", out var title)
                && (title.GetString() ?? string.Empty).Contains(titleMarker, StringComparison.Ordinal));

            // Never fall back to another window.
            return owned.ValueKind == JsonValueKind.Undefined
                ? "1"
                : owned.GetProperty("entityId").GetString() ?? "1";
        }

        var background = entities.FirstOrDefault(entity =>
            !entity.TryGetProperty("foreground", out var flag) || !flag.GetBoolean());

        return (background.ValueKind == JsonValueKind.Undefined ? entities[0] : background)
            .GetProperty("entityId").GetString() ?? "1";
    }

    /// <summary>The centre of the picked window, in screen coordinates, as desktop_observe reports them.</summary>
    private static (int X, int Y) PickCentreOf(IEnumerable<LlmRequest> requests, string? titleMarker = null)
    {
        var target = PickTargetOf(requests, titleMarker);
        var observed = requests
            .SelectMany(request => request.Messages)
            .Where(message => message.Role == "tool" && message.Content is not null)
            .Select(message => message.Content!)
            .LastOrDefault(content => content.Contains("\"entities\"", StringComparison.Ordinal));

        if (observed is null)
        {
            return (1, 1);
        }

        using var document = JsonDocument.Parse(observed[..(observed.LastIndexOf('}') + 1)]);
        var entity = document.RootElement.GetProperty("entities").EnumerateArray()
            .FirstOrDefault(candidate => candidate.GetProperty("entityId").GetString() == target);
        if (entity.ValueKind == JsonValueKind.Undefined)
        {
            return (1, 1);
        }

        var rect = entity.GetProperty("rect");
        return (
            rect.GetProperty("x").GetInt32() + (rect.GetProperty("width").GetInt32() / 2),
            rect.GetProperty("y").GetInt32() + (rect.GetProperty("height").GetInt32() / 2));
    }


    /// <summary>Waits until a host answers, so a probe is not a guess at a duration.</summary>
    private static async Task WaitForHostAsync(HttpClient http)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            try
            {
                using var probe = await http.GetAsync("/api/health");
                if (probe.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException("the host never started listening");
    }

    /// <summary>
    /// Waits for a queue item to leave the view.
    /// <para>
    /// The view is served from what is still in play rather than from the log, so an item settles by
    /// disappearing from it — there is no <c>Done</c> row to poll for. What the turn produced is asserted from
    /// the session afterwards, which is where a settled item's record lives.
    /// </para>
    /// </summary>
    private static async Task<bool> WaitForQueueItemToSettleAsync(HttpClient http, string? itemId)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            return false;
        }

        for (var attempt = 0; attempt < 600; attempt++)
        {
            var polled = await http.GetFromJsonAsync<JsonElement>("/api/queue");
            var listed = polled.GetProperty("items").EnumerateArray()
                .Any(item => item.GetProperty("itemId").GetString() == itemId);
            if (!listed)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    /// <summary>Asks the OS for a free port (the API host must not collide with anything).</summary>
    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Locates the built synthetic game server, walking up to the solution root.</summary>
    private static string? FindGameTestServer()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Partner.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            return null;
        }

        var bin = Path.Combine(directory.FullName, "tests", "Partner.GameTestServer", "bin");
        return Directory.Exists(bin)
            ? Directory.GetFiles(bin, "partner-game-testserver.dll", SearchOption.AllDirectories)
                .Where(m => m.Contains("net10.0", StringComparison.Ordinal))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
    }



    /// <summary>Locates the built MCP test-double server, walking up to the solution root.</summary>
    private static string? FindMcpTestServer()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Partner.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            return null;
        }

        var pattern = Path.Combine("tests", "Partner.McpTestServer", "bin", "*", "net10.0", "Partner.McpTestServer.dll");
        var root = directory.FullName;
        var matches = Directory.Exists(Path.Combine(root, "tests"))
            ? Directory.GetFiles(Path.Combine(root, "tests", "Partner.McpTestServer", "bin"), "Partner.McpTestServer.dll",
                SearchOption.AllDirectories)
            : Array.Empty<string>();

        return matches
            .Where(m => m.Contains("net10.0", StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>Writes a skill directory into the smoke workspace (null name/description = no front matter).</summary>
    private static void WriteSkill(string workspace, string skillId, string? name, string? description, string body)
    {
        var dir = Path.Combine(workspace, "skills", skillId);
        Directory.CreateDirectory(dir);

        var text = name is null || description is null
            ? body
            : $"---\nname: {name}\ndescription: {description}\n---\n{body}";

        File.WriteAllText(Path.Combine(dir, "SKILL.md"), text);
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        Results.Add((what, ok, detail, Skipped: false));

        if (ok)
        {
            Console.WriteLine($"  PASS  {what}");
            return;
        }

        _failures++;
        Console.WriteLine($"  FAIL  {what}{(detail is null ? "" : $"  → {detail}")}");
    }

    /// <summary>
    /// The desktop checks read the real desktop (pit 11/12): one extra window from a running Partner app
    /// adds seconds to every UIA walk and the timing-sensitive scenes degrade wholesale, reads looking like
    /// regressions that are not. Warned at the top, where the operator still decides to stop rather than
    /// after the desktop section spent a minute failing.
    /// </summary>
    private static void WarnIfPartnerAppIsRunning()
    {
        var suspects = System.Diagnostics.Process.GetProcessesByName("partner-api")
            .Concat(System.Diagnostics.Process.GetProcessesByName("partner-shell"))
            .Concat(System.Diagnostics.Process.GetProcessesByName("Partner"))
            .ToList();

        if (suspects.Count == 0)
        {
            return;
        }

        Console.WriteLine(
            $"  WARN  检测到疑似本应用进程在跑（{string.Join("、", suspects.Select(p => $"{p.ProcessName} (pid {p.Id})"))}）："
            + "桌面结果可能整片失真——建议先停应用再跑 smoke");
    }


    /// <summary>
    /// A check that did not run, and why — reported as neither pass nor fail.
    /// <para>
    /// "The capability regressed" and "this machine's desktop was busy" are different facts: a check that
    /// manipulates the reader's real windows fails for reasons that have nothing to do with the code (a
    /// fullscreen game takes the focus back the moment you minimize it), and a build that goes red on such a
    /// machine is a build people learn to ignore. A skipped check is not a passed one — the capability it
    /// belongs to reports as *not verified this round*, which is a state the manifest can say out loud.
    /// </para>
    /// </summary>
    private static void Skip(string what, string reason)
    {
        Results.Add((what, true, reason, Skipped: true));
        Console.WriteLine($"  SKIP  {what}  → {reason}");
    }

    /// <summary>
    /// Whether the live desktop checks may run: they move and restore the reader's own windows, so they are
    /// opt-in, exactly like <c>DesktopLiveInteractionTests</c> (which moves the pointer, so it is gated on the
    /// same variable). Everything that only reads the desktop still runs by default.
    /// </summary>
    private static bool LiveDesktop => Environment.GetEnvironmentVariable("PARTNER_DESKTOP_LIVE") == "1";


    private static CapabilityManifest BuildManifest()
    {
        // A check name may run more than once (e.g. per loop iteration): every observation must pass.
        var byName = Results
            .GroupBy(r => r.Name)
            .ToDictionary(g => g.Key, g => g.ToList());

        var entries = Capabilities.Select(c =>
        {
            var byEvidence = new Dictionary<string, List<(string Name, bool Passed, string? Detail, bool Skipped)>>(StringComparer.Ordinal);
            foreach (var name in c.Checks)
            {
                byEvidence[name] = byName.TryGetValue(name, out var runs) ? runs : new List<(string, bool, string?, bool)>();
            }

            // Skipped is neither ran nor missing: the check was named, declared, and deliberately not
            // executed. Counting it as missing would downgrade the capability, and counting it as passed would
            // claim evidence that does not exist.
            var missing = c.Checks.Where(n => !byName.ContainsKey(n)).ToArray();
            var observed = byEvidence.Values.SelectMany(r => r).Where(o => !o.Skipped).ToList();
            var skipped = byEvidence.Values.SelectMany(r => r).Count(o => o.Skipped);
            var failed = observed.Count(o => !o.Passed);

            // A capability reaches product-supported only if every check it names actually ran; a skipped
            // check leaves it covered — not supported, and not silently promoted either.
            var level = observed.Count == 0
                ? CapabilityLevel.NotSupported
                : missing.Length == 0 && failed == 0 && skipped == 0
                    ? CapabilityLevel.ProductSupported
                    : failed == observed.Count
                        ? CapabilityLevel.NotSupported
                        : CapabilityLevel.Covered;

            return new CapabilityEntry
            {
                Id = c.Id,
                Owner = c.Owner,
                Level = level,
                Evidence = c.Checks.Select(n => new CapabilityCheckEvidence(
                    n,
                    byEvidence[n].Count > 0 && byEvidence[n].Any(o => !o.Skipped),
                    byEvidence[n].Count == 0 || byEvidence[n].All(o => o.Passed),
                    byEvidence[n].Count > 0 && byEvidence[n].All(o => o.Skipped))).ToList(),
            };
        }).ToList();

        return new CapabilityManifest
        {
            ChecksFailed = _failures,
            Capabilities = entries,
        };
    }

    private static void WriteJson<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(value, CapabilityManifest.Json) + Environment.NewLine);
    }
}
