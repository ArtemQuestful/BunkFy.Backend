namespace BunkFy.Modules.Workspaces.Tests.Persistence;

using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Cqrs;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffHistoricalNoProvisionReceiptTests
{
    private const string TenantId =
        "aaaaaaaa-1111-1111-1111-111111111111";
    private static readonly DateTimeOffset ReviewedAtUtc = new(
        2026,
        8,
        11,
        16,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void Model_is_scoped_append_only_and_uniquely_binds_operation_and_app()
    {
        using WorkspacesDbContext context = CreateContext();
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entity = context
            .Model.FindEntityType(
                typeof(WorkspaceStaffHistoricalNoProvisionReceipt))!;

        Assert.Equal(
            "staff_historical_no_provision_receipts",
            entity.GetTableName());
        Assert.NotEmpty(entity.GetDeclaredQueryFilters());
        Assert.Contains(entity.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(WorkspaceStaffHistoricalNoProvisionReceipt.ScopeId),
                nameof(WorkspaceStaffHistoricalNoProvisionReceipt.OperationId)
            ]));
        Assert.Contains(entity.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(WorkspaceStaffHistoricalNoProvisionReceipt.ScopeId),
                nameof(WorkspaceStaffHistoricalNoProvisionReceipt.ApplicationId)
            ]));
        Assert.Contains(entity.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(WorkspaceStaffHistoricalNoProvisionReceipt.ScopeId),
                nameof(WorkspaceStaffHistoricalNoProvisionReceipt.SourceKind),
                nameof(WorkspaceStaffHistoricalNoProvisionReceipt.SourceId)
            ]));
    }

    [Fact]
    public async Task Persisted_receipt_cannot_be_modified_or_deleted()
    {
        await using WorkspacesDbContext context = CreateContext();
        WorkspaceStaffHistoricalNoProvisionReceipt receipt = CreateReceipt();
        context.StaffHistoricalNoProvisionReceipts.Add(receipt);
        await context.SaveChangesAsync();

        context.Entry(receipt).State = EntityState.Modified;
        InvalidOperationException modified =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());
        Assert.Equal(
            "Workspace immutable receipts are append-only.",
            modified.Message);

        context.Entry(receipt).State = EntityState.Unchanged;
        context.StaffHistoricalNoProvisionReceipts.Remove(receipt);
        InvalidOperationException deleted =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());
        Assert.Equal(
            "Workspace immutable receipts are append-only.",
            deleted.Message);
    }

    [Fact]
    public void Exact_review_exclusion_translates_the_receipt_derived_pseudonym_for_postgresql()
    {
        using WorkspacesDbContext context = new(
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=translation_only;" +
                    "Username=translation_only;Password=translation_only")
                .Options,
            new TestScopeContext());

        string sql = context.StaffOnboardingApplications
            .ExcludeExactlyReviewed(context)
            .ToQueryString();

        Assert.Contains(
            WorkspaceStaffHistoricalNoProvisionReceipt
                .SubjectPseudonymPrefix,
            sql,
            StringComparison.Ordinal);
        Assert.Contains("::text", sql, StringComparison.Ordinal);
        Assert.Contains(
            nameof(WorkspaceStaffOnboarding.DisplayName),
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            nameof(WorkspaceStaffOnboarding
                .IdentityAnchorResolutionObservedAtUtc),
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_proof_is_exact_and_rejects_version_overflow()
    {
        WorkspaceStaffHistoricalNoProvisionReceipt receipt = CreateReceipt();

        Assert.True(receipt.HasValidCanonicalProof());
        Assert.True(receipt.MatchesReplay(
            TenantId,
            receipt.ApplicationId,
            receipt.ExpectedApplicationVersion,
            receipt.ExpectedApplicationStatus,
            receipt.OrganizationsScopeRevision,
            receipt.OrganizationsSourceVersion,
            receipt.OrganizationsSourceStatus,
            receipt.ExternalEvidenceManifestId,
            receipt.ExternalEvidenceSha256,
            receipt.ReviewerId));
        Assert.False(receipt.MatchesReplay(
            TenantId,
            receipt.ApplicationId,
            receipt.ExpectedApplicationVersion,
            receipt.ExpectedApplicationStatus,
            receipt.OrganizationsScopeRevision,
            receipt.OrganizationsSourceVersion,
            receipt.OrganizationsSourceStatus,
            receipt.ExternalEvidenceManifestId,
            new string('c', 64),
            receipt.ReviewerId));

        Assert.True(WorkspaceStaffHistoricalNoProvisionReceipt.Create(
            Guid.NewGuid(),
            TenantId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            WorkspaceStaffOnboardingSource.Invitation,
            Guid.NewGuid(),
            long.MaxValue,
            WorkspaceStaffOnboardingState.Submitted,
            long.MinValue,
            WorkspaceStaffOnboardingState.Superseded,
            organizationsScopeRevision: 1,
            organizationsSourceVersion: 1,
            WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                .InvitationRevoked,
            new string('a', 64),
            Guid.NewGuid(),
            new string('b', 64),
            "operator:reviewer",
            ReviewedAtUtc).IsFailure);
    }

    [Fact]
    public void Receipt_timestamp_is_canonical_at_postgresql_precision_before_hashing()
    {
        DateTimeOffset subMicrosecond = ReviewedAtUtc.AddTicks(9);

        WorkspaceStaffHistoricalNoProvisionReceipt receipt =
            WorkspaceStaffHistoricalNoProvisionReceipt.Create(
                Guid.Parse("11111111-2222-3333-4444-555555555556"),
                TenantId,
                Guid.Parse("22222222-2222-3333-4444-555555555556"),
                Guid.Parse("33333333-2222-3333-4444-555555555556"),
                WorkspaceStaffOnboardingSource.Invitation,
                Guid.Parse("44444444-2222-3333-4444-555555555556"),
                expectedApplicationVersion: 3,
                WorkspaceStaffOnboardingState.Superseded,
                resultApplicationVersion: 4,
                WorkspaceStaffOnboardingState.Superseded,
                organizationsScopeRevision: 5,
                organizationsSourceVersion: 6,
                WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                    .InvitationRevoked,
                new string('a', 64),
                Guid.Parse("55555555-2222-3333-4444-555555555556"),
                new string('b', 64),
                "operator:reviewer",
                subMicrosecond).Value;

        Assert.Equal(0, receipt.ReviewedAtUtc.Ticks % 10);
        Assert.Equal(ReviewedAtUtc, receipt.ReviewedAtUtc);
        Assert.True(receipt.HasValidCanonicalProof());
    }

    [Theory]
    [InlineData(
        WorkspaceStaffOnboardingSource.Invitation,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus.InvitationRevoked,
        true)]
    [InlineData(
        WorkspaceStaffOnboardingSource.Invitation,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus.InvitationSuperseded,
        true)]
    [InlineData(
        WorkspaceStaffOnboardingSource.Invitation,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus.InvitationExpired,
        true)]
    [InlineData(
        WorkspaceStaffOnboardingSource.Invitation,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus.InvitationPending,
        false)]
    [InlineData(
        WorkspaceStaffOnboardingSource.Invitation,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus.InvitationAccepted,
        false)]
    [InlineData(
        WorkspaceStaffOnboardingSource.EnrollmentLink,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus.EnrollmentLinkDisabled,
        true)]
    [InlineData(
        WorkspaceStaffOnboardingSource.EnrollmentLink,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus.EnrollmentLinkRotated,
        true)]
    [InlineData(
        WorkspaceStaffOnboardingSource.EnrollmentLink,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus.EnrollmentLinkExpired,
        true)]
    [InlineData(
        WorkspaceStaffOnboardingSource.EnrollmentLink,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus.EnrollmentLinkActive,
        false)]
    [InlineData(
        WorkspaceStaffOnboardingSource.EnrollmentLink,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus.EnrollmentLinkCapacityReached,
        false)]
    public void Terminal_authority_allowlist_is_exact(
        WorkspaceStaffOnboardingSource sourceKind,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus status,
        bool expected)
    {
        Assert.Equal(
            expected,
            WorkspaceStaffHistoricalNoProvisionReceipt.IsTerminalAuthority(
                sourceKind,
                status));
    }

    [Fact]
    public void Authority_status_contract_ordinals_are_stable()
    {
        Assert.Equal(
            Enumerable.Range(0, 11),
            Enum.GetValues<
                    WorkspaceStaffHistoricalNoProvisionAuthorityStatus>()
                .Select(status => (int)status));
    }

    [Fact]
    public void Persistence_registers_the_scoped_repository_and_dbset()
    {
        ServiceCollection applicationServices = new();
        applicationServices.AddWorkspacesApplication(
            new ConfigurationBuilder().Build(),
            "global");
        ServiceDescriptor handler = Assert.Single(
            applicationServices,
            descriptor => descriptor.ServiceType == typeof(ICommandHandler<
                ReviewWorkspaceStaffHistoricalNoProvisionCommand,
                WorkspaceStaffHistoricalNoProvisionDispositionResult>));
        Assert.Equal(
            "ReviewWorkspaceStaffHistoricalNoProvisionCommandHandler",
            handler.ImplementationType?.Name);
        Assert.Equal(ServiceLifetime.Scoped, handler.Lifetime);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            "Host=localhost;Database=unused;Username=unused;Password=unused";

        builder.AddWorkspacesPersistence();

        ServiceDescriptor registration = Assert.Single(
            builder.Services,
            descriptor => descriptor.ServiceType ==
                typeof(IWorkspaceStaffHistoricalNoProvisionReceiptRepository));
        Assert.Equal(
            "WorkspaceStaffHistoricalNoProvisionReceiptRepository",
            registration.ImplementationType?.Name);
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
        Assert.NotNull(typeof(WorkspacesDbContext).GetProperty(
            nameof(WorkspacesDbContext.StaffHistoricalNoProvisionReceipts)));
    }

    private static WorkspacesDbContext CreateContext() => new(
        new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options,
        new TestScopeContext());

    private static WorkspaceStaffHistoricalNoProvisionReceipt CreateReceipt()
        => WorkspaceStaffHistoricalNoProvisionReceipt.Create(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            TenantId,
            Guid.Parse("22222222-2222-3333-4444-555555555555"),
            Guid.Parse("33333333-2222-3333-4444-555555555555"),
            WorkspaceStaffOnboardingSource.Invitation,
            Guid.Parse("44444444-2222-3333-4444-555555555555"),
            expectedApplicationVersion: 3,
            WorkspaceStaffOnboardingState.Superseded,
            resultApplicationVersion: 4,
            WorkspaceStaffOnboardingState.Superseded,
            organizationsScopeRevision: 5,
            organizationsSourceVersion: 6,
            WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                .InvitationRevoked,
            new string('a', 64),
            Guid.Parse("55555555-2222-3333-4444-555555555555"),
            new string('b', 64),
            "operator:reviewer",
            ReviewedAtUtc).Value;

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }
}
