using Partner.Api.Settings;
using Partner.Core.Agent.Tools;

namespace Partner.Api.Agents;

/// <summary>The answer to "start a job": the job, or the reason there is none.</summary>
/// <param name="Job">The job that was started, or the one already running when <paramref name="Refusal"/> is a busy answer.</param>
/// <param name="Refusal">Why nothing was started; null when a job was.</param>
/// <param name="Busy">True when the refusal is "that agent is already working", so the caller can point at the running job.</param>
public sealed record DelegateResult(AgentJobView? Job, string? Refusal, bool Busy = false)
{
    public bool Started => Refusal is null;
}

/// <summary>
/// Runs sub-agent jobs in the background and hands each one's report back to the main agent through the input
/// queue.
/// <para>
/// A job is not a queue item. It has its own session, its own tool set and its own model, and the main agent's
/// turn ends the moment it is started; the single reader of the queue is never held. What comes back is a new
/// queue item (<c>InputSource.Agent</c>) with a stable idempotency key, so the at-least-once rules the queue
/// already has apply: a report is not delivered twice, and one lost to a crash is written on the next start.
/// </para>
/// <para>
/// One job per sub-agent at a time. For desktop control that is a hard requirement (one shared desktop state);
/// for the others it is a rule that keeps two jobs from reading the same files into two contexts.
/// </para>
/// </summary>
public sealed partial class SubAgentHost : IAsyncDisposable
{
    /// <summary>
    /// A ceiling on a whole job. Each round is bounded already, but five rounds of ten minutes is fifty minutes
    /// of someone else's mouse and keyboard.
    /// </summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromMinutes(15);

    /// <summary>How many finished jobs stay listed. The journal on disk keeps them all.</summary>
    private const int KeepFinished = 30;

    /// <summary>A goal that just failed is not delegated again word for word inside this window.</summary>
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(10);

    private readonly ApiContext _context;
    private readonly string _workspace;
    private readonly Func<SubAgentSettings, ModelBinding?> _models;
    private readonly IToolFailureLog _failures;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _deadline;
    private readonly object _gate = new();
    private readonly List<AgentJob> _jobs = [];

    /// <param name="models">Resolves the model a job should use, or null when there is none (no consumer is running).</param>
    public SubAgentHost(
        ApiContext context,
        string workspace,
        Func<SubAgentSettings, ModelBinding?> models,
        IToolFailureLog failures,
        TimeProvider? clock = null,
        TimeSpan? deadline = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _workspace = workspace;
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _failures = failures ?? throw new ArgumentNullException(nameof(failures));
        _clock = clock ?? TimeProvider.System;
        _deadline = deadline ?? DefaultDeadline;
    }

