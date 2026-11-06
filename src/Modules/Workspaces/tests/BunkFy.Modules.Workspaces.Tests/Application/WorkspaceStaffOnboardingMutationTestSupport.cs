namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Modules.Organizations.Contracts;

internal static class WorkspaceStaffOnboardingMutationTestSupport
{
    public static WorkspaceStaffOnboardingMutationCoordinator Create(
        IWorkspaceStaffOnboardingRepository applications,
        IWorkspaceStaffOnboardingOperationLock? operationLock = null) =>
        new(operationLock ?? new NoOpOperationLock(), applications);

    public static WorkspaceStaffOnboardingMutationCoordinator
        CreateForSourceOnly() => new(new NoOpOperationLock(), null!);

    public static WorkspaceStaffOnboardingIdentityAnchorConvergence
        CreateIdentityAnchorConvergence(
            IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader? outcomes =
                null) =>
        new(
            outcomes ??
                new StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader(),
            WorkspaceStaffAccessMutationTestSupport.Create(
                WorkspaceStaffAccessMutationTestSupport.NoOpenProcesses),
            WorkspaceStaffAccessMutationTestSupport.NoOpenProcesses,
            new WorkspaceAccessProvisioner(
                roles: null!,
                profiles: null!,
                scopedProfiles: null!),
            new TestClock(),
            new TestIds());

    private sealed class NoOpOperationLock
        : IWorkspaceStaffOnboardingOperationLock
    {
        public Task AcquireSourceReadAsync(
            Guid sourceId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireSourceWriteAsync(
            Guid sourceId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireApplicantAsync(
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> TryAcquireAsync(
            Guid applicationId,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow =>
            new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class TestIds : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }
}

internal sealed class FakeOrganizationEnrollmentClaimInspector(
    OrganizationEnrollmentClaimDto? claim = null)
    : IOrganizationEnrollmentClaimInspector
{
    public OrganizationEnrollmentClaimDto? Claim { get; set; } = claim;

    public List<(Guid OrganizationId, Guid EnrollmentLinkId, string SubjectId)>
        Requests
    { get; } = [];

    public Task<OrganizationEnrollmentClaimDto?> FindAsync(
        Guid organizationId,
        Guid enrollmentLinkId,
        string subjectId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.Requests.Add((organizationId, enrollmentLinkId, subjectId));
        return Task.FromResult(this.Claim);
    }
}

internal sealed class FakeWorkspaceStaffDeferredClaimWithdrawalRepository(
    params WorkspaceStaffDeferredClaimWithdrawal[] seed)
    : IWorkspaceStaffDeferredClaimWithdrawalRepository
{
    private readonly List<WorkspaceStaffDeferredClaimWithdrawal> withdrawals =
        [.. seed];

    public IReadOnlyList<WorkspaceStaffDeferredClaimWithdrawal> Items =>
        this.withdrawals;

    public Task<WorkspaceStaffDeferredClaimWithdrawal?> GetAsync(
        Guid claimId,
        CancellationToken cancellationToken) =>
        Task.FromResult(this.withdrawals.SingleOrDefault(item =>
            item.Id == claimId));

    public Task<bool> AnyBySourceAsync(
        Guid enrollmentLinkId,
        CancellationToken cancellationToken) =>
        Task.FromResult(this.withdrawals.Any(item =>
            item.EnrollmentLinkId == enrollmentLinkId));

    public Task AddAsync(
        WorkspaceStaffDeferredClaimWithdrawal withdrawal,
        CancellationToken cancellationToken)
    {
        this.withdrawals.Add(withdrawal);
        return Task.CompletedTask;
    }

    public void Remove(WorkspaceStaffDeferredClaimWithdrawal withdrawal) =>
        this.withdrawals.Remove(withdrawal);

    public Task<int> RemoveBySourceAsync(
        Guid enrollmentLinkId,
        CancellationToken cancellationToken)
    {
        int removed = this.withdrawals.RemoveAll(item =>
            item.EnrollmentLinkId == enrollmentLinkId);
        return Task.FromResult(removed);
    }
}
