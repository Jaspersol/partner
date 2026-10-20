using System.Net;
using System.Text.Json;
using Partner.Core.Agent.Diagnostics;

namespace Partner.Core.Agent.Llm;

/// <summary>
/// What went wrong when a model call did not come back, in a shape somebody can act on.
/// <para>
/// An endpoint's error body is the most useful thing in the failure and the easiest to throw away. This type
/// keeps the three facts that matter: <b>which</b> call failed, <b>why</b> in the endpoint's own words, and
/// <b>what to do next</b> — plus the request id, which is the only handle the provider's own support has on it.
/// </para>
/// </summary>
public sealed class ModelCallFailure : Exception
{
    /// <summary>How the call failed, without going near the wire.</summary>
    public enum FailureKind
    {
        /// <summary>The endpoint answered, and said no.</summary>
        Rejected,

        /// <summary>The endpoint could not be reached.</summary>
        Unreachable,

        /// <summary>The endpoint did not answer in time.</summary>
        Timeout,

        /// <summary>The endpoint answered with something that is not a completion.</summary>
        Unreadable,
    }

    private ModelCallFailure(FailureKind kind, string what, string value, string reason, string nextStep, string? requestId, string detail)
        : base(Compose(what, value, reason, nextStep, detail))
    {
        Kind = kind;
        What = what;
        Value = value;
        Reason = reason;
        NextStep = nextStep;
        RequestId = requestId;
        Detail = detail;
    }
    /// <summary>Which sort of failure this is, for callers that branch on it.</summary>
    public FailureKind Kind { get; }

    /// <summary>Which call this was: completion or model listing.</summary>
    public string What { get; }

    /// <summary>The specific value involved — a status code, a host, a limit.</summary>
    public string Value { get; }

    /// <summary>Why it failed, in one clause.</summary>
    public string Reason { get; }

    /// <summary>What to do about it. Always non-empty: a failure with no next step only tells you it broke.</summary>
    public string NextStep { get; }

    /// <summary>The provider's request id, when it sent one. This is what their support asks for first.</summary>
    public string? RequestId { get; }

    /// <summary>The endpoint's own words, cleaned. Kept whole so a reader can read past the summary.</summary>
    public string Detail { get; }

    private static string Compose(string what, string value, string reason, string nextStep, string detail)
    {
        var head = $"{what}: {value} ({reason}; {nextStep})";

        // The detail is appended rather than folded into the reason: it is the provider's own wording, and it
        // is what a person quotes when they ask for help. It is sanitised first — an error body is exactly the
        // kind of text that can echo a key back.
        return string.IsNullOrWhiteSpace(detail) ? head : $"{head} · {detail}";
    }

    /// <summary>An endpoint that answered and refused.</summary>
    public static ModelCallFailure Rejected(string what, HttpStatusCode status, string body, string? requestId) =>
        new(FailureKind.Rejected, what, $"HTTP {(int)status}", ReasonFor(status), NextStepFor(status), requestId, Sanitize(body));

    /// <summary>An endpoint that could not be reached at all.</summary>
    public static ModelCallFailure Unreachable(string what, Exception cause) =>
        new(
            FailureKind.Unreachable,
            what,
            HostOf(what, cause),
            Describe(cause),
            "确认端点在运行、网络可达；本机端口可用 curl 直连验证",
            null,
            string.Empty)
        {
            Source = cause.Source,
        };

    /// <summary>
    /// An endpoint that did not answer in time.
    /// <para>
    /// <paramref name="limit"/> is null when the caller does not know it, since a cancelled task does not say
    /// why. Saying "0s" there would read as an instant, so the value falls back to a word instead.
    /// </para>
    /// </summary>
    public static ModelCallFailure TimedOut(string what, TimeSpan? limit) =>
        new(
            FailureKind.Timeout,
            what,
            limit is { } known ? $"{known.TotalSeconds:0.#}s" : "未在时限内",
            "端点未在超时内响应",
            "调大设置里的超时，或换一个更快的端点",
            null,
            string.Empty);

    /// <summary>An answer that is not a completion.</summary>
    public static ModelCallFailure Unreadable(string what, string detail) =>
        new(FailureKind.Unreadable, what, "响应格式", "端点返回的不是预期的补全结构", "确认端点兼容 OpenAI 的 /chat/completions", null, Sanitize(detail));

    /// <summary>Builds the right failure for an exception the transport threw.</summary>
    public static ModelCallFailure From(string what, Exception cause, TimeSpan? limit = null) =>
        cause is OperationCanceledException or TaskCanceledException
            ? TimedOut(what, limit)
            : Unreachable(what, cause);

    private static string Sanitize(string body) => DiagnosticText.Sanitize(body, 600);

    /// <summary>
    /// The endpoint a failed call was aimed at, for diagnosis.
    /// <para>
    /// Derived from the exception rather than the call, because a connection refused while doing something has
    /// to name <b>where</b>. Falling back to the call name is better than nothing, but should not be common.
    /// </para>
    /// </summary>
    private static string HostOf(string what, Exception cause)
    {
        var thrown = cause is HttpRequestException { InnerException: { } inner } ? inner : cause;
        return thrown is System.Net.Sockets.SocketException socket ? $"{what} @ {socket.Message}" : what;
    }

    private static string Describe(Exception cause) =>
        cause is HttpRequestException { InnerException: not null }
            ? cause.InnerException.Message
            : cause.Message;

    /// <summary>
    /// Why this status means, in a clause. The table stays short: a longer one would be a guess about bodies
    /// this layer never reads.
    /// </summary>
    private static string ReasonFor(HttpStatusCode status) => (int)status switch
    {
        400 => "端点认为请求本身不合法",
        401 => "端点拒绝了凭据",
        403 => "凭据有效但没有这个权限",
        404 => "端点上没有这个路径",
        408 => "端点等这个请求等太久",
        413 => "请求对端点来说太大",
        422 => "请求结构没错但内容不被接受",
        429 => "触发限流",
        500 => "端点自己出错",
        502 or 503 or 504 => "端点暂时不可用",
        _ => "端点拒绝了这个请求",
    };

    private static string NextStepFor(HttpStatusCode status) => (int)status switch
    {
        400 or 422 => "对照端点文档核对模型名与参数；开思考挡位后确认端点认 reasoning_effort",
        401 or 403 => "检查设置里的 API Key 是否正确、是否过期、是否有该模型的权限",
        404 => "检查端点地址：本地服务通常是 http://127.0.0.1:PORT/v1",
        413 => "调小上下文窗口或最大输出；减少一次发送的附件",
        429 => "等一会儿再试，或换一个额度更高的 Key",
        _ => "查看端点日志；带 RequestId 找服务商排查",
    };
}
