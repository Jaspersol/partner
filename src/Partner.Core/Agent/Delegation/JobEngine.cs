using System.Text.Json;
using Partner.Core.Agent.Llm;
using Partner.Core.Contracts;
using Partner.Core.Queue;
using Partner.Core.Sessions;

namespace Partner.Core.Agent.Delegation;

/// <summary>What one job is asked to do.</summary>
/// <param name="JobId">Names the job in logs and in the message that comes back.</param>
/// <param name="Goal">What the main agent asked for.</param>
/// <param name="Context">Anything the main agent thought the sub-agent should know. May be null.</param>
/// <param name="MaxRounds">How many rounds the job may spend, counting the first.</param>
/// <param name="Deadline">A ceiling on the whole job; null means only the per-round ceiling applies.</param>
public sealed record JobSpec(string JobId, string Goal, string? Context, int MaxRounds, TimeSpan? Deadline);

/// <summary>
/// Runs one sub-agent job as a sequence of rounds and decides, in code, how it ended.
/// <para>
/// A round is one full <see cref="AgentTurnRunner"/> turn on the job's own session. It succeeds only if the
/// sub-agent called <c>task_report</c> with <c>status=success</c> and evidence; everything else is a failed
/// round, and the reason is written here from what actually happened (an unanswered approval, a refused tool,
/// a spent step budget), not taken from the model's account of it.
/// </para>
/// <para>
/// Some failures end the job at once: retrying a refusal is not persistence, it is asking again after being
/// told no. Between rounds the sub-agent is handed the failure and the list of actions already made, because a
/// GUI action is not idempotent and a round that sent the message before it failed must not send it twice.
/// </para>
/// </summary>
public sealed partial class JobEngine(
    AgentTurnRunner runner,
    ISessionStore session,
    TaskReportTool report,
    Func<string, string?>? extraTerminal = null,
    TimeProvider? clock = null)
{
    private const int ErrorLimit = 400;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<JobOutcome> RunAsync(
        JobSpec spec,
        CancellationToken ct,
        Action<int>? roundStarted = null,
        Action<RoundRecord>? roundFinished = null)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var maxRounds = Math.Max(1, spec.MaxRounds);
        var history = new List<RoundRecord>();
        var allActions = new Dictionary<string, int>(StringComparer.Ordinal);
        var allKinds = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var feedback = (string?)null;
        var started = 0;

        using var deadline = new CancellationTokenSource();
        if (spec.Deadline is { } limit)
        {
            deadline.CancelAfter(limit);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        JobOutcome Failed(string category, string error, string summary) => new(
            false, category, summary, null, Truncate(error), started, maxRounds, history, Describe(allActions, allKinds));

        for (var round = 1; round <= maxRounds; round++)
        {
            if (linked.IsCancellationRequested)
            {
                return Stopped(ct, Failed);
            }

            report.Reset();
            started = round;
            roundStarted?.Invoke(round);

            var before = session.LastSeq;
            var item = new InputItem
            {
                ItemId = $"{spec.JobId}-r{round}",
                Seq = round,
                EnqueuedAt = _clock.GetUtcNow(),
                Source = InputSource.Agent,
                Payload = round == 1 ? FirstMessage(spec, maxRounds) : feedback!,
                IdempotencyKey = $"{spec.JobId}-r{round}",
                Status = InputItemStatus.Running,
                StartedAt = _clock.GetUtcNow(),
            };

            TurnResult? turn;
            try
            {
                turn = await RunBoundedAsync(item, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Stopped(ct, Failed);
            }
            catch (ModelCallFailure failure)
            {
                return Failed(JobCategories.ModelError, failure.Message, "模型调用失败");
            }
            catch (Exception error)
            {
                return Failed(JobCategories.InternalError, error.Message, "子 agent 运行出错");
            }

            if (turn is null)
            {
                return Failed(JobCategories.Timeout, $"一轮超过 {runner.TurnTimeout.TotalSeconds:0} 秒未结束", "一轮超时");
            }

            var facts = await ReadRoundAsync(before, ct).ConfigureAwait(false);
            foreach (var (name, count) in facts.Calls)
            {
                allActions[name] = allActions.GetValueOrDefault(name) + count;
            }

            foreach (var (name, perKind) in facts.Kinds)
            {
                var total = allKinds.TryGetValue(name, out var existing) ? existing : allKinds[name] = new(StringComparer.Ordinal);
                foreach (var (kind, count) in perKind)
                {
                    total[kind] = total.GetValueOrDefault(kind) + count;
                }
            }

            var reported = report.Latest;
            if (reported is { Success: true })
            {
                var done = new RoundRecord(round, true, "成功", string.Empty, facts.Actions);
                history.Add(done);
                roundFinished?.Invoke(done);
                return new JobOutcome(
                    true, null, reported.Summary, reported.Evidence, null, round, maxRounds, history, Describe(allActions, allKinds));
            }

            var lastError = reported?.Error ?? facts.LastFailure ?? string.Empty;
            var reason = Reason(reported, turn, facts);
            var record = new RoundRecord(round, false, reason, Truncate(lastError), facts.Actions);
            history.Add(record);
            roundFinished?.Invoke(record);

            var terminal = facts.Terminal
                ?? (reported is { Retryable: false } ? JobCategories.ReportedImpossible : null);
            if (terminal is not null)
            {
                return Failed(terminal, lastError, reported?.Summary ?? reason);
            }

            feedback = Feedback(spec, round + 1, maxRounds, reason, lastError, facts.Actions);
        }

        var last = history.Count > 0 ? history[^1] : null;
        return Failed(JobCategories.RoundsExhausted, last?.Error ?? string.Empty, last?.Reason ?? "没有可用的轮次");
    }

    private async Task<TurnResult?> RunBoundedAsync(InputItem item, CancellationToken ct)
    {
        var running = runner.RunAsync(item, ct);
        var finished = await Task.WhenAny(running, Task.Delay(runner.TurnTimeout, CancellationToken.None)).ConfigureAwait(false);
        if (finished == running)
        {
            return await running.ConfigureAwait(false);
        }

        // The turn is not listening (it is parked on something that ignores its token). Let it go, as the
        // consumer loop does for the main agent: a job that waits forever on one round is a hung job.
        _ = running.ContinueWith(
            static faulted => _ = faulted.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return null;
    }

    private static JobOutcome Stopped(
        CancellationToken caller,
        Func<string, string, string, JobOutcome> failed) =>
        caller.IsCancellationRequested
            ? failed(JobCategories.Cancelled, "任务被取消", "任务被取消")
            : failed(JobCategories.Timeout, "超过了任务总时限", "任务超时");

    private static string FirstMessage(JobSpec spec, int maxRounds) =>
        $"任务目标：{spec.Goal}\n"
        + (string.IsNullOrWhiteSpace(spec.Context) ? string.Empty : $"补充信息：{spec.Context}\n")
        + $"这是第 1 轮，最多 {maxRounds} 轮。";

    private static string Feedback(JobSpec spec, int round, int maxRounds, string reason, string error, IReadOnlyList<string> actions) =>
        $"上一轮（第 {round - 1} 轮）没有成功。\n"
        + $"失败原因：{reason}\n"
        + (string.IsNullOrWhiteSpace(error) ? string.Empty : $"最后一次错误：{Truncate(error)}\n")
        + (actions.Count == 0 ? "上一轮没有执行任何动作。\n" : $"上一轮已经执行过的动作：{string.Join("，", actions)}\n")
        + "请先观察现状，判断上一轮做到了哪一步；不要重复已经完成的动作，换一种做法。\n"
        + $"这是第 {round} 轮，共 {maxRounds} 轮。\n"
        + $"任务目标：{spec.Goal}";

    private static string Reason(TaskReport? reported, TurnResult turn, RoundFacts facts)
    {
        if (facts.Terminal is { } category)
        {
            return $"被终止：{category}";
        }

        if (reported is { Success: false })
        {
            return reported.Retryable ? $"汇报失败：{reported.Summary}" : $"汇报做不成：{reported.Summary}";
        }

        if (facts.StepLimit)
        {
            return $"步数耗尽（{turn.Steps} 步）仍没有汇报";
        }

        return turn.Reason == TurnEndReason.MaxTokens ? "回复被截断，没有汇报" : "没有调用 task_report 汇报";
    }

    internal static string Truncate(string text) =>
        text.Length <= ErrorLimit ? text : text[..ErrorLimit] + "…";
}
