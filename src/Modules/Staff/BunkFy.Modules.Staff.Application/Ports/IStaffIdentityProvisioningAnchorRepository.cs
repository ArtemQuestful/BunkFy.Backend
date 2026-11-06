namespace BunkFy.Modules.Staff.Application.Ports;

public interface IStaffIdentityProvisioningAnchorRepository
{
    async Task<IReadOnlyList<StaffIdentityProvisioningAnchorRecord>> ListAsync(
        IReadOnlyList<StaffIdentityProvisioningSourceKey> sources,
        CancellationToken cancellationToken)
    {
        List<StaffIdentityProvisioningAnchorRecord> records = [];
        foreach (StaffIdentityProvisioningSourceKey source in sources)
        {
            StaffIdentityProvisioningAnchorRecord? record = await this.GetAsync(
                source.SourceKind,
                source.SourceId,
                cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return records;
    }

    Task<StaffIdentityProvisioningAnchorRecord?> GetAsync(
        StaffIdentityProvisioningSourceKind sourceKind,
        Guid sourceId,
        CancellationToken cancellationToken);

    Task<bool> HasWorkspaceOnboardingAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken);

    Task AddAsync(
        StaffIdentityProvisioningAnchorRecord anchor,
        CancellationToken cancellationToken);
}

public sealed record StaffIdentityProvisioningSourceKey(
    StaffIdentityProvisioningSourceKind SourceKind,
    Guid SourceId);

public sealed record StaffIdentityProvisioningAnchorRecord(
    string ScopeId,
    StaffIdentityProvisioningSourceKind SourceKind,
    Guid SourceId,
    Guid StaffMemberId,
    DateTimeOffset AnchoredAtUtc,
    Guid? ResolutionEventId = null);

public enum StaffIdentityProvisioningSourceKind
{
    Unknown = 0,
    WorkspaceOnboarding = 1,
    OrganizationMembership = 2
}
