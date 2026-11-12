using System.Text.Json;
using Partner.Core.Agent.Tools;
using Partner.Desktop.Input;
using Partner.Desktop.Perception;

namespace Partner.Desktop.Tools;

/// <summary>
/// The machine-readable head of a <c>desktop_act</c> answer. A model that only reads Chinese prose has to
/// guess whether a failed-looking sentence means "nothing happened, safe to retry" or "something may have
/// been clicked, do not press it again". The three fields answer exactly that.
/// </summary>
public static class DesktopResultContract
{
    public const string Ok = "OK";
    public const string StaleRef = "STALE_REF";
    public const string AmbiguousTarget = "AMBIGUOUS_TARGET";
    public const string PermDenied = "PERM_DENIED";
    public const string PolicyDenied = "POLICY_DENIED";
    public const string Timeout = "TIMEOUT";
    public const string AppUnresponsive = "APP_UNRESPONSIVE";
    public const string NotFound = "NOT_FOUND";
    public const string InvalidArgs = "INVALID_ARGS";

    public const string NotDelivered = "not_delivered";
    public const string PostedUnverified = "posted_unverified";
    public const string DeliveredVerified = "delivered_verified";
    public const string DeliveryUncertain = "delivery_uncertain";

    public const string RetrySafe = "safe";
    public const string RetryUnsafe = "unsafe";

    /// <summary>The JSON line that goes before the prose.</summary>
    public static string Header(string code, string delivery, string retry) =>
        JsonSerializer.Serialize(new { code, delivery, retry });

    /// <summary>A refusal that injected nothing: always <c>not_delivered</c> and safe to try again.</summary>
    public static ToolResult Refuse(string code, string message) =>
        new(false, Header(code, NotDelivered, RetrySafe) + "\n" + message);

    /// <summary>
    /// What the action's own record says about delivery and retry. A pattern call is delivery evidence of its
    /// own (it either reached the control or reported failure); injected input is only "posted", and the
    /// verification decides whether it is confirmed.
    /// </summary>
    public static (string Code, string Delivery, string Retry) Classify(DesktopAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!action.GuardPassed)
        {
            return (CodeForGuard(action.GuardReason), NotDelivered, RetrySafe);
        }

        var injection = action.Injection;
        if (injection is null)
        {
            return (Ok, NotDelivered, RetrySafe);
        }

        if (!injection.Posted)
        {
            // Part of a batch was inserted: a key or a click may have landed even though the call failed.
            return injection.Delivered > 0
                ? (Timeout, DeliveryUncertain, RetryUnsafe)
                : (Timeout, NotDelivered, RetrySafe);
        }

        return action.PostVerify?.Decision == VerifyDecision.AnyChange
            ? (Ok, DeliveredVerified, RetryUnsafe)
            : (Ok, PostedUnverified, RetryUnsafe);
    }

    /// <summary>
    /// The code for a refusal worded by a guard rather than by a record. The wording carries the cause: a
    /// missing argument, a target that is not in front, a minimized stub or a hung window.
    /// </summary>
    public static string CodeForRefusal(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.Contains("STALE_REF", StringComparison.Ordinal))
        {
            return StaleRef;
        }

        if (message.Contains("未响应", StringComparison.Ordinal))
        {
            return AppUnresponsive;
        }

        if (message.Contains("需要 elementId", StringComparison.Ordinal)
            || message.Contains("需要 text", StringComparison.Ordinal)
            || message.Contains("需要 virtualKey", StringComparison.Ordinal)
            || message.Contains("需要 scrollDelta", StringComparison.Ordinal)
            || message.Contains("只能是", StringComparison.Ordinal)
            || message.Contains("不认识", StringComparison.Ordinal)
            || message.Contains("不在 0..1", StringComparison.Ordinal)
            || message.Contains("同时给了", StringComparison.Ordinal)
            || message.Contains("需要 x/y", StringComparison.Ordinal)
            || message.Contains("需要 toX/toY", StringComparison.Ordinal))
        {
            return InvalidArgs;
        }

        return PermDenied;
    }

    private static string CodeForGuard(string? reason) => reason switch
    {
        "no_observation" or "entity_not_found" or "stale_element" => StaleRef,
        "window_gone" => NotFound,
        "app_unresponsive" => AppUnresponsive,
        "Expired" or "GenerationMismatch" or "DigestMismatch" => StaleRef,
        "EntityNotFound" => NotFound,
        "no window handle" => InvalidArgs,
        _ => PermDenied,
    };
}
