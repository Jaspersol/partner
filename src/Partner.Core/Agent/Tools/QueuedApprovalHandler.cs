using System.Collections.Concurrent;

namespace Partner.Core.Agent.Tools;

/// <summary>
/// A tool call waiting for a human decision, as the UI sees it.
/// </summary>
/// <param name="ApprovalId">What the answer refers to.</param>
/// <param name="ToolName">Which tool wants to run.</param>
/// <param name="ArgumentsJson">What it wants to do, verbatim.</param>
/// <param name="RequestedAt">When it was asked.</param>
/// <param name="ExpiresAt">When the question stops being answerable.</param>
/// <param name="Origin">
/// Who is asking, when it is not the main agent: a sub-agent's name and what it was asked to do. A question
/// that arrives while nothing is on screen has to say whose it is, or a person cannot tell it from the others.
/// </param>
public sealed record PendingApproval(
    string ApprovalId,
    string ToolName,
    string ArgumentsJson,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    string? Origin = null);

/// <summary>
/// Approval channel that parks a request until someone answers it, and **denies when nobody does**.
/// <para>
/// The default matters more than the happy path: a turn that waits forever for a human is worse than a
/// refusal, and an irreversible action must not slip through because the window was closed, the UI was on
/// another tab, or nobody noticed.
/// </para>
/// </summary>
public sealed class QueuedApprovalHandler : IApprovalHandler
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _waiting = new();
    private readonly ConcurrentDictionary<string, PendingApproval> _requests = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    private int _sequence;

    public QueuedApprovalHandler(TimeProvider? clock = null, TimeSpan? timeout = null)
    {
        _clock = clock ?? TimeProvider.System;
        _timeout = timeout ?? TimeSpan.FromSeconds(120);
    }

    /// <summary>How long a question stays open before it is denied.</summary>
    public TimeSpan Timeout => _timeout;

    /// <summary>Unanswered requests, oldest first.</summary>
    public IReadOnlyList<PendingApproval> Pending =>
        _requests.Values.OrderBy(request => request.RequestedAt).ToList();

    public Task<bool> RequestAsync(string toolName, string argumentsJson, CancellationToken ct = default) =>
        RequestAsync(toolName, argumentsJson, origin: null, ct);

    /// <summary>The same question, with the asker named. See <see cref="PendingApproval.Origin"/>.</summary>
    public async Task<bool> RequestAsync(string toolName, string argumentsJson, string? origin, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var approvalId = $"ap-{Interlocked.Increment(ref _sequence)}-{now.UtcDateTime:HHmmss}";
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _waiting[approvalId] = completion;
        _requests[approvalId] = new PendingApproval(
            approvalId,
            toolName,
            argumentsJson,
            now,
            now + _timeout,
            origin);

        try
        {
            var answered = await Task.WhenAny(completion.Task, Task.Delay(_timeout, ct)).ConfigureAwait(false);
            return answered == completion.Task && await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _waiting.TryRemove(approvalId, out _);
            _requests.TryRemove(approvalId, out _);
        }
    }

    /// <summary>
    /// Answers a pending request. False when it is unknown or already settled — a second click must not look
    /// like it decided something.
    /// </summary>
    public bool Answer(string approvalId, bool approved)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);

        return _waiting.TryGetValue(approvalId, out var completion) && completion.TrySetResult(approved);
    }
}
