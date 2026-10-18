using System.Text.Json;
using Partner.Api.Settings;
using Partner.Core.Agent;
using Partner.Core.Agent.Delegation;
using Partner.Core.Agent.Hooks;
using Partner.Core.Agent.Tools;
using Partner.Core.Contracts;
using Partner.Core.Queue;
using Partner.Core.Sessions;
using Partner.Desktop.Windows;

namespace Partner.Api.Agents;

public sealed partial class SubAgentHost
{
    private readonly object _journalGate = new();

    private string JournalPath => Path.Combine(_workspace, "agents", "jobs.jsonl");

    private async Task RunAsync(AgentJob job, SubAgentSettings agent, RuntimeSettings settings, ModelBinding binding)
    {
        JobOutcome outcome;
        JsonlSessionStore? session = null;

        try
        {
            Journal("started", job, agent.Name);
            (session, _) = JsonlSessionStore.Open(Path.Combine(_workspace, "agents", agent.Id, job.Id), _clock);

            var report = new TaskReportTool();
            var tools = new InMemoryToolRegistry();
            tools.Register(report);
            if (agent.Scenario == AgentScenarios.Desktop)
            {
                DesktopToolSet.Register(tools, _context, settings, binding.SupportsImages);
            }
            else
            {
                FileToolSet.Register(tools, settings, _workspace);
            }

            // The prompt is read once, here: a job runs under the wording it started with, and an edit made
            // while it works is for the next one.
            var options = new AgentTurnOptions
            {
                ContextWindowTokens = binding.ContextTokens,
                MaxTokens = binding.MaxTokens,
                SessionId = $"agent:{job.Id}",
                BaseSystemPrompt = AgentRouting.SubAgentPrompt(agent),
            };
            var assembler = new SessionWindowContextAssembler(
                session,
                options.ContextWindowTokens,
                options.MaxTokens,
                binding.Tokens,
                options.BaseSystemPrompt,
                _context.Media);

            var origin = $"子 agent「{agent.Name}」：{Short(job.Goal)}";
            var runner = new AgentTurnRunner(
                session,
                binding.Llm,
                tools,
                new DesktopGovernance(_context),
                new LeaseGrantingApprovalHandler(new OriginApprovalHandler(_context.Approvals, origin), _context.TaskLeases),
                new HookRegistry(),
                assembler,
                options,
                stream: null,
                failures: _failures,
                clock: _clock);

            var engine = new JobEngine(runner, session, report, ExtraTerminal, _clock);
            outcome = await engine
                .RunAsync(
                    new JobSpec(job.Id, job.Goal, job.Context, agent.MaxRounds, _deadline),
                    job.Cancel.Token,
                    job.StartRound,
                    job.AddRound)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            outcome = new JobOutcome(
                false, JobCategories.InternalError, "子 agent 没能启动", null, error.Message, 0, agent.MaxRounds, [], []);
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            binding.Owned?.Dispose();
        }

        await FinishAsync(job, agent.Name, outcome).ConfigureAwait(false);
    }

    /// <summary>
    /// Records the end of a job and queues its report for the main agent. The journal line is written first: a
    /// crash between the two leaves a finished job whose report is simply not there, which is recoverable; the
    /// other order leaves a report for a job the journal still thinks is running.
    /// </summary>
    private async Task FinishAsync(AgentJob job, string agentName, JobOutcome outcome)
    {
        job.Finish(outcome, _clock.GetUtcNow());
        Journal("finished", job, agentName, outcome.Category ?? "completed");
        await QueueReportAsync(job.Id, agentName, job.Goal, outcome).ConfigureAwait(false);
        TrimFinished();
    }

    private async Task QueueReportAsync(string jobId, string agentName, string goal, JobOutcome outcome)
    {
        try
        {
            await _context.Queue.EnqueueAsync(new InputItem
            {
                ItemId = $"agent-{jobId}",
                Source = InputSource.Agent,
                Payload = outcome.ToMessage(jobId, agentName, goal),
                IdempotencyKey = $"agent-job:{jobId}",
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The queue is gone because the process is shutting down; the journal still says what happened.
        }
    }

    /// <summary>
    /// Writes a report for every job the journal started and never finished — the process died under it. The
    /// idempotency key is the job's, so a report that did make it into the queue is not duplicated.
    /// </summary>
    public async Task<int> RecoverAsync()
    {
        if (!File.Exists(JournalPath))
        {
            return 0;
        }

        var open = new Dictionary<string, (string AgentName, string Goal)>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(JournalPath))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var id = root.GetProperty("id").GetString()!;
                if (root.GetProperty("event").GetString() == "started")
                {
                    open[id] = (root.GetProperty("agentName").GetString() ?? string.Empty, root.GetProperty("goal").GetString() ?? string.Empty);
                }
                else
                {
                    open.Remove(id);
                }
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException)
            {
                // A torn or foreign line says nothing about a job; skip it.
            }
        }

        foreach (var (id, (agentName, goal)) in open)
        {
            var outcome = new JobOutcome(
                false,
                JobCategories.Interrupted,
                "应用在任务进行中退出或重启，任务没有做完",
                null,
                "任务被中断：应用在任务进行中退出或重启",
                0,
                0,
                [],
                []);
            AppendJournal(new { @event = "finished", id, agentName, goal, at = _clock.GetUtcNow(), status = JobCategories.Interrupted });
            await QueueReportAsync(id, agentName, goal, outcome).ConfigureAwait(false);
        }

        return open.Count;
    }

    private void Journal(string kind, AgentJob job, string agentName, string? status = null) =>
        AppendJournal(new
        {
            @event = kind,
            id = job.Id,
            agentName,
            scenario = job.Scenario,
            goal = job.Goal,
            at = _clock.GetUtcNow(),
            status,
        });

    private void AppendJournal(object record)
    {
        try
        {
            lock (_journalGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
                File.AppendAllText(JournalPath, JsonSerializer.Serialize(record) + "\n");
            }
        }
        catch (IOException)
        {
            // The journal is a safety net for a crash, not the record of the job; failing to write it must not fail the job.
        }
    }

    /// <summary>Failures only the desktop side knows how to recognise as "no round can fix this".</summary>
    private static string? ExtraTerminal(string body) =>
        body.Contains(IntegrityGate.ElevatedMarker, StringComparison.Ordinal) ? JobCategories.GovernanceDenied : null;

    private static string Short(string text) => text.Length <= 40 ? text : text[..40] + "…";
}
