using Partner.Core.Agent.Tools;
using Partner.Desktop.Tools;

namespace Partner.Api.Settings;

/// <summary>Records continuing desktop authorization only when the runner accepts an approval.</summary>
internal sealed class LeaseGrantingApprovalHandler(IApprovalHandler inner, TaskLeaseStore leases)
    : IApprovalHandler, IAcceptedApprovalHandler
{
    public Task<bool> RequestAsync(string toolName, string argumentsJson, CancellationToken ct = default) =>
        inner.RequestAsync(toolName, argumentsJson, ct);

    public void Accept(string toolName, string argumentsJson, Func<bool>? isStillAuthorized)
    {
        if (toolName != "desktop_act" || SystemShortcutGuard.DescribeBlocked(argumentsJson) is not null)
            return;

        var target = TaskLeaseStore.TryTarget(argumentsJson);
        if (target is not null)
            leases.Grant(target.Value.EntityId, argumentsJson, isStillAuthorized: isStillAuthorized);
    }
}
