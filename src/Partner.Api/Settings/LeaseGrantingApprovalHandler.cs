using Partner.Core.Agent.Tools;

namespace Partner.Api.Settings;

/// <summary>
/// The approval path, intercepted to record what a human granted.
/// <para>
/// The approval answer is the one place a "yes" is known to have come from a person, so it is the one place a
/// continuing grant can be minted without asking the UI to cooperate. Everything forwards; only successful
/// <c>desktop_act</c> approvals mint a grant — a denial mints nothing, an approval that timed out was false, and
/// the dangerous tools are untouched since they ask for themselves every time.
/// </para>
/// </summary>
internal sealed class LeaseGrantingApprovalHandler(IApprovalHandler inner, TaskLeaseStore leases) : IApprovalHandler
{
    public async Task<bool> RequestAsync(string toolName, string argumentsJson, CancellationToken ct = default)
    {
        var approved = await inner.RequestAsync(toolName, argumentsJson, ct).ConfigureAwait(false);

        if (approved && toolName == "desktop_act")
        {
            var target = TaskLeaseStore.TryTarget(argumentsJson);
            if (target is not null)
            {
                leases.Grant(target.Value.EntityId, argumentsJson);
            }
        }

        return approved;
    }
}