    /// <summary>Starts a job for a scenario. Returns at once; the work happens in the background.</summary>
    public DelegateResult Start(string scenario, string goal, string? extra)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            return new(null, "需要 goal：说清楚要子 agent 做什么");
        }

        var settings = _context.Settings.Load();
        var route = AgentRouting.Find(settings, scenario);
        if (route is null)
        {
            return new(null, $"场景 '{scenario}' 现在不可用：没有启用的子 agent，或对应的能力没有开启。这件事请自己处理或告诉用户。");
        }

        AgentJob job;
        lock (_gate)
        {
            var running = _jobs.FirstOrDefault(other => other.AgentId == route.Agent.Id && other.IsRunning);
            if (running is not null)
            {
                return new(running.View(), $"「{route.Agent.Name}」正在做任务 {running.Id}。等它汇报，或先用 agent_job_cancel 取消它。", Busy: true);
            }

            if (RecentFailureOf(scenario, goal) is { } failed)
            {
                return new(failed.View(), $"同样的目标刚刚失败过（任务 {failed.Id}：{failed.Outcome?.Category}）。"
                    + "先把失败原因告诉用户；有新信息或换了做法，再用改写后的目标委派。");
            }

            job = new AgentJob(
                $"job-{Guid.NewGuid():n}"[..12],
                route.Agent.Id,
                route.Agent.Name,
                scenario,
                goal.Trim(),
                route.Agent.MaxRounds,
                _clock.GetUtcNow())
            {
                Context = extra,
            };
            _jobs.Add(job);
        }

        var binding = _models(route.Agent);
        if (binding is null)
        {
            Forget(job);
            return new(null, "模型没有就绪（未配置或消费者未启动），子 agent 无法工作");
        }

        job.Done = Task.Run(() => RunAsync(job, route.Agent, settings, binding));
        return new(job.View(), null);
    }

    /// <summary>Jobs, newest first.</summary>
    public IReadOnlyList<AgentJobView> Jobs()
    {
        lock (_gate)
        {
            return _jobs.AsEnumerable().Reverse().Select(job => job.View()).ToList();
        }
    }

    public AgentJobView? Job(string id)
    {
        lock (_gate)
        {
            return _jobs.FirstOrDefault(job => job.Id == id)?.View();
        }
    }

    /// <summary>Asks a running job to stop. False when it is unknown or already over.</summary>
    public bool Cancel(string id)
    {
        AgentJob? job;
        lock (_gate)
        {
            job = _jobs.FirstOrDefault(other => other.Id == id);
        }

        if (job is null || !job.IsRunning)
        {
            return false;
        }

        job.Cancel.Cancel();
        return true;
    }

    /// <summary>
    /// A job's own session, read from its log — finished or still running (the log is shared-readable). Null
    /// when there is no such job. This is how a person (or the smoke) sees what a sub-agent actually did: none
    /// of it is in the main session.
    /// </summary>
    public async Task<IReadOnlyList<Partner.Core.Sessions.SessionEvent>?> ReadSessionAsync(string jobId, CancellationToken ct = default)
    {
        // The id becomes part of a path; only what a job id is made of is let through.
        if (string.IsNullOrWhiteSpace(jobId) || jobId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            return null;
        }

        var root = Path.Combine(_workspace, "agents");
        if (!Directory.Exists(root))
        {
            return null;
        }

        foreach (var agentDirectory in Directory.EnumerateDirectories(root))
        {
            var path = Path.Combine(agentDirectory, jobId, "session", "events.jsonl");
            if (!File.Exists(path))
            {
                continue;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var events = new List<Partner.Core.Sessions.SessionEvent>();
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (Partner.Core.Sessions.SessionJson.Deserialize(line) is { } parsed)
                {
                    events.Add(parsed);
                }
            }

            return events;
        }

        return null;
    }

    /// <summary>
    /// Stops every running job and waits for each report to be queued. Called before the consumer is rebuilt:
    /// a job holds the tool table it was started with, and the consumer it was started under.
    /// </summary>
    public async Task CancelAllAsync()
    {
        List<AgentJob> running;
        lock (_gate)
        {
            running = _jobs.Where(job => job.IsRunning).ToList();
        }

        foreach (var job in running)
        {
            job.Cancel.Cancel();
        }

        await Task.WhenAny(Task.WhenAll(running.Select(job => job.Done)), Task.Delay(TimeSpan.FromSeconds(10)))
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await CancelAllAsync().ConfigureAwait(false);

    private AgentJob? RecentFailureOf(string scenario, string goal)
    {
        var now = _clock.GetUtcNow();
        var wanted = Normalize(goal);

        return _jobs.LastOrDefault(job =>
            job.Scenario == scenario
            && !job.IsRunning
            && job.Outcome is { Completed: false }
            && job.FinishedAt is { } at
            && now - at < RepeatWindow
            && Normalize(job.Goal) == wanted);
    }

    /// <summary>Letters and digits only, so a rephrasing that changes spacing or punctuation is still the same goal.</summary>
    private static string Normalize(string goal) =>
        new(goal.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private void Forget(AgentJob job)
    {
        lock (_gate)
        {
            _jobs.Remove(job);
        }
    }

    private void TrimFinished()
    {
        lock (_gate)
        {
            var excess = _jobs.Count(job => !job.IsRunning) - KeepFinished;
            for (var i = 0; i < _jobs.Count && excess > 0; )
            {
                if (_jobs[i].IsRunning)
                {
                    i++;
                    continue;
                }

                _jobs.RemoveAt(i);
                excess--;
            }
        }
    }
}
