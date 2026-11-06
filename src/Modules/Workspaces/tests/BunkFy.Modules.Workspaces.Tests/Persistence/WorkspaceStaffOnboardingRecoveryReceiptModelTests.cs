namespace BunkFy.Modules.Workspaces.Tests.Persistence;

using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffOnboardingRecoveryReceiptModelTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 25, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Model_is_scope_bound_append_only_and_database_constrained()
    {
        using WorkspacesDbContext context = CreateContext();
        IModel model = context.GetService<IDesignTimeModel>().Model;
        IEntityType receipt = model.FindEntityType(
            typeof(WorkspaceStaffOnboardingRecoveryReceipt))!;
        IEntityType application = model.FindEntityType(
            typeof(WorkspaceStaffOnboarding))!;

        Assert.Contains(receipt.GetCheckConstraints(), constraint =>
            constraint.Name ==
                "CK_workspaces_staff_onboarding_recovery_receipts_no_mutation");
        Assert.Contains(receipt.GetCheckConstraints(), constraint =>
            constraint.Name ==
                "CK_workspaces_staff_onboarding_recovery_receipts_versions");
        Assert.Contains(receipt.GetCheckConstraints(), constraint =>
            constraint.Name ==
                "CK_workspaces_staff_onboarding_recovery_receipts_outcome");
        Assert.Contains(receipt.GetCheckConstraints(), constraint =>
            constraint.Name ==
                "CK_workspaces_staff_onboarding_recovery_receipts_verification");
        Assert.Contains(receipt.GetCheckConstraints(), constraint =>
            constraint.Name ==
                "CK_workspaces_staff_onboarding_recovery_receipts_presence");
        Assert.Contains(receipt.GetCheckConstraints(), constraint =>
            constraint.Name ==
                "CK_workspaces_staff_onboarding_recovery_receipts_chronology");
        Assert.Contains(receipt.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(
                [nameof(WorkspaceStaffOnboardingRecoveryReceipt.ScopeId),
                 nameof(WorkspaceStaffOnboardingRecoveryReceipt.OperationId)]));
        Assert.Contains(receipt.GetForeignKeys(), foreignKey =>
            foreignKey.DeleteBehavior == DeleteBehavior.Cascade &&
            foreignKey.Properties.Select(property => property.Name).SequenceEqual(
                [nameof(WorkspaceStaffOnboardingRecoveryReceipt.ScopeId),
                 nameof(WorkspaceStaffOnboardingRecoveryReceipt.ApplicationId)]));
        Assert.True(receipt.FindProperty(
            nameof(WorkspaceStaffOnboardingRecoveryReceipt
                .OrganizationObservedAtUtc))!.IsNullable);
        Assert.Contains(application.GetCheckConstraints(), constraint =>
            constraint.Name == "CK_staff_onboarding_membership_binding");
    }

    [Fact]
    public async Task Persisted_receipt_cannot_be_updated_or_deleted()
    {
        await using WorkspacesDbContext context = CreateContext();
        WorkspaceStaffOnboardingRecoveryReceipt receipt = CreateReceipt();
        context.StaffOnboardingRecoveryReceipts.Add(receipt);
        await context.SaveChangesAsync();

        context.Entry(receipt).Property(item => item.Reason).CurrentValue =
            WorkspaceStaffOnboardingRecoveryReason.AccessConflict;
        InvalidOperationException updateFailure = await Assert.ThrowsAsync<
            InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains("append-only", updateFailure.Message);
        context.Entry(receipt).State = EntityState.Unchanged;
        context.StaffOnboardingRecoveryReceipts.Remove(receipt);
        InvalidOperationException deleteFailure = await Assert.ThrowsAsync<
            InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains("append-only", deleteFailure.Message);
    }

    private static WorkspaceStaffOnboardingRecoveryReceipt CreateReceipt() =>
        WorkspaceStaffOnboardingRecoveryReceipt.Create(
            new WorkspaceStaffOnboardingRecoveryReceiptValues(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                WorkspaceStaffOnboardingRecoveryTriggerKind.ManualOperator,
                Guid.NewGuid(),
                "auth:applicant",
                "auth:plan-creator",
                "auth:recovery-operator",
                "system:workspaces",
                new string('a', 64),
                ExpectedApplicationVersion: 6,
                SelectedApplicationVersion: 6,
                WorkspaceStaffOnboardingState.Failed,
                ResultingApplicationVersion: 6,
                WorkspaceStaffOnboardingState.Failed,
                Guid.NewGuid(),
                CurrentProfileVersion: 3,
                PropertyCount: 2,
                new string('b', 64),
                OrganizationRevision: null,
                MembershipId: null,
                MembershipVersion: null,
                MembershipStatus: null,
                StaffTenantRevision: null,
                StaffMemberVersion: null,
                StaffStateProofSha256: null,
                StaffPropertySetSha256: null,
                AccessManagementRevision: null,
                AccessFactsSha256: null,
                WorkspaceRecoveryVerification.Unknown,
                WorkspaceRecoveryVerification.Unknown,
                WorkspaceRecoveryVerification.Unknown,
                WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
                WorkspaceStaffOnboardingRecoveryReason
                    .LegacyMembershipBindingMissing,
                AccessMutationPerformed: false,
                OrganizationObservedAtUtc: null,
                StaffObservedAtUtc: null,
                AccessObservedAtUtc: null,
                Now)).Value;

    private static WorkspacesDbContext CreateContext() => new(
        new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseInMemoryDatabase($"workspace-recovery-model-{Guid.NewGuid():N}")
            .Options,
        new TestScopeContext());

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
