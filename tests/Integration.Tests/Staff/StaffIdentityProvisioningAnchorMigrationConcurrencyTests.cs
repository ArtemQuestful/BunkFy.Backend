namespace Integration.Tests.Staff;

using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Staff.Persistence.Repositories;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class StaffIdentityProvisioningAnchorMigrationConcurrencyTests
{
    private const string PreviousMigration =
        "20260822003301_AddStaffRetentionControlProofAuditIntegrity";
    private const string CurrentMigration =
        "20261102220100_AddStaffIdentityProvisioningAnchors";
    private const string TenantId =
        "10000000-0000-0000-0000-000000000001";
    private const string OtherTenantId =
        "10000000-0000-0000-0000-000000000002";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Upgrade_fails_fast_for_legacy_writers_then_installs_exact_commit_time_protocol()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_staff_anchor_up_race_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        string connectionString = postgreSql.GetConnectionString();
        Guid staffMemberId =
            Guid.Parse("21000000-0000-0000-0000-000000000001");
        Guid otherStaffMemberId =
            Guid.Parse("21000000-0000-0000-0000-000000000002");
        Guid legacySourceId =
            Guid.Parse("31000000-0000-0000-0000-000000000001");
        Guid otherSourceId =
            Guid.Parse("31000000-0000-0000-0000-000000000003");
        Guid resumeSourceId =
            Guid.Parse("31000000-0000-0000-0000-000000000005");
        Guid resumeResolutionEventId =
            Guid.Parse("51000000-0000-0000-0000-000000000005");
        Guid legacyCreatedStaffMemberId =
            Guid.Parse("21000000-0000-0000-0000-000000000005");
        Guid legacyCreatedSourceId =
            Guid.Parse("31000000-0000-0000-0000-000000000006");
        DateTimeOffset completedAtUtc =
            new(2026, 8, 11, 12, 1, 0, TimeSpan.Zero);

        await using (StaffDbContext setup = CreateDbContext(
                         connectionString,
                         "bunkfy-anchor-up-setup"))
        {
            await setup.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousMigration).ConfigureAwait(false);
            await SeedMemberAsync(
                setup,
                staffMemberId,
                "subject:legacy-anchor-target",
                "Legacy Anchor Target").ConfigureAwait(false);
            await SeedMemberAsync(
                setup,
                otherStaffMemberId,
                "subject:other-anchor-target",
                "Other Anchor Target").ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }
        await ExecuteSqlAsync(
            connectionString,
            """
            UPDATE staff.staff_members
            SET "Status" = 2,
                "SuspendedAtUtc" = '2026-08-11T12:00:30Z',
                "LastChangedAtUtc" = '2026-08-11T12:00:30Z',
                "Version" = "Version" + 1
            WHERE "ScopeId" = @scopeId
              AND "Id" = @staffMemberId;
            """,
            ("scopeId", TenantId),
            ("staffMemberId", otherStaffMemberId)).ConfigureAwait(false);

        await using NpgsqlConnection legacyResume = new(
            WithApplicationName(
                connectionString,
                "bunkfy-anchor-legacy-resume"));
        await legacyResume.OpenAsync().ConfigureAwait(false);
        await using NpgsqlTransaction legacyResumeTransaction =
            await legacyResume.BeginTransactionAsync().ConfigureAwait(false);
        await LockLegacyLifecycleCoordinatesAsync(
            legacyResume,
            legacyResumeTransaction,
            otherStaffMemberId).ConfigureAwait(false);
        Guid oldResumeMutationId =
            Guid.Parse("41000000-0000-0000-0000-000000000002");
        await InsertMutationOperationAsync(
            legacyResume,
            legacyResumeTransaction,
            oldResumeMutationId,
            otherStaffMemberId,
            kind: 4,
            fingerprintCharacter: 'e',
            completedAtUtc).ConfigureAwait(false);
        await AssertUpgradeLockFailureAsync(
            connectionString,
            "bunkfy-anchor-up-active-resume").ConfigureAwait(false);
        await AssertAnchorMigrationNotAppliedAsync(connectionString)
            .ConfigureAwait(false);

        await InsertLegacyOutboxAsync(
            legacyResume,
            legacyResumeTransaction,
            oldResumeMutationId,
            "LegacyResumeCompleted").ConfigureAwait(false);
        await ExecuteNonQueryAsync(
            legacyResume,
            legacyResumeTransaction,
            """
            UPDATE staff.staff_members
            SET "Status" = 1,
                "SuspendedAtUtc" = NULL,
                "LastChangedAtUtc" = '2026-08-11T12:01:00Z',
                "Version" = "Version" + 1
            WHERE "ScopeId" = @scopeId
              AND "Id" = @staffMemberId;
            """,
            ("scopeId", TenantId),
            ("staffMemberId", otherStaffMemberId)).ConfigureAwait(false);
        await AdvanceTenantRevisionAsync(
            legacyResume,
            legacyResumeTransaction).ConfigureAwait(false);
        await legacyResumeTransaction.CommitAsync().ConfigureAwait(false);

        await using NpgsqlConnection legacyWriter = new(
            WithApplicationName(
                connectionString,
                "bunkfy-anchor-legacy-new-member-writer"));
        await legacyWriter.OpenAsync().ConfigureAwait(false);
        await using NpgsqlTransaction legacyWriterTransaction =
            await legacyWriter.BeginTransactionAsync().ConfigureAwait(false);
        await InsertLegacyStaffMemberAsync(
            legacyWriter,
            legacyWriterTransaction,
            legacyCreatedStaffMemberId).ConfigureAwait(false);
        await AssertUpgradeLockFailureAsync(
            connectionString,
            "bunkfy-anchor-up-active-onboarding").ConfigureAwait(false);
        await AssertAnchorMigrationNotAppliedAsync(connectionString)
            .ConfigureAwait(false);
        await InsertMutationOperationAsync(
            legacyWriter,
            legacyWriterTransaction,
            legacyCreatedSourceId,
            legacyCreatedStaffMemberId,
            kind: 8,
            fingerprintCharacter: 'c',
            completedAtUtc).ConfigureAwait(false);
        await InsertMutationOperationAsync(
            legacyWriter,
            legacyWriterTransaction,
            legacySourceId,
            staffMemberId,
            kind: 8,
            fingerprintCharacter: 'a',
            completedAtUtc).ConfigureAwait(false);
        await InsertLegacyOutboxAsync(
            legacyWriter,
            legacyWriterTransaction,
            legacyCreatedStaffMemberId,
            "LegacyStaffMemberCreated").ConfigureAwait(false);
        await AdvanceTenantRevisionAsync(
            legacyWriter,
            legacyWriterTransaction).ConfigureAwait(false);

        await legacyWriterTransaction.CommitAsync().ConfigureAwait(false);
        await using NpgsqlConnection legacyLifecycle = new(
            WithApplicationName(
                connectionString,
                "bunkfy-anchor-legacy-lifecycle"));
        await legacyLifecycle.OpenAsync().ConfigureAwait(false);
        await using NpgsqlTransaction legacyLifecycleTransaction =
            await legacyLifecycle.BeginTransactionAsync().ConfigureAwait(false);
        await LockLegacyLifecycleCoordinatesAsync(
            legacyLifecycle,
            legacyLifecycleTransaction,
            staffMemberId).ConfigureAwait(false);
        await using (StaffDbContext upgrade = CreateDbContext(
                         connectionString,
                         "bunkfy-anchor-up-after-drain"))
        {
            await upgrade.Database.GetService<IMigrator>()
                .MigrateAsync().ConfigureAwait(false);
        }

        Guid resolutionEventId = await AssertExactAnchorAsync(
            connectionString,
            legacySourceId,
            staffMemberId,
            completedAtUtc).ConfigureAwait(false);
        Assert.Equal(
            1L,
            await CountMutationOperationAsync(
                connectionString,
                oldResumeMutationId).ConfigureAwait(false));
        _ = await AssertExactAnchorAsync(
            connectionString,
            legacyCreatedSourceId,
            legacyCreatedStaffMemberId,
            completedAtUtc).ConfigureAwait(false);

        await InsertKind8AndExactAnchorAsync(
            connectionString,
            resumeSourceId,
            otherStaffMemberId,
            resumeResolutionEventId,
            completedAtUtc.AddMinutes(1)).ConfigureAwait(false);
        Assert.Equal(
            1L,
            await CountMutationOperationAsync(
                connectionString,
                resumeSourceId).ConfigureAwait(false));
        Assert.Equal(
            resumeResolutionEventId,
            await AssertExactAnchorAsync(
                connectionString,
                resumeSourceId,
                otherStaffMemberId,
                completedAtUtc.AddMinutes(1)).ConfigureAwait(false));

        await ExecuteNonQueryAsync(
            legacyLifecycle,
            legacyLifecycleTransaction,
            "SAVEPOINT before_resume_guard;").ConfigureAwait(false);
        await ExecuteNonQueryAsync(
            legacyLifecycle,
            legacyLifecycleTransaction,
            """
            UPDATE staff.staff_members
            SET "Status" = 2,
                "SuspendedAtUtc" = '2026-08-11T12:02:00Z',
                "LastChangedAtUtc" = '2026-08-11T12:02:00Z',
                "Version" = "Version" + 1
            WHERE "ScopeId" = @scopeId
              AND "Id" = @staffMemberId;
            """,
            ("scopeId", TenantId),
            ("staffMemberId", staffMemberId)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(
            legacyLifecycle,
            legacyLifecycleTransaction,
            "SAVEPOINT before_resume_attempt;").ConfigureAwait(false);
        PostgresException oldResumeFailure = await Assert.ThrowsAsync<
            PostgresException>(() => ExecuteNonQueryAsync(
            legacyLifecycle,
            legacyLifecycleTransaction,
            """
            UPDATE staff.staff_members
            SET "Status" = 1,
                "SuspendedAtUtc" = NULL,
                "LastChangedAtUtc" = '2026-08-11T12:02:01Z',
                "Version" = "Version" + 1
            WHERE "ScopeId" = @scopeId
              AND "Id" = @staffMemberId;
            """,
            ("scopeId", TenantId),
            ("staffMemberId", staffMemberId)));
        Assert.Equal("P0001", oldResumeFailure.SqlState);
        Assert.Contains(
            "Unresolved Workspace onboarding anchor blocks Staff identity lifecycle mutation",
            oldResumeFailure.MessageText,
            StringComparison.Ordinal);
        await ExecuteNonQueryAsync(
            legacyLifecycle,
            legacyLifecycleTransaction,
            "ROLLBACK TO SAVEPOINT before_resume_attempt;")
            .ConfigureAwait(false);
        await ExecuteNonQueryAsync(
            legacyLifecycle,
            legacyLifecycleTransaction,
            "ROLLBACK TO SAVEPOINT before_resume_guard;")
            .ConfigureAwait(false);

        Guid oldAuthMutationId =
            Guid.Parse("41000000-0000-0000-0000-000000000001");
        await InsertMutationOperationAsync(
            legacyLifecycle,
            legacyLifecycleTransaction,
            oldAuthMutationId,
            staffMemberId,
            kind: 2,
            fingerprintCharacter: 'b',
            completedAtUtc.AddMinutes(1)).ConfigureAwait(false);
        PostgresException oldWriterFailure = await Assert.ThrowsAsync<
            PostgresException>(async () =>
            await ExecuteNonQueryAsync(
                legacyLifecycle,
                legacyLifecycleTransaction,
                """
                UPDATE staff.staff_members
                SET "AuthSubjectId" = 'subject:legacy-mutated',
                    "Version" = "Version" + 1
                WHERE "ScopeId" = @scopeId
                  AND "Id" = @staffMemberId;
                """,
                ("scopeId", TenantId),
                ("staffMemberId", staffMemberId)).ConfigureAwait(false));
        Assert.Equal("P0001", oldWriterFailure.SqlState);
        Assert.Contains(
            "Workspace onboarding anchor requires access closure before Staff Auth subject change",
            oldWriterFailure.MessageText,
            StringComparison.Ordinal);
        await legacyLifecycleTransaction.RollbackAsync()
            .ConfigureAwait(false);
        Assert.Equal(
            0L,
            await CountMutationOperationAsync(
                connectionString,
                oldAuthMutationId).ConfigureAwait(false));
        Assert.Equal(
            "subject:legacy-anchor-target",
            await ReadAuthSubjectAsync(
                connectionString,
                staffMemberId).ConfigureAwait(false));

        PostgresException statusFailure = await Assert.ThrowsAsync<
            PostgresException>(() => ExecuteSqlAsync(
            connectionString,
            """
            UPDATE staff.staff_members
            SET "AuthSubjectId" = NULL,
                "Status" = 4,
                "DisplayName" = @anonymisedName,
                "DisplayNameSearch" = @anonymisedNameSearch,
                "LegalName" = NULL, "LegalNameSearch" = NULL,
                "WorkEmail" = NULL, "WorkEmailSearch" = NULL,
                "WorkPhone" = NULL, "WorkPhoneSearch" = NULL,
                "EmployeeNumber" = NULL, "EmployeeNumberSearch" = NULL,
                "JobTitle" = NULL, "Department" = NULL,
                "SuspendedAtUtc" = NULL,
                "DepartedAtUtc" = '2026-08-11T12:02:00Z',
                "DepartureEffectiveOn" = DATE '2026-08-11',
                "AnonymisedAtUtc" = '2026-08-11T12:03:00Z',
                "LastChangedAtUtc" = '2026-08-11T12:03:00Z',
                "Version" = "Version" + 1
            WHERE "ScopeId" = @scopeId
              AND "Id" = @staffMemberId;
            """,
            ("scopeId", TenantId),
            ("staffMemberId", staffMemberId),
            ("anonymisedName", StaffMember.AnonymisedDisplayName),
            ("anonymisedNameSearch", StaffMember.AnonymisedDisplayName.ToUpperInvariant())));
        Assert.Equal("P0001", statusFailure.SqlState);
        Assert.Contains(
            "Unresolved Workspace onboarding anchor blocks Staff identity lifecycle mutation",
            statusFailure.MessageText,
            StringComparison.Ordinal);

        Guid unanchoredSourceId =
            Guid.Parse("31000000-0000-0000-0000-000000000002");
        PostgresException missingAnchorFailure =
            await InsertKind8AndCommitExpectFailureAsync(
                connectionString,
                unanchoredSourceId,
                otherStaffMemberId).ConfigureAwait(false);
        Assert.Equal("P0001", missingAnchorFailure.SqlState);
        Assert.Contains(
            "Workspace onboarding receipt requires an exact Staff identity provisioning anchor",
            missingAnchorFailure.MessageText,
            StringComparison.Ordinal);

        PostgresException unresolvedDeleteFailure =
            await DeleteKind8AndCommitExpectFailureAsync(
                connectionString,
                legacySourceId,
                staffMemberId).ConfigureAwait(false);
        Assert.Equal("P0001", unresolvedDeleteFailure.SqlState);
        Assert.Contains(
            "Unresolved Workspace onboarding receipt cannot be removed",
            unresolvedDeleteFailure.MessageText,
            StringComparison.Ordinal);

        PostgresException wrongEventFailure =
            await Assert.ThrowsAsync<PostgresException>(() =>
                InsertResolutionAsync(
                    connectionString,
                    legacySourceId,
                    staffMemberId,
                    legacySourceId,
                    completedAtUtc.AddMinutes(2)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, wrongEventFailure.SqlState);

        PostgresException arbitraryEventFailure =
            await Assert.ThrowsAsync<PostgresException>(() =>
                InsertResolutionAsync(
                    connectionString,
                    legacySourceId,
                    staffMemberId,
                    Guid.Parse("51000000-0000-0000-0000-000000000001"),
                    completedAtUtc.AddMinutes(2)));
        Assert.Equal(
            "P0001",
            arbitraryEventFailure.SqlState);
        Assert.Contains(
            "does not match its exact anchor coordinates",
            arbitraryEventFailure.MessageText,
            StringComparison.Ordinal);

        PostgresException wrongTargetFailure =
            await Assert.ThrowsAsync<PostgresException>(() =>
                InsertResolutionAsync(
                    connectionString,
                    legacySourceId,
                    otherStaffMemberId,
                    resolutionEventId,
                    completedAtUtc.AddMinutes(2)));
        Assert.Equal(
            PostgresErrorCodes.ForeignKeyViolation,
            wrongTargetFailure.SqlState);

        PostgresException predatingFailure =
            await Assert.ThrowsAsync<PostgresException>(() =>
                InsertResolutionAsync(
                    connectionString,
                    legacySourceId,
                    staffMemberId,
                    resolutionEventId,
                    completedAtUtc.AddTicks(-10)));
        Assert.Equal("P0001", predatingFailure.SqlState);
        Assert.Contains(
            "does not match its exact anchor coordinates",
            predatingFailure.MessageText,
            StringComparison.Ordinal);

        await InsertResolutionAsync(
            connectionString,
            legacySourceId,
            staffMemberId,
            resolutionEventId,
            completedAtUtc.AddMinutes(2)).ConfigureAwait(false);

        await InsertResolutionAsync(
            connectionString,
            resumeSourceId,
            otherStaffMemberId,
            resumeResolutionEventId,
            completedAtUtc.AddMinutes(2)).ConfigureAwait(false);
        await ExecuteSqlAsync(
            connectionString,
            """
            UPDATE staff.staff_members
            SET "Status" = 2,
                "SuspendedAtUtc" = '2026-08-11T12:04:00Z',
                "LastChangedAtUtc" = '2026-08-11T12:04:00Z',
                "Version" = "Version" + 1
            WHERE "ScopeId" = @scopeId
              AND "Id" = @staffMemberId;
            """,
            ("scopeId", TenantId),
            ("staffMemberId", otherStaffMemberId)).ConfigureAwait(false);
        await ExecuteSqlAsync(
            connectionString,
            """
            UPDATE staff.staff_members
            SET "Status" = 1,
                "SuspendedAtUtc" = NULL,
                "LastChangedAtUtc" = '2026-08-11T12:04:01Z',
                "Version" = "Version" + 1
            WHERE "ScopeId" = @scopeId
              AND "Id" = @staffMemberId;
            """,
            ("scopeId", TenantId),
            ("staffMemberId", otherStaffMemberId)).ConfigureAwait(false);
        Assert.Equal(
            1,
            await ReadStatusAsync(
                connectionString,
                otherStaffMemberId).ConfigureAwait(false));

        await ExecuteSqlAsync(
            connectionString,
            """
            UPDATE staff.staff_members
            SET "AuthSubjectId" = NULL,
                "Status" = 4,
                "DisplayName" = @anonymisedName,
                "DisplayNameSearch" = @anonymisedNameSearch,
                "LegalName" = NULL, "LegalNameSearch" = NULL,
                "WorkEmail" = NULL, "WorkEmailSearch" = NULL,
                "WorkPhone" = NULL, "WorkPhoneSearch" = NULL,
                "EmployeeNumber" = NULL, "EmployeeNumberSearch" = NULL,
                "JobTitle" = NULL, "Department" = NULL,
                "SuspendedAtUtc" = NULL,
                "DepartedAtUtc" = '2026-08-11T12:05:00Z',
                "DepartureEffectiveOn" = DATE '2026-08-11',
                "AnonymisedAtUtc" = '2026-08-11T12:06:00Z',
                "LastChangedAtUtc" = '2026-08-11T12:06:00Z',
                "Version" = "Version" + 1
            WHERE "ScopeId" = @scopeId
              AND "Id" = @staffMemberId;
            """,
            ("scopeId", TenantId),
            ("staffMemberId", otherStaffMemberId),
            ("anonymisedName", StaffMember.AnonymisedDisplayName),
            ("anonymisedNameSearch", StaffMember.AnonymisedDisplayName.ToUpperInvariant()))
            .ConfigureAwait(false);
        Assert.Equal(
            4,
            await ReadStatusAsync(
                connectionString,
                otherStaffMemberId).ConfigureAwait(false));
        Assert.Null(
            await ReadAuthSubjectAsync(
                connectionString,
                otherStaffMemberId).ConfigureAwait(false));

        PostgresException postAnonymisationRelinkFailure =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteSqlAsync(
                    connectionString,
                    """
                    UPDATE staff.staff_members
                    SET "AuthSubjectId" = 'subject:forbidden-post-anonymisation',
                        "Version" = "Version" + 1
                    WHERE "ScopeId" = @scopeId
                      AND "Id" = @staffMemberId;
                    """,
                    ("scopeId", TenantId),
                    ("staffMemberId", otherStaffMemberId)));
        Assert.Equal("P0001", postAnonymisationRelinkFailure.SqlState);
        Assert.Contains(
            "Workspace onboarding anchor requires access closure before Staff Auth subject change",
            postAnonymisationRelinkFailure.MessageText,
            StringComparison.Ordinal);
        Assert.Null(
            await ReadAuthSubjectAsync(
                connectionString,
                otherStaffMemberId).ConfigureAwait(false));
        Assert.Equal(
            4,
            await ReadStatusAsync(
                connectionString,
                otherStaffMemberId).ConfigureAwait(false));

        Guid otherResolutionEventId =
            Guid.Parse("51000000-0000-0000-0000-000000000002");
        await InsertAnchorAsync(
            connectionString,
            otherSourceId,
            otherStaffMemberId,
            completedAtUtc.AddMinutes(1),
            otherResolutionEventId).ConfigureAwait(false);
        Guid crossTenantStaffMemberId =
            Guid.Parse("21000000-0000-0000-0000-000000000004");
        await using (StaffDbContext otherTenant = CreateDbContext(
                         connectionString,
                         "bunkfy-anchor-cross-tenant-setup",
                         OtherTenantId))
        {
            await SeedMemberAsync(
                otherTenant,
                crossTenantStaffMemberId,
                "subject:cross-tenant-anchor-target",
                "Cross Tenant Anchor Target",
                OtherTenantId).ConfigureAwait(false);
            await otherTenant.SaveChangesAsync().ConfigureAwait(false);
        }

        PostgresException crossTenantAnchorEventFailure =
            await Assert.ThrowsAsync<PostgresException>(() =>
                InsertAnchorAsync(
                    connectionString,
                    Guid.Parse("31000000-0000-0000-0000-000000000004"),
                    crossTenantStaffMemberId,
                    completedAtUtc.AddMinutes(1),
                    otherResolutionEventId,
                    OtherTenantId));
        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            crossTenantAnchorEventFailure.SqlState);
        PostgresException crossAnchorEventFailure =
            await Assert.ThrowsAsync<PostgresException>(() =>
                InsertResolutionAsync(
                    connectionString,
                    otherSourceId,
                    otherStaffMemberId,
                    resolutionEventId,
                    completedAtUtc.AddMinutes(3)));
        Assert.Equal(
            PostgresErrorCodes.UniqueViolation,
            crossAnchorEventFailure.SqlState);
        Assert.True(await IsResolutionEventIndexUniqueAsync(connectionString)
            .ConfigureAwait(false));

        await using (NpgsqlConnection resolvedWriter = new(connectionString))
        {
            await resolvedWriter.OpenAsync().ConfigureAwait(false);
            await using NpgsqlTransaction transaction =
                await resolvedWriter.BeginTransactionAsync()
                    .ConfigureAwait(false);
            await InsertMutationOperationAsync(
                resolvedWriter,
                transaction,
                oldAuthMutationId,
                staffMemberId,
                kind: 2,
                fingerprintCharacter: 'b',
                completedAtUtc.AddMinutes(3)).ConfigureAwait(false);
            PostgresException resolvedWriterFailure =
                await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecuteNonQueryAsync(
                        resolvedWriter,
                        transaction,
                        """
                        UPDATE staff.staff_members
                        SET "AuthSubjectId" = 'subject:resolved-mutation',
                            "Version" = "Version" + 1
                        WHERE "ScopeId" = @scopeId
                          AND "Id" = @staffMemberId;
                        """,
                        ("scopeId", TenantId),
                        ("staffMemberId", staffMemberId)));
            Assert.Equal("P0001", resolvedWriterFailure.SqlState);
            Assert.Contains(
                "Workspace onboarding anchor requires access closure before Staff Auth subject change",
                resolvedWriterFailure.MessageText,
                StringComparison.Ordinal);
            await transaction.RollbackAsync().ConfigureAwait(false);
        }

        await ExecuteSqlAsync(
            connectionString,
            """
            DELETE FROM staff.member_mutation_operations
            WHERE "ScopeId" = @scopeId
              AND "StaffMemberId" = @staffMemberId
              AND "Id" = @sourceId;
            """,
            ("scopeId", TenantId),
            ("staffMemberId", staffMemberId),
            ("sourceId", legacySourceId)).ConfigureAwait(false);
        Assert.Equal(
            0L,
            await CountMutationOperationAsync(
                connectionString,
                legacySourceId).ConfigureAwait(false));
        Assert.Equal(
            "subject:legacy-anchor-target",
            await ReadAuthSubjectAsync(
                connectionString,
                staffMemberId).ConfigureAwait(false));
        Assert.Equal(
            0L,
            await CountMutationOperationAsync(
                connectionString,
                oldAuthMutationId).ConfigureAwait(false));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Downgrade_waits_for_concurrent_insert_then_fails_without_dropping_anchor()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_staff_anchor_down_race_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        string baseConnectionString = postgreSql.GetConnectionString();
        Guid staffMemberId =
            Guid.Parse("20000000-0000-0000-0000-000000000001");
        Guid sourceId =
            Guid.Parse("30000000-0000-0000-0000-000000000001");
        await using (StaffDbContext setup = CreateDbContext(
                         baseConnectionString,
                         "bunkfy-anchor-down-setup"))
        {
            await setup.Database.MigrateAsync().ConfigureAwait(false);
            StaffMember member = StaffMember.Create(
                staffMemberId,
                TenantId,
                "Anchor Target",
                "Anchor Target",
                "anchor-target@example.test",
                null,
                "ANCHOR-001",
                "Owner",
                "Operations",
                "subject:anchor-target",
                "system:migration-test",
                Guid.Parse("40000000-0000-0000-0000-000000000001"),
                new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero))
                .Value;
            var repository = new StaffMemberRepository(setup);
            await repository.AddAsync(member, CancellationToken.None)
                .ConfigureAwait(false);
            await setup.SaveChangesAsync().ConfigureAwait(false);
        }

        await using NpgsqlConnection inserter = new(
            WithApplicationName(baseConnectionString, "bunkfy-anchor-inserter"));
        await inserter.OpenAsync().ConfigureAwait(false);
        await using NpgsqlTransaction insertTransaction =
            await inserter.BeginTransactionAsync().ConfigureAwait(false);
        await using (NpgsqlCommand insert = new(
                         """
                         INSERT INTO staff.identity_provisioning_anchors
                             ("ScopeId", "SourceKind", "SourceId",
                              "StaffMemberId", "AnchoredAtUtc",
                              "ResolutionEventId")
                         VALUES
                             (@scopeId, 1, @sourceId, @staffMemberId,
                              @anchoredAtUtc, @resolutionEventId);
                         """,
                         inserter,
                         insertTransaction))
        {
            insert.Parameters.AddWithValue("scopeId", TenantId);
            insert.Parameters.AddWithValue("sourceId", sourceId);
            insert.Parameters.AddWithValue("staffMemberId", staffMemberId);
            insert.Parameters.AddWithValue(
                "resolutionEventId",
                Guid.Parse("50000000-0000-0000-0000-000000000001"));
            insert.Parameters.AddWithValue(
                "anchoredAtUtc",
                new DateTimeOffset(2026, 8, 11, 12, 1, 0, TimeSpan.Zero));
            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using StaffDbContext downgrade = CreateDbContext(
            baseConnectionString,
            "bunkfy-anchor-down-migration");
        Task downgradeTask = downgrade.Database.GetService<IMigrator>()
            .MigrateAsync(PreviousMigration);

        await WaitForBlockedAnchorAccessExclusiveAsync(baseConnectionString)
            .ConfigureAwait(false);
        Assert.False(downgradeTask.IsCompleted);

        await insertTransaction.CommitAsync().ConfigureAwait(false);
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(
                async () => await downgradeTask.ConfigureAwait(false))
            .ConfigureAwait(false);
        Assert.Contains(
            "Cannot remove Staff identity provisioning anchors while durable anchors exist",
            failure.ToString(),
            StringComparison.Ordinal);

        await using NpgsqlConnection verifier = new(baseConnectionString);
        await verifier.OpenAsync().ConfigureAwait(false);
        await using NpgsqlCommand count = new(
            """
            SELECT COUNT(*)
            FROM staff.identity_provisioning_anchors
            WHERE "ScopeId" = @scopeId
              AND "SourceKind" = 1
              AND "SourceId" = @sourceId
              AND "StaffMemberId" = @staffMemberId;
            """,
            verifier);
        count.Parameters.AddWithValue("scopeId", TenantId);
        count.Parameters.AddWithValue("sourceId", sourceId);
        count.Parameters.AddWithValue("staffMemberId", staffMemberId);
        Assert.Equal(1L, await count.ExecuteScalarAsync().ConfigureAwait(false));
    }

    private static async Task SeedMemberAsync(
        StaffDbContext context,
        Guid staffMemberId,
        string authSubjectId,
        string displayName,
        string scopeId = TenantId)
    {
        StaffMember member = StaffMember.Create(
            staffMemberId,
            scopeId,
            displayName,
            displayName,
            $"{staffMemberId:N}@example.test",
            null,
            $"EMP-{staffMemberId:N}",
            "Owner",
            "Operations",
            authSubjectId,
            "system:migration-test",
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero))
            .Value;
        await new StaffMemberRepository(context)
            .AddAsync(member, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task LockLegacyLifecycleCoordinatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid staffMemberId)
    {
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            SELECT "Revision"
            FROM staff.staff_operation_locks
            WHERE "ScopeId" = @scopeId
              AND "StaffMemberId" = @staffMemberId
            FOR UPDATE;
            """,
            ("scopeId", TenantId),
            ("staffMemberId", staffMemberId)).ConfigureAwait(false);
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            SELECT "Revision"
            FROM staff.tenant_revisions
            WHERE "ScopeId" = @scopeId
            FOR SHARE;
            """,
            ("scopeId", TenantId)).ConfigureAwait(false);
    }

    private static async Task InsertMutationOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId,
        Guid staffMemberId,
        int kind,
        char fingerprintCharacter,
        DateTimeOffset completedAtUtc) =>
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            INSERT INTO staff.member_mutation_operations
                ("ScopeId", "StaffMemberId", "Id", "Kind",
                 "ExpectedVersion", "RequestFingerprint", "ResultStatus",
                 "ResultVersion", "CompletedAtUtc")
            VALUES
                (@scopeId, @staffMemberId, @operationId, @kind,
                 1, @fingerprint, 1, 1, @completedAtUtc);
            """,
            ("scopeId", TenantId),
            ("staffMemberId", staffMemberId),
            ("operationId", operationId),
            ("kind", kind),
            ("fingerprint", new string(fingerprintCharacter, 64)),
            ("completedAtUtc", completedAtUtc)).ConfigureAwait(false);

    private static async Task InsertLegacyStaffMemberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid staffMemberId) =>
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            INSERT INTO staff.staff_members
                ("Id", "DisplayName", "DisplayNameSearch", "AuthSubjectId",
                 "Status", "Version", "CreatedBy", "CreatedAtUtc",
                 "LastChangedBy", "LastChangedAtUtc", "ScopeId")
            VALUES
                (@staffMemberId, 'Legacy Created Anchor Target',
                 'legacy created anchor target',
                 'subject:legacy-created-anchor-target', 1, 1,
                 'system:migration-test', '2026-08-11T12:00:00Z',
                 'system:migration-test', '2026-08-11T12:00:00Z',
                 @scopeId);
            """,
            ("staffMemberId", staffMemberId),
            ("scopeId", TenantId)).ConfigureAwait(false);

    private static async Task InsertLegacyOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        string eventType) =>
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            INSERT INTO staff.outbox_messages
                ("Id", "Subject", "EventType", "Version", "ScopeId",
                 "OccurredAtUtc", "CreatedAtUtc", "Payload", "Attempts")
            VALUES
                (@eventId, 'staff.lifecycle.migration-proof', @eventType, 1,
                 @scopeId, '2026-08-11T12:01:00Z',
                 '2026-08-11T12:01:00Z', '{}', 0);
            """,
            ("eventId", eventId),
            ("eventType", eventType),
            ("scopeId", TenantId)).ConfigureAwait(false);

    private static async Task AdvanceTenantRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) =>
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            UPDATE staff.tenant_revisions
            SET "Revision" = "Revision" + 1
            WHERE "ScopeId" = @scopeId;
            """,
            ("scopeId", TenantId)).ConfigureAwait(false);

    private static async Task AssertUpgradeLockFailureAsync(
        string connectionString,
        string applicationName)
    {
        await using StaffDbContext upgrade = CreateDbContext(
            connectionString,
            applicationName);
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            upgrade.Database.GetService<IMigrator>().MigrateAsync());
        PostgresException postgres = Assert.IsType<PostgresException>(
            failure.GetBaseException());
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, postgres.SqlState);
    }

    private static async Task AssertAnchorMigrationNotAppliedAsync(
        string connectionString)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            """
            SELECT to_regclass('staff.identity_provisioning_anchors') IS NULL
               AND NOT EXISTS (
                    SELECT 1
                    FROM staff.__ef_migrations_history
                    WHERE "MigrationId" = @migrationId);
            """,
            connection);
        command.Parameters.AddWithValue("migrationId", CurrentMigration);
        Assert.True((bool)(await command.ExecuteScalarAsync()
            .ConfigureAwait(false))!);
    }

    private static async Task<PostgresException>
        InsertKind8AndCommitExpectFailureAsync(
            string connectionString,
            Guid operationId,
            Guid staffMemberId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync().ConfigureAwait(false);
        await InsertMutationOperationAsync(
            connection,
            transaction,
            operationId,
            staffMemberId,
            kind: 8,
            fingerprintCharacter: 'c',
            new DateTimeOffset(2026, 8, 11, 12, 4, 0, TimeSpan.Zero))
            .ConfigureAwait(false);
        return await Assert.ThrowsAsync<PostgresException>(() =>
                transaction.CommitAsync())
            .ConfigureAwait(false);
    }

    private static async Task InsertKind8AndExactAnchorAsync(
        string connectionString,
        Guid operationId,
        Guid staffMemberId,
        Guid resolutionEventId,
        DateTimeOffset completedAtUtc)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync().ConfigureAwait(false);
        await InsertMutationOperationAsync(
            connection,
            transaction,
            operationId,
            staffMemberId,
            kind: 8,
            fingerprintCharacter: 'd',
            completedAtUtc).ConfigureAwait(false);
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            INSERT INTO staff.identity_provisioning_anchors
                ("ScopeId", "SourceKind", "SourceId", "StaffMemberId",
                 "AnchoredAtUtc", "ResolutionEventId")
            VALUES
                (@scopeId, 1, @sourceId, @staffMemberId,
                 @anchoredAtUtc, @resolutionEventId);
            """,
            ("scopeId", TenantId),
            ("sourceId", operationId),
            ("staffMemberId", staffMemberId),
            ("anchoredAtUtc", completedAtUtc),
            ("resolutionEventId", resolutionEventId)).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static async Task<PostgresException>
        DeleteKind8AndCommitExpectFailureAsync(
            string connectionString,
            Guid operationId,
            Guid staffMemberId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync().ConfigureAwait(false);
        Assert.Equal(
            1,
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                """
                DELETE FROM staff.member_mutation_operations
                WHERE "ScopeId" = @scopeId
                  AND "StaffMemberId" = @staffMemberId
                  AND "Id" = @operationId;
                """,
                ("scopeId", TenantId),
                ("staffMemberId", staffMemberId),
                ("operationId", operationId)).ConfigureAwait(false));
        return await Assert.ThrowsAsync<PostgresException>(() =>
                transaction.CommitAsync())
            .ConfigureAwait(false);
    }

    private static async Task InsertResolutionAsync(
        string connectionString,
        Guid sourceId,
        Guid staffMemberId,
        Guid resolutionEventId,
        DateTimeOffset resolvedAtUtc)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync().ConfigureAwait(false);
        await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            INSERT INTO staff.identity_provisioning_anchor_resolutions
                ("ScopeId", "SourceKind", "SourceId", "StaffMemberId",
                 "WorkspaceApplicationVersion", "Disposition",
                 "ResolutionEventId", "ResolvedAtUtc")
            VALUES
                (@scopeId, 1, @sourceId, @staffMemberId,
                 1, 1, @resolutionEventId, @resolvedAtUtc);
            """,
            ("scopeId", TenantId),
            ("sourceId", sourceId),
            ("staffMemberId", staffMemberId),
            ("resolutionEventId", resolutionEventId),
            ("resolvedAtUtc", resolvedAtUtc)).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static async Task InsertAnchorAsync(
        string connectionString,
        Guid sourceId,
        Guid staffMemberId,
        DateTimeOffset anchoredAtUtc,
        Guid resolutionEventId,
        string scopeId = TenantId) => await ExecuteSqlAsync(
            connectionString,
            """
            INSERT INTO staff.identity_provisioning_anchors
                ("ScopeId", "SourceKind", "SourceId", "StaffMemberId",
                 "AnchoredAtUtc", "ResolutionEventId")
            VALUES
                (@scopeId, 1, @sourceId, @staffMemberId, @anchoredAtUtc,
                 @resolutionEventId);
            """,
            ("scopeId", scopeId),
            ("sourceId", sourceId),
            ("staffMemberId", staffMemberId),
            ("anchoredAtUtc", anchoredAtUtc),
            ("resolutionEventId", resolutionEventId)).ConfigureAwait(false);

    private static async Task<bool> IsResolutionEventIndexUniqueAsync(
        string connectionString)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            """
            SELECT EXISTS (
                SELECT 1
            FROM pg_indexes
            WHERE schemaname = 'staff'
              AND tablename = 'identity_provisioning_anchor_resolutions'
                  AND indexdef ILIKE 'CREATE UNIQUE INDEX%'
                  AND indexdef LIKE '%("ResolutionEventId")%'
            );
            """,
            connection);
        return Assert.IsType<bool>(
            await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    private static async Task<Guid> AssertExactAnchorAsync(
        string connectionString,
        Guid sourceId,
        Guid staffMemberId,
        DateTimeOffset anchoredAtUtc)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            """
            SELECT "AnchoredAtUtc", "ResolutionEventId"
            FROM staff.identity_provisioning_anchors
            WHERE "ScopeId" = @scopeId
              AND "SourceKind" = 1
              AND "SourceId" = @sourceId
              AND "StaffMemberId" = @staffMemberId;
            """,
            connection);
        command.Parameters.AddWithValue("scopeId", TenantId);
        command.Parameters.AddWithValue("sourceId", sourceId);
        command.Parameters.AddWithValue("staffMemberId", staffMemberId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync()
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync().ConfigureAwait(false));
        Assert.Equal(anchoredAtUtc.UtcDateTime, reader.GetDateTime(0));
        Guid resolutionEventId = reader.GetGuid(1);
        Assert.NotEqual(Guid.Empty, resolutionEventId);
        Assert.NotEqual(sourceId, resolutionEventId);
        Assert.False(await reader.ReadAsync().ConfigureAwait(false));
        return resolutionEventId;
    }

    private static async Task<long> CountMutationOperationAsync(
        string connectionString,
        Guid operationId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            """
            SELECT COUNT(*)
            FROM staff.member_mutation_operations
            WHERE "ScopeId" = @scopeId
              AND "Id" = @operationId;
            """,
            connection);
        command.Parameters.AddWithValue("scopeId", TenantId);
        command.Parameters.AddWithValue("operationId", operationId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ReadAuthSubjectAsync(
        string connectionString,
        Guid staffMemberId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            """
            SELECT "AuthSubjectId"
            FROM staff.staff_members
            WHERE "ScopeId" = @scopeId
              AND "Id" = @staffMemberId;
            """,
            connection);
        command.Parameters.AddWithValue("scopeId", TenantId);
        command.Parameters.AddWithValue("staffMemberId", staffMemberId);
        return await command.ExecuteScalarAsync().ConfigureAwait(false)
            as string;
    }

    private static async Task<int> ReadStatusAsync(
        string connectionString,
        Guid staffMemberId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            """
            SELECT "Status"
            FROM staff.staff_members
            WHERE "ScopeId" = @scopeId
              AND "Id" = @staffMemberId;
            """,
            connection);
        command.Parameters.AddWithValue("scopeId", TenantId);
        command.Parameters.AddWithValue("staffMemberId", staffMemberId);
        return Assert.IsType<int>(
            await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    private static async Task ExecuteSqlAsync(
        string connectionString,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await ExecuteNonQueryAsync(
            connection,
            transaction: null,
            commandText,
            parameters).ConfigureAwait(false);
    }

    private static async Task<int> ExecuteNonQueryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = new(
            commandText,
            connection,
            transaction);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task WaitForBlockedAnchorAccessExclusiveAsync(
        string connectionString)
    {
        await using NpgsqlConnection observer = new(connectionString);
        await observer.OpenAsync().ConfigureAwait(false);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using NpgsqlCommand command = new(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_locks lock
                    INNER JOIN pg_stat_activity activity
                        ON activity.pid = lock.pid
                    WHERE activity.application_name =
                              'bunkfy-anchor-down-migration'
                      AND lock.relation =
                              'staff.identity_provisioning_anchors'::regclass
                      AND lock.mode = 'AccessExclusiveLock'
                      AND NOT lock.granted);
                """,
                observer);
            if ((bool)(await command.ExecuteScalarAsync()
                    .ConfigureAwait(false))!)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25))
                .ConfigureAwait(false);
        }

        throw new TimeoutException(
            "The downgrade never waited for the anchor AccessExclusive lock.");
    }

    private static StaffDbContext CreateDbContext(
        string connectionString,
        string applicationName,
        string scopeId = TenantId)
    {
        DbContextOptions<StaffDbContext> options =
            new DbContextOptionsBuilder<StaffDbContext>()
                .UseNpgsql(
                    WithApplicationName(connectionString, applicationName),
                    provider => provider
                        .MigrationsAssembly(
                            StaffMigrations.PostgreSqlAssembly)
                        .MigrationsHistoryTable(
                            StaffMigrations.HistoryTable,
                            StaffMigrations.Schema))
                .Options;
        return new(
            options,
            new TestScopeContext(scopeId),
            OpenWorkspaceTerminationFenceReader.Instance);
    }

    private static string WithApplicationName(
        string connectionString,
        string applicationName)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString)
        {
            ApplicationName = applicationName,
        };
        return builder.ConnectionString;
    }

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => scopeId;
    }
}
