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
    IReadOnlyList<string> Rounds);

/// <summary>The states a job can be in. <c>running</c> is the only one that is not final.</summary>
public static class AgentJobStatus
{
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

    /// <summary>Anything the main agent added beyond the goal.</summary>
    public string? Context { get; init; }

    /// <summary>Cancels the job. Its token is what the engine and the running turn listen to.</summary>
    public CancellationTokenSource Cancel { get; } = new();

    /// <summary>Completes when the job has finished and its report has been queued.</summary>
    public Task Done { get; set; } = Task.CompletedTask;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _status == AgentJobStatus.Running;
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
                _rounds.ToList());
        }
    }
}
