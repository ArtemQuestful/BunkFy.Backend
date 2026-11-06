namespace Integration.Tests.Workspaces;

using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed partial class
    WorkspaceStaffIdentityProvisioningAnchorMigrationIntegrationTests
{
    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Batched_domain_transitions_preserve_concurrency_and_exact_resolution_versions()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_anchor_batch_version_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        await using WorkspacesDbContext context = CreateDbContext(
            connectionString,
            TenantId);
        await context.Database.GetService<IMigrator>()
            .MigrateAsync(CurrentMigration).ConfigureAwait(false);
        DateTimeOffset now = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
        Guid applicationId = Guid.NewGuid();
        Guid staffMemberId = Guid.NewGuid();
        Guid resolutionEventId = Guid.NewGuid();
        Guid continuationEventId = Guid.NewGuid();
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboarding.Create(
            applicationId,
            TenantId,
            WorkspaceStaffOnboardingSource.Invitation,
            Guid.NewGuid(),
            "subject:batched-anchor",
            "batched-anchor@example.test",
            "Batched applicant",
            legalName: null,
            workEmail: null,
            workPhone: null,
            employeeNumber: null,
            jobTitle: null,
            department: null,
            now).Value;
        context.StaffOnboardingApplications.Add(application);
        await context.SaveChangesAsync().ConfigureAwait(false);
        long originalVersion = application.Version;
        await using WorkspacesDbContext staleContext = CreateDbContext(
            connectionString,
            TenantId);
        WorkspaceStaffOnboarding stale =
            await staleContext.StaffOnboardingApplications.SingleAsync(
                item => item.Id == applicationId).ConfigureAwait(false);

        foreach (long invalidVersion in new[] { originalVersion, originalVersion - 1 })
        {
            PostgresException rejectedVersion =
                await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecuteNonQueryAsync(connectionString, $$"""
                        UPDATE workspaces.staff_onboarding_applications
                        SET "StaffMemberId" = '{{staffMemberId}}',
                            "IdentityAnchorExpectedResolutionEventId" = '{{resolutionEventId}}',
                            "IdentityAnchorContinuationEventId" = '{{continuationEventId}}',
                            "Status" = 4, "VerifiedAccountEmail" = NULL,
                            "DisplayName" = NULL, "Version" = {{invalidVersion}},
                            "LastChangedAtUtc" = '2026-11-03T00:01:00Z'
                        WHERE "Id" = '{{applicationId}}'
                          AND "ScopeId" = '{{TenantId}}'
                        """)).ConfigureAwait(false);
            Assert.Equal(PostgresErrorCodes.RaiseException, rejectedVersion.SqlState);
            Assert.Contains("anchor transitions must advance", rejectedVersion.MessageText,
                StringComparison.Ordinal);
        }

        // Version counts domain changes. The unit of work persists acceptance,
        // membership binding and Staff readiness together, using the original
        // persisted Version as its optimistic concurrency predicate.
        Guid membershipId = Guid.NewGuid();
        Assert.True(application.ObserveInvitationAccepted(now.AddMinutes(1)).IsSuccess);
        Assert.True(application.BindAcceptedMembership(
            membershipId, 1, now.AddMinutes(2)).IsSuccess);
        Assert.True(application.MarkStaffReady(
            staffMemberId, resolutionEventId, continuationEventId,
            now.AddMinutes(3)).IsSuccess);
        Assert.Equal(originalVersion + 3, application.Version);
        await context.SaveChangesAsync().ConfigureAwait(false);

        Assert.True(stale.ObserveInvitationAccepted(now.AddMinutes(1)).IsSuccess);
        Assert.True(stale.BindAcceptedMembership(
            membershipId, 1, now.AddMinutes(2)).IsSuccess);
        Assert.True(stale.MarkStaffReady(
            staffMemberId, resolutionEventId, continuationEventId,
            now.AddMinutes(3)).IsSuccess);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            staleContext.SaveChangesAsync()).ConfigureAwait(false);

        Assert.True(application.Complete(now.AddMinutes(4)).IsSuccess);
        context.Entry(application)
            .Property(item => item.IdentityAnchorResolutionApplicationVersion)
            .CurrentValue = application.Version + 1;
        DbUpdateException invalidResolution =
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                context.SaveChangesAsync()).ConfigureAwait(false);
        PostgresException rejectedResolution =
            Assert.IsType<PostgresException>(invalidResolution.GetBaseException());
        Assert.Equal(PostgresErrorCodes.RaiseException, rejectedResolution.SqlState);
        Assert.Contains("exact terminal coordinates", rejectedResolution.MessageText,
            StringComparison.Ordinal);

        context.ChangeTracker.Clear();
        application = await context.StaffOnboardingApplications.SingleAsync(
            item => item.Id == applicationId).ConfigureAwait(false);
        Assert.Equal(originalVersion + 3, application.Version);
        Assert.True(application.Complete(now.AddMinutes(4)).IsSuccess);
        await context.SaveChangesAsync().ConfigureAwait(false);
        context.ChangeTracker.Clear();
        WorkspaceStaffOnboarding persisted =
            await context.StaffOnboardingApplications.SingleAsync(
                item => item.Id == applicationId).ConfigureAwait(false);
        Assert.Equal(originalVersion + 4, persisted.Version);
        Assert.Equal(persisted.Version,
            persisted.IdentityAnchorResolutionApplicationVersion);
        Assert.Equal(resolutionEventId, persisted.IdentityAnchorResolutionEventId);
        Assert.Equal(staffMemberId, persisted.IdentityAnchorResolutionStaffMemberId);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Pending_anchor_failure_requires_prior_binding_and_an_exact_recovery_version()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspace_anchor_recovery_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        await using WorkspacesDbContext context = CreateDbContext(
            connectionString,
            TenantId);
        await context.Database.GetService<IMigrator>()
            .MigrateAsync(CurrentMigration).ConfigureAwait(false);
        DateTimeOffset now = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
        Guid applicationId = Guid.NewGuid();
        Guid staffMemberId = Guid.NewGuid();
        Guid resolutionEventId = Guid.NewGuid();
        Guid continuationEventId = Guid.NewGuid();
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboarding.Create(
            applicationId,
            TenantId,
            WorkspaceStaffOnboardingSource.Invitation,
            Guid.NewGuid(),
            "subject:anchor-recovery",
            "anchor-recovery@example.test",
            "Recovery applicant",
            legalName: null,
            workEmail: null,
            workPhone: null,
            employeeNumber: null,
            jobTitle: null,
            department: null,
            now).Value;
        context.StaffOnboardingApplications.Add(application);
        await context.SaveChangesAsync().ConfigureAwait(false);
        Assert.True(application.ObserveInvitationAccepted(
            now.AddMinutes(1)).IsSuccess);
        await context.SaveChangesAsync().ConfigureAwait(false);
        long unboundVersion = application.Version;

        Assert.True(application.MarkStaffReady(
            staffMemberId,
            resolutionEventId,
            continuationEventId,
            now.AddMinutes(3)).IsSuccess);
        Assert.True(application.Fail(
            "Workspaces.AccessProvisioningFailed",
            now.AddMinutes(4)).IsSuccess);
        // Model an adversarial initial binding that otherwise satisfies the
        // one-version anchor transition guard. Failure cannot create a binding.
        context.Entry(application).Property(item => item.Version)
            .CurrentValue = unboundVersion + 1;
        DbUpdateException initialFailure =
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                context.SaveChangesAsync()).ConfigureAwait(false);
        PostgresException rejectedInitialBinding =
            Assert.IsType<PostgresException>(initialFailure.GetBaseException());
        Assert.Equal(PostgresErrorCodes.RaiseException,
            rejectedInitialBinding.SqlState);
        Assert.Contains("bound recoverable failure",
            rejectedInitialBinding.MessageText, StringComparison.Ordinal);

        context.ChangeTracker.Clear();
        application = await context.StaffOnboardingApplications.SingleAsync(
            item => item.Id == applicationId).ConfigureAwait(false);
        Assert.Equal(unboundVersion, application.Version);
        Assert.Null(application.StaffMemberId);
        Assert.True(application.MarkStaffReady(
            staffMemberId,
            resolutionEventId,
            continuationEventId,
            now.AddMinutes(3)).IsSuccess);
        await context.SaveChangesAsync().ConfigureAwait(false);
        long readyVersion = application.Version;
        // After an independently committed Staff anchor is recovered, the next
        // pass binds the currently observed membership before access work. A
        // later access failure is another domain transition in the same write.
        Assert.True(application.BindAcceptedMembership(
            Guid.NewGuid(),
            membershipVersion: 1,
            now.AddMinutes(3)).IsSuccess);
        Assert.True(application.Fail(
            "Workspaces.AccessProvisioningFailed",
            now.AddMinutes(4)).IsSuccess);
        await context.SaveChangesAsync().ConfigureAwait(false);
        Assert.Equal(readyVersion + 2, application.Version);

        PostgresException staleVersion =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(connectionString, $$"""
                    UPDATE workspaces.staff_onboarding_applications
                    SET "FailureCode" = 'Workspaces.ChangedWithoutVersion'
                    WHERE "Id" = '{{applicationId}}'
                      AND "ScopeId" = '{{TenantId}}'
                    """)).ConfigureAwait(false);
        Assert.Equal(PostgresErrorCodes.RaiseException, staleVersion.SqlState);
        Assert.Contains("bound recoverable failure", staleVersion.MessageText,
            StringComparison.Ordinal);

        PostgresException unreceiptedReopen =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(connectionString, $$"""
                    UPDATE workspaces.staff_onboarding_applications
                    SET "Status" = 3, "FailureCode" = NULL,
                        "Version" = "Version" + 1
                    WHERE "Id" = '{{applicationId}}'
                      AND "ScopeId" = '{{TenantId}}'
                    """)).ConfigureAwait(false);
        Assert.Equal(PostgresErrorCodes.RaiseException, unreceiptedReopen.SqlState);

        Assert.True(application.RecoverCompletedExact(
            now.AddMinutes(5)).IsSuccess);
        await context.SaveChangesAsync().ConfigureAwait(false);
        Assert.Equal(readyVersion + 3, application.Version);
        Assert.Equal(resolutionEventId, application.IdentityAnchorResolutionEventId);
        Assert.Equal(application.Version,
            application.IdentityAnchorResolutionApplicationVersion);
        context.ChangeTracker.Clear();
        WorkspaceStaffOnboarding persisted =
            await context.StaffOnboardingApplications.SingleAsync(
                item => item.Id == applicationId).ConfigureAwait(false);
        Assert.Equal(WorkspaceStaffOnboardingState.Completed, persisted.Status);
        Assert.Equal(application.Version, persisted.Version);
        Assert.Equal(resolutionEventId, persisted.IdentityAnchorResolutionEventId);
    }
}
