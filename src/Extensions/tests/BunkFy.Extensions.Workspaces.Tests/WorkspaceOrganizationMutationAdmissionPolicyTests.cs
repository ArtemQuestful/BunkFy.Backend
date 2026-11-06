namespace BunkFy.Extensions.Workspaces.Tests;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Modules.Organizations.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceOrganizationMutationAdmissionPolicyTests
{
    private static readonly Guid OrganizationId =
        Guid.Parse("e9797edc-e40c-4697-8ae5-9cb34331158c");
    private const string ActorSubjectId = "owner-a";
    private const string TargetSubjectId = "member-b";

    [Fact]
    public async Task Transfer_allows_an_exact_active_staff_target()
    {
        StubStaffOperationalIdentityReader staff = new()
        {
            Snapshot = CreateSnapshot(StaffStatus.Active)
        };
        WorkspaceOrganizationMutationAdmissionPolicy policy = CreatePolicy(staff);

        OrganizationMutationAdmissionDecision decision = await policy.EvaluateAsync(
            Transfer(TargetSubjectId));

        Assert.Equal(OrganizationMutationAdmissionDecision.Allowed, decision);
        Assert.Equal(
            [(OrganizationId.ToString("D"), TargetSubjectId)],
            staff.Requests);
    }

    [Theory]
    [InlineData(StaffStatus.Unknown)]
    [InlineData(StaffStatus.Suspended)]
    [InlineData(StaffStatus.Departed)]
    public async Task Transfer_denies_an_inactive_staff_target(StaffStatus status)
    {
        StubStaffOperationalIdentityReader staff = new()
        {
            Snapshot = CreateSnapshot(status)
        };
        WorkspaceOrganizationMutationAdmissionPolicy policy = CreatePolicy(staff);

        OrganizationMutationAdmissionDecision decision = await policy.EvaluateAsync(
            Transfer(TargetSubjectId));

        Assert.Equal(OrganizationMutationAdmissionDecision.Denied, decision);
    }

    [Fact]
    public async Task Transfer_denies_a_missing_staff_target()
    {
        StubStaffOperationalIdentityReader staff = new();
        WorkspaceOrganizationMutationAdmissionPolicy policy = CreatePolicy(staff);

        OrganizationMutationAdmissionDecision decision = await policy.EvaluateAsync(
            Transfer(TargetSubjectId));

        Assert.Equal(OrganizationMutationAdmissionDecision.Denied, decision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("member\nb")]
    public async Task Transfer_denies_a_malformed_target_without_reading_staff(
        string? targetSubjectId)
    {
        StubStaffOperationalIdentityReader staff = new();
        WorkspaceOrganizationMutationAdmissionPolicy policy = CreatePolicy(staff);

        OrganizationMutationAdmissionDecision decision = await policy.EvaluateAsync(
            Transfer(targetSubjectId));

        Assert.Equal(OrganizationMutationAdmissionDecision.Denied, decision);
        Assert.Empty(staff.Requests);
    }

    [Fact]
    public async Task Transfer_denies_a_snapshot_for_another_subject()
    {
        StubStaffOperationalIdentityReader staff = new()
        {
            Snapshot = CreateSnapshot(StaffStatus.Active) with
            {
                AuthSubjectId = "member-from-another-workspace"
            }
        };
        WorkspaceOrganizationMutationAdmissionPolicy policy = CreatePolicy(staff);

        OrganizationMutationAdmissionDecision decision = await policy.EvaluateAsync(
            Transfer(TargetSubjectId));

        Assert.Equal(OrganizationMutationAdmissionDecision.Denied, decision);
    }

    [Theory]
    [InlineData(WorkspaceOperationalAdmissionOutcome.Restricted,
        OrganizationMutationAdmissionDecision.Denied)]
    [InlineData(WorkspaceOperationalAdmissionOutcome.Unavailable,
        OrganizationMutationAdmissionDecision.Unavailable)]
    [InlineData(WorkspaceOperationalAdmissionOutcome.Unknown,
        OrganizationMutationAdmissionDecision.Unavailable)]
    public async Task Transfer_preserves_non_allowed_operational_admission(
        WorkspaceOperationalAdmissionOutcome outcome,
        OrganizationMutationAdmissionDecision expected)
    {
        StubStaffOperationalIdentityReader staff = new()
        {
            Snapshot = CreateSnapshot(StaffStatus.Active)
        };
        WorkspaceOrganizationMutationAdmissionPolicy policy = new(
            new StubWorkspaceOperationalAdmissionPolicy(outcome),
            staff);

        OrganizationMutationAdmissionDecision decision = await policy.EvaluateAsync(
            Transfer(TargetSubjectId));

        Assert.Equal(expected, decision);
        Assert.Empty(staff.Requests);
    }

    [Fact]
    public async Task Non_transfer_operation_keeps_existing_admission_without_staff_lookup()
    {
        StubStaffOperationalIdentityReader staff = new();
        WorkspaceOrganizationMutationAdmissionPolicy policy = CreatePolicy(staff);
        OrganizationMutationAdmissionContext context = new(
            OrganizationMutationAdmissionOperation.UpdateOrganization,
            OrganizationId,
            ActorSubjectId);

        OrganizationMutationAdmissionDecision decision = await policy.EvaluateAsync(context);

        Assert.Equal(OrganizationMutationAdmissionDecision.Allowed, decision);
        Assert.Empty(staff.Requests);
    }

    private static WorkspaceOrganizationMutationAdmissionPolicy CreatePolicy(
        StubStaffOperationalIdentityReader staff) =>
        new(new StubWorkspaceOperationalAdmissionPolicy(), staff);

    private static OrganizationMutationAdmissionContext Transfer(
        string? targetSubjectId) =>
        new(
            OrganizationMutationAdmissionOperation.TransferOwnership,
            OrganizationId,
            ActorSubjectId,
            TargetSubjectId: targetSubjectId);

    private static StaffOperationalIdentitySnapshot CreateSnapshot(
        StaffStatus status) =>
        new(Guid.NewGuid(), TargetSubjectId, status, Version: 3);

    private sealed class StubStaffOperationalIdentityReader
        : IStaffOperationalIdentityReader
    {
        public StaffOperationalIdentitySnapshot? Snapshot { get; init; }

        public List<(string TenantId, string AuthSubjectId)> Requests { get; } = [];

        public Task<StaffOperationalIdentitySnapshot?> FindAsync(
            string tenantId,
            string authSubjectId,
            CancellationToken cancellationToken = default)
        {
            this.Requests.Add((tenantId, authSubjectId));
            return Task.FromResult(this.Snapshot);
        }
    }
}
