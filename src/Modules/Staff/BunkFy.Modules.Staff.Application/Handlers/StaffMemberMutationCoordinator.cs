namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Domain.Aggregates;
using Gma.Framework.Scoping;

internal sealed class StaffMemberMutationCoordinator(
    IStaffMemberRepository members,
    IStaffOperationLock operationLock,
    IScopeContext scopeContext)
{
    public Task<StaffMember?> AcquireOperationalAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) => this.AcquireAndReloadAsync(
        staffMemberId,
        members.ReloadOperationalAsync,
        cancellationToken);

    public Task<StaffMember?> AcquireSafetyTransitionAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) => this.AcquireAndReloadAsync(
        staffMemberId,
        members.ReloadForSafetyTransitionAsync,
        cancellationToken);

    private async Task<StaffMember?> AcquireAndReloadAsync(
        Guid staffMemberId,
        Func<Guid, CancellationToken, Task<StaffMember?>> reload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reload);
        if (staffMemberId == Guid.Empty ||
            !scopeContext.IsEnabled ||
            string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return null;
        }

        string tenantId = scopeContext.ScopeId;

        if (!await operationLock.TryAcquireStaffMemberAsync(
                tenantId,
                staffMemberId,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        StaffMember? reloaded = await reload(
            staffMemberId,
            cancellationToken).ConfigureAwait(false);
        return reloaded is not null &&
            reloaded.Id == staffMemberId &&
            string.Equals(
                reloaded.ScopeId,
                tenantId,
                StringComparison.Ordinal)
                ? reloaded
                : null;
    }
}
