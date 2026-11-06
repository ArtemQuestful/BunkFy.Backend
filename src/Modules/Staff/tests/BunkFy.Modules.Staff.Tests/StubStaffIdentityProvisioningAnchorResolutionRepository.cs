namespace BunkFy.Modules.Staff.Tests;

using BunkFy.Modules.Staff.Application.Ports;

internal sealed class StubStaffIdentityProvisioningAnchorResolutionRepository(
    bool hasUnresolved = false)
    : IStaffIdentityProvisioningAnchorResolutionRepository
{
    private readonly Dictionary<Guid,
        StaffIdentityProvisioningAnchorResolutionRecord> records = [];

    public Task<StaffIdentityProvisioningAnchorResolutionRecord?> GetAsync(
        Guid sourceId,
        CancellationToken cancellationToken) =>
        Task.FromResult(this.records.GetValueOrDefault(sourceId));

    public Task<bool> HasUnresolvedWorkspaceOnboardingAsync(
        Guid staffMemberId,
        CancellationToken cancellationToken) =>
        Task.FromResult(hasUnresolved);

    public Task AddAsync(
        StaffIdentityProvisioningAnchorResolutionRecord resolution,
        CancellationToken cancellationToken)
    {
        this.records.Add(resolution.SourceId, resolution);
        return Task.CompletedTask;
    }
}

internal sealed class StubStaffIdentityProvisioningAnchorWriter(
    IStaffIdentityProvisioningAnchorRepository anchors)
    : IStaffIdentityProvisioningAnchorWriter
{
    public async Task<StaffIdentityProvisioningAnchorRecord> AddAsync(
        StaffIdentityProvisioningAnchorRecord anchor,
        CancellationToken cancellationToken)
    {
        StaffIdentityProvisioningAnchorRecord persisted = anchor.SourceKind ==
            StaffIdentityProvisioningSourceKind.WorkspaceOnboarding &&
            !anchor.ResolutionEventId.HasValue
                ? anchor with { ResolutionEventId = Guid.NewGuid() }
                : anchor;
        await anchors.AddAsync(persisted, cancellationToken);
        return persisted;
    }
}
