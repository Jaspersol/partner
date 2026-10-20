using Partner.Core.Agent.Delegation;

namespace Partner.Api.Agents;

/// <summary>What a job looks like from outside: for the status tool, the status bar and the jobs list.</summary>
public sealed record AgentJobView(
    string Id,
    string AgentId,
    string AgentName,
    string Scenario,
    string Goal,
    string Status,
    int Round,
    int MaxRounds,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Category,
    string? Summary,
    string? Error,
    IReadOnlyList<string> Rounds,
    int Ahead = 0,
    string? Activity = null);

/// <summary>The states a job can be in. <c>running</c> is the only one that is not final.</summary>
public static class AgentJobStatus
{
    /// <summary>Accepted, waiting for the agent to finish the job ahead of it. Not final.</summary>
    public const string Queued = "queued";

    public const string Running = "running";
    public const string Completed = "completed";
    public const string CannotExecute = "cannot_execute";
}

/// <summary>One job's mutable state. Everything is read and written under the job's own lock.</summary>
internal sealed class AgentJob(
    string id,
    string agentId,
    string agentName,
    string scenario,
    string goal,
    int maxRounds,
    DateTimeOffset startedAt)
{
    private readonly object _gate = new();
    private readonly List<string> _rounds = [];

    private string _status = AgentJobStatus.Running;
    private int _ahead;
    private string? _activity;
    private int _round;
    private DateTimeOffset? _finishedAt;
    private JobOutcome? _outcome;

    public string Id { get; } = id;

    public string AgentId { get; } = agentId;

    public string AgentName { get; } = agentName;

    public string Scenario { get; } = scenario;

    public string Goal { get; } = goal;

    public int MaxRounds { get; } = maxRounds;

    public DateTimeOffset StartedAt { get; } = startedAt;

    /// <summary>
    /// The host's generation when the job was accepted. A job that outlives the reconfiguration which cancelled
    /// it belongs to a consumer that no longer exists, and its report must not be handed to the new one.
    /// </summary>
    public int Generation { get; init; }

    /// <summary>Anything the main agent added beyond the goal.</summary>
    public string? Context { get; init; }

    /// <summary>Cancels the job. Its token is what the engine and the running turn listen to.</summary>
    public CancellationTokenSource Cancel { get; } = new();

    private readonly TaskCompletionSource _turnOver = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes when this job no longer holds its agent (it ran to its end, or was cancelled while waiting). The
    /// job behind it waits on this, which is what makes the queue first-in first-out.
    /// </summary>
    public Task TurnOver => _turnOver.Task;

    public void ReleaseTurn() => _turnOver.TrySetResult();

    /// <summary>Completes when the job has finished and its report has been queued.</summary>
    public Task Done { get; set; } = Task.CompletedTask;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _status is AgentJobStatus.Running or AgentJobStatus.Queued;
            }
        }
    }

    public JobOutcome? Outcome
    {
        get
        {
            lock (_gate)
            {
                return _outcome;
            }
        }
    }

    public DateTimeOffset? FinishedAt
    {
        get
        {
            lock (_gate)
            {
                return _finishedAt;
            }
        }
    }

    /// <summary>
    /// Puts a job behind <paramref name="ahead"/> others on the same agent. The job is accepted either way: a
    /// refusal would make the main agent wait for a free agent and ask again, which is the queue's job.
    /// </summary>
    public void MarkQueued(int ahead)
    {
        lock (_gate)
        {
            _status = AgentJobStatus.Queued;
            _ahead = ahead;
        }
    }

    /// <summary>The agent is free: the job starts now.</summary>
    public void MarkStarted()
    {
        lock (_gate)
        {
            if (_status == AgentJobStatus.Queued)
            {
                _status = AgentJobStatus.Running;
                _ahead = 0;
            }
        }
    }

    /// <summary>How many jobs on the same agent were ahead when this one was accepted; 0 when it started at once.</summary>
    public int Ahead
    {
        get
        {
            lock (_gate)
            {
                return _ahead;
            }
        }
    }

    /// <summary>What the job is doing at this moment (the tool it is inside), or null between steps.</summary>
    public void SetActivity(string? activity)
    {
        lock (_gate)
        {
            _activity = activity;
        }
    }

    public void StartRound(int round)
    {
        lock (_gate)
        {
            _round = round;
        }
    }

    public void AddRound(RoundRecord record)
    {
        lock (_gate)
        {
            _rounds.Add($"{record.Round}. {record.Reason}");
        }
    }

    public void Finish(JobOutcome outcome, DateTimeOffset at)
    {
        lock (_gate)
        {
            _outcome = outcome;
            _finishedAt = at;
            _status = outcome.Completed ? AgentJobStatus.Completed : AgentJobStatus.CannotExecute;
            _round = outcome.Rounds;
        }
    }

    public AgentJobView View()
    {
        lock (_gate)
        {
            return new AgentJobView(
                Id,
                AgentId,
                AgentName,
                Scenario,
                Goal,
                _status,
                _round,
                MaxRounds,
                StartedAt,
                _finishedAt,
                _outcome?.Category,
                _outcome?.Summary,
                _outcome?.Error,
                _rounds.ToList(),
                _ahead,
                _status == AgentJobStatus.Running ? _activity : null);
        }
    }
}
