namespace BunkFy.Modules.Staff.Application.Ports;

using BunkFy.Modules.Staff.Contracts;

public interface IStaffIdentityProvisioningAnchorResolutionRepository
{
    async Task<IReadOnlyList<
        StaffIdentityProvisioningAnchorResolutionRecord>> ListAsync(
            IReadOnlyList<Guid> sourceIds,
            CancellationToken cancellationToken)
    {
        List<StaffIdentityProvisioningAnchorResolutionRecord> records = [];
        foreach (Guid sourceId in sourceIds)
        {
            StaffIdentityProvisioningAnchorResolutionRecord? record =
                await this.GetAsync(sourceId, cancellationToken)
                    .ConfigureAwait(false);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return records;
    }

    Task<StaffIdentityProvisioningAnchorResolutionRecord?> GetAsync(
        Guid sourceId,
        CancellationToken cancellationToken);

    Task<bool> HasUnresolvedWorkspaceOnboardingAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken);

    Task AddAsync(
        StaffIdentityProvisioningAnchorResolutionRecord resolution,
        CancellationToken cancellationToken);
}

public sealed record StaffIdentityProvisioningAnchorResolutionRecord(
    string ScopeId,
    StaffIdentityProvisioningSourceKind SourceKind,
    Guid SourceId,
    Guid StaffMemberId,
    long WorkspaceApplicationVersion,
    StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition Disposition,
    Guid ResolutionEventId,
    DateTimeOffset ResolvedAtUtc);
