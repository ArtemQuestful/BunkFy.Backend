namespace Integration.Tests;

using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class PropertiesCanonicalTimeZoneMigrationIntegrationTests
{
    private const string PreviousMigration =
        "20260807212415_AddBedMutationOperationReceipts";
    private const string TenantId = "tenant-time-zone-migration";
    private const string CatalogVersion =
        "TZDB: 2026c (mapping: 48.2)";
    private const string AppendSeamCatalogVersion =
        "TZDB: provider-append-seam-test";
    private const string Digest =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";
    private static readonly Guid LegacyPropertyId = Guid.Parse(
        "a1000000-0000-0000-0000-000000000001");
    private static readonly Guid AliasPropertyId = Guid.Parse(
        "a1000000-0000-0000-0000-000000000002");
    private static readonly Guid NativePropertyId = Guid.Parse(
        "a1000000-0000-0000-0000-000000000003");
    private static readonly Guid InvalidPropertyId = Guid.Parse(
        "a1000000-0000-0000-0000-000000000004");
    private static readonly Guid CanonicalLegacyPropertyId = Guid.Parse(
        "a1000000-0000-0000-0000-000000000005");
    private static readonly Guid TimeZoneOperationId = Guid.Parse(
        "a2000000-0000-0000-0000-000000000001");
    private static readonly Guid TimeZoneRevisionId = Guid.Parse(
        "a3000000-0000-0000-0000-000000000001");
    private static readonly Guid AliasTimeZoneRevisionId = Guid.Parse(
        "a3000000-0000-0000-0000-000000000002");
    private static readonly Guid NativeCreatedRevisionId = Guid.Parse(
        "a3000000-0000-0000-0000-000000000003");
    private static readonly Guid NativeLondonOperationId = Guid.Parse(
        "a2000000-0000-0000-0000-000000000002");
    private static readonly Guid NativeLondonRevisionId = Guid.Parse(
        "a3000000-0000-0000-0000-000000000004");
    private static readonly Guid NativeUtcOperationId = Guid.Parse(
        "a2000000-0000-0000-0000-000000000003");
    private static readonly Guid NativeUtcRevisionId = Guid.Parse(
        "a3000000-0000-0000-0000-000000000005");
    private static readonly DateTimeOffset OccurredAtUtc = new(
        2026,
        8,
        13,
        20,
        0,
        0,
        TimeSpan.Zero);
    private static readonly DateTimeOffset LondonOccurredAtUtc =
        OccurredAtUtc.AddMinutes(1);
    private static readonly DateTimeOffset UtcOccurredAtUtc =
        OccurredAtUtc.AddMinutes(2);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Migration_is_locked_preserving_append_only_and_safely_reversible()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_properties_time_zone_migration_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();

        await using (PropertiesDbContext previous = CreateDbContext(
                         connectionString))
        {
            await previous.GetService<IMigrator>()
                .MigrateAsync(PreviousMigration)
                .ConfigureAwait(false);
        }

        await AssertMigrationLockIsFailFastAsync(connectionString)
            .ConfigureAwait(false);
        await AssertInFlightDestructionBlocksUpAsync(connectionString)
            .ConfigureAwait(false);
        await AssertStructurallyInvalidTimeZoneBlocksUpAsync(
                connectionString)
            .ConfigureAwait(false);

        await using PropertiesDbContext dbContext = CreateDbContext(
            connectionString);
        await SeedLegacyPropertiesAsync(dbContext).ConfigureAwait(false);
        IMigrator migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync().ConfigureAwait(false);

        await AssertLegacyValuesPreservedAsync(dbContext)
            .ConfigureAwait(false);
        Assert.Equal(
            8,
            await ReadTenantRevisionAsync(dbContext).ConfigureAwait(false));
        await AssertLedgerSchemaAsync(dbContext).ConfigureAwait(false);
        await AssertCatalogAndNoSyntheticHistoryAsync(dbContext)
            .ConfigureAwait(false);
        await AssertCatalogRuntimeWritesRejectedAsync(dbContext)
            .ConfigureAwait(false);
        await AssertMigrationOwnedCatalogAppendSeamAsync(dbContext)
            .ConfigureAwait(false);
        await AssertCatalogRuntimeWritesRejectedAsync(dbContext)
            .ConfigureAwait(false);
        await AssertCreatedReceiptNeedsActualInsertionAsync(dbContext)
            .ConfigureAwait(false);
        await InsertValidNativeCreationAsync(dbContext)
            .ConfigureAwait(false);
        await AssertPropertyWriteWithoutLedgerRejectedAsync(dbContext)
            .ConfigureAwait(false);
        await AssertForgedRequestedIdentifierRejectedAsync(dbContext)
            .ConfigureAwait(false);
        await AssertFalseSemanticReceiptsRejectedAsync(dbContext)
            .ConfigureAwait(false);
        await AssertChangedReceiptNeedsAnActualTimeZoneTransitionAsync(
                dbContext)
            .ConfigureAwait(false);
        await InsertNativeRoundTripHistoryAsync(dbContext)
            .ConfigureAwait(false);
        await AssertHistoricalLedgerCannotAuthorizeNewTransitionAsync(
                dbContext)
            .ConfigureAwait(false);
        await InsertTimeZoneHistoryAsync(dbContext).ConfigureAwait(false);

        Exception nativeHistoryFailure = await Assert.ThrowsAnyAsync<
            Exception>(() => migrator.MigrateAsync(PreviousMigration));
        PostgresException nativeHistory = Assert.IsType<PostgresException>(
            nativeHistoryFailure.GetBaseException());
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
            nativeHistory.SqlState);
        Assert.Contains(
            "native history",
            nativeHistory.MessageText,
            StringComparison.Ordinal);

        await AssertRawMutationsRejectedAsync(dbContext)
            .ConfigureAwait(false);
        Guid destroyOperationId = await SeedClosingDestructionAsync(dbContext)
            .ConfigureAwait(false);
        await AssertMismatchedDestructionGucRejectedAsync(dbContext)
            .ConfigureAwait(false);
        await DeleteWithAuthorizedDestructionGucAsync(
                dbContext,
                destroyOperationId)
            .ConfigureAwait(false);

        Exception inFlightDownFailure = await Assert.ThrowsAnyAsync<
            Exception>(() => migrator.MigrateAsync(PreviousMigration));
        PostgresException inFlightDown = Assert.IsType<PostgresException>(
            inFlightDownFailure.GetBaseException());
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
            inFlightDown.SqlState);
        Assert.Contains(
            "tenant destruction is in flight",
            inFlightDown.MessageText,
            StringComparison.Ordinal);

        await RemoveClosingDestructionAsync(dbContext, destroyOperationId)
            .ConfigureAwait(false);
        long revisionBeforeDown = await ReadTenantRevisionAsync(dbContext)
            .ConfigureAwait(false);
        await migrator.MigrateAsync(PreviousMigration).ConfigureAwait(false);
        Assert.Equal(
            revisionBeforeDown + 1,
            await ReadTenantRevisionAsync(dbContext).ConfigureAwait(false));

        Assert.Equal(
            0,
            await dbContext.Database.SqlQuery<int>($"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM information_schema.tables
                    WHERE table_schema = 'properties'
                      AND table_name = 'property_time_zone_operations'
                    """).SingleAsync().ConfigureAwait(false));
        await AssertConvergedValuesPreservedByDownAsync(dbContext)
            .ConfigureAwait(false);
    }

    private static async Task AssertMigrationLockIsFailFastAsync(
        string connectionString)
    {
        await using NpgsqlConnection blocker = new(connectionString);
        await blocker.OpenAsync().ConfigureAwait(false);
        await using NpgsqlTransaction transaction =
            await blocker.BeginTransactionAsync().ConfigureAwait(false);
        await using (NpgsqlCommand command = new(
                         "LOCK TABLE properties.properties " +
                         "IN ACCESS SHARE MODE;",
                         blocker,
                         transaction))
        {
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using PropertiesDbContext competing = CreateDbContext(
            connectionString);
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            competing.GetService<IMigrator>().MigrateAsync());
        PostgresException providerFailure = Assert.IsType<PostgresException>(
            failure.GetBaseException());
        Assert.Equal(
            PostgresErrorCodes.LockNotAvailable,
            providerFailure.SqlState);
        await transaction.RollbackAsync().ConfigureAwait(false);
    }

    private static async Task AssertInFlightDestructionBlocksUpAsync(
        string connectionString)
    {
        Guid destroyOperationId = Guid.Parse(
            "a4000000-0000-0000-0000-000000000001");
        await using PropertiesDbContext dbContext = CreateDbContext(
            connectionString);
        await InsertClosingDestructionAsync(
                dbContext,
                destroyOperationId,
                stage: 1)
            .ConfigureAwait(false);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            dbContext.GetService<IMigrator>().MigrateAsync());
        PostgresException providerFailure = Assert.IsType<PostgresException>(
            failure.GetBaseException());
        Assert.Equal(
            PostgresErrorCodes.ObjectNotInPrerequisiteState,
            providerFailure.SqlState);
        Assert.Contains(
            "tenant destruction is in flight",
            providerFailure.MessageText,
            StringComparison.Ordinal);

        await RemoveClosingDestructionAsync(dbContext, destroyOperationId)
            .ConfigureAwait(false);
    }

    private static async Task AssertStructurallyInvalidTimeZoneBlocksUpAsync(
        string connectionString)
    {
        await using PropertiesDbContext dbContext = CreateDbContext(
            connectionString);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO properties.properties
                    ("Id", "Name", "Code", "TimeZoneId", "Status",
                     "CreatedAtUtc", "ScopeId")
                VALUES
                    ({InvalidPropertyId}, 'Invalid persisted zone',
                     'invalid-persisted-zone', {"\u00a0"}, 1,
                     {OccurredAtUtc}, {TenantId});
                """).ConfigureAwait(false);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            dbContext.GetService<IMigrator>().MigrateAsync());
        PostgresException providerFailure = Assert.IsType<PostgresException>(
            failure.GetBaseException());
        Assert.Equal(
            PostgresErrorCodes.RaiseException,
            providerFailure.SqlState);
        Assert.Contains(
            "cannot be restored",
            providerFailure.MessageText,
            StringComparison.Ordinal);

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM properties.properties
                WHERE "Id" = {InvalidPropertyId};
                """).ConfigureAwait(false);
    }

    private static Task<int> SeedLegacyPropertiesAsync(
        PropertiesDbContext dbContext) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO properties.properties
                ("Id", "Name", "Code", "TimeZoneId", "Status",
                 "CreatedAtUtc", "ScopeId")
            VALUES
                ({LegacyPropertyId}, 'Legacy Windows zone', 'legacy-zone',
                 'Pacific Standard Time', 1, {OccurredAtUtc}, {TenantId}),
                ({AliasPropertyId}, 'Legacy TZDB alias', 'legacy-alias',
                 'UTC', 1, {OccurredAtUtc}, {TenantId}),
                ({CanonicalLegacyPropertyId}, 'Legacy canonical zone',
                 'legacy-canonical-zone', 'Etc/UTC', 1, {OccurredAtUtc},
                 {TenantId});

            INSERT INTO properties.property_operation_locks
                ("Id", "PropertyId", "Revision", "ScopeId")
            VALUES
                ({LegacyPropertyId}, {LegacyPropertyId}, 1, {TenantId}),
                ({AliasPropertyId}, {AliasPropertyId}, 1, {TenantId}),
                ({CanonicalLegacyPropertyId}, {CanonicalLegacyPropertyId},
                 1, {TenantId});

            INSERT INTO properties.tenant_revisions
                ("ScopeId", "Revision", "LifecycleStatus")
            VALUES ({TenantId}, 7, 1)
            ON CONFLICT ("ScopeId") DO UPDATE
            SET "Revision" = 7,
                "LifecycleStatus" = 1,
                "DestroyOperationId" = NULL,
                "DestroyRequestSha256" = NULL,
                "DestroyStartedAtUtc" = NULL;
            """);

    private static async Task AssertLegacyValuesPreservedAsync(
        PropertiesDbContext dbContext)
    {
        string[] values = await dbContext.Database.SqlQuery<string>($"""
                SELECT "TimeZoneId" AS "Value"
                FROM properties.properties
                WHERE "ScopeId" = {TenantId}
                ORDER BY "TimeZoneId"
                """).ToArrayAsync().ConfigureAwait(false);
        Assert.Equal(["Etc/UTC", "Pacific Standard Time", "UTC"], values);

        Property legacy = await dbContext.Properties
            .AsNoTracking()
            .SingleAsync(property => property.Id == LegacyPropertyId)
            .ConfigureAwait(false);
        Assert.Equal("Pacific Standard Time", legacy.TimeZoneId.Value);
    }

    private static async Task AssertConvergedValuesPreservedByDownAsync(
        PropertiesDbContext dbContext)
    {
        string[] values = await dbContext.Database.SqlQuery<string>($"""
                SELECT "TimeZoneId" AS "Value"
                FROM properties.properties
                WHERE "ScopeId" = {TenantId}
                ORDER BY "Id"
                """).ToArrayAsync().ConfigureAwait(false);
        Assert.Equal(
            ["Etc/UTC", "Etc/UTC", "Etc/UTC", "Etc/UTC"],
            values);
    }

    private static Task<long> ReadTenantRevisionAsync(
        PropertiesDbContext dbContext) =>
        dbContext.Database.SqlQuery<long>($"""
                SELECT "Revision" AS "Value"
                FROM properties.tenant_revisions
                WHERE "ScopeId" = {TenantId}
                """).SingleAsync();

    private static async Task AssertLedgerSchemaAsync(
        PropertiesDbContext dbContext)
    {
        string[] constraints = await dbContext.Database.SqlQuery<string>($"""
                SELECT constraint_name AS "Value"
                FROM information_schema.table_constraints
                WHERE table_schema = 'properties'
                  AND table_name = 'property_time_zone_operations'
                ORDER BY constraint_name
                """).ToArrayAsync().ConfigureAwait(false);
        Assert.Contains(
            "PK_property_time_zone_operations",
            constraints,
            StringComparer.Ordinal);
        Assert.Contains(
            "FK_property_time_zone_operations_properties_ScopeId_PropertyId",
            constraints,
            StringComparer.Ordinal);
        Assert.Contains(
            constraints,
            constraint => constraint.StartsWith(
                "FK_property_time_zone_operations_property_operation_locks_",
                StringComparison.Ordinal));
        Assert.Contains(
            constraints,
            constraint => constraint.StartsWith(
                "FK_property_time_zone_operations_property_time_zone_catalog_re",
                StringComparison.Ordinal));
        Assert.Contains(
            "CK_properties_property_time_zone_operation_change",
            constraints,
            StringComparer.Ordinal);
        Assert.Contains(
            "CK_properties_property_time_zone_operation_ids",
            constraints,
            StringComparer.Ordinal);
        Assert.Contains(
            "CK_properties_property_time_zone_operation_text",
            constraints,
            StringComparer.Ordinal);

        string[] indexes = await dbContext.Database.SqlQuery<string>($"""
                SELECT indexname AS "Value"
                FROM pg_indexes
                WHERE schemaname = 'properties'
                  AND tablename = 'property_time_zone_operations'
                ORDER BY indexname
                """).ToArrayAsync().ConfigureAwait(false);
        Assert.Contains(
            "IX_property_time_zone_operations_scope_occurred",
            indexes,
            StringComparer.Ordinal);
        Assert.DoesNotContain(
            "IX_property_time_zone_operations_scope_operation",
            indexes,
            StringComparer.Ordinal);
        Assert.Contains(
            "IX_property_time_zone_operations_scope_revision",
            indexes,
            StringComparer.Ordinal);

        Assert.Equal(
            1,
            await dbContext.Database.SqlQuery<int>($"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM pg_trigger
                    WHERE tgname =
                        'property_time_zone_operations_append_only'
                      AND NOT tgisinternal
                    """).SingleAsync().ConfigureAwait(false));
        string transactionIdType = await dbContext.Database.SqlQuery<string>(
                $"""
                SELECT format_type(attribute.atttypid, attribute.atttypmod)
                    AS "Value"
                FROM pg_attribute attribute
                INNER JOIN pg_class relation
                    ON relation.oid = attribute.attrelid
                INNER JOIN pg_namespace schema
                    ON schema.oid = relation.relnamespace
                WHERE schema.nspname = 'properties'
                  AND relation.relname =
                      'property_time_zone_transaction_proofs'
                  AND attribute.attname = 'TransactionId'
                """)
            .SingleAsync().ConfigureAwait(false);
        Assert.Equal("xid8", transactionIdType);
        string parsedValidationFunction = await dbContext.Database
            .SqlQuery<string>($"""
                SELECT pg_get_functiondef(
                    'properties.validate_property_time_zone_operation_insert()'::regprocedure)
                    AS "Value"
                """)
            .SingleAsync().ConfigureAwait(false);
        string parsedPropertyGuardFunction = await dbContext.Database
            .SqlQuery<string>($"""
                SELECT pg_get_functiondef(
                    'properties.require_property_time_zone_operation()'::regprocedure)
                    AS "Value"
                """)
            .SingleAsync().ConfigureAwait(false);
        string parsedTransactionProtocol =
            parsedValidationFunction + parsedPropertyGuardFunction;
        Assert.Contains(
            "pg_current_xact_id()",
            parsedTransactionProtocol,
            StringComparison.Ordinal);
        Assert.Contains(
            "pg_xact_status",
            parsedTransactionProtocol,
            StringComparison.Ordinal);
        Assert.Contains(
            "xmin::text::xid8",
            parsedTransactionProtocol,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "::bigint",
            parsedTransactionProtocol,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "= pg_current_xact_id()::xid",
            parsedTransactionProtocol,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            await dbContext.Database.SqlQuery<int>($"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM pg_trigger
                    WHERE tgname =
                        'properties_require_time_zone_operation'
                      AND NOT tgisinternal
                    """).SingleAsync().ConfigureAwait(false));
        Assert.Equal(
            1,
            await dbContext.Database.SqlQuery<int>($"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM pg_trigger
                    WHERE tgname =
                        'properties_record_time_zone_transaction_proof'
                      AND NOT tgisinternal
                    """).SingleAsync().ConfigureAwait(false));
    }

    private static async Task AssertCatalogAndNoSyntheticHistoryAsync(
        PropertiesDbContext dbContext)
    {
        Assert.Equal(
            340,
            await dbContext.Database.SqlQuery<int>($"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM properties.property_time_zone_catalog_entries
                    WHERE "CatalogVersion" = {CatalogVersion}
                    """).SingleAsync().ConfigureAwait(false));
        Assert.Equal(
            597,
            await dbContext.Database.SqlQuery<int>($"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM properties.property_time_zone_catalog_resolutions
                    WHERE "CatalogVersion" = {CatalogVersion}
                    """).SingleAsync().ConfigureAwait(false));
        string utcResolution = await dbContext.Database.SqlQuery<string>($"""
                SELECT "CanonicalTimeZoneId" AS "Value"
                FROM properties.property_time_zone_catalog_resolutions
                WHERE "CatalogVersion" = {CatalogVersion}
                  AND "RequestedTimeZoneId" = 'UTC'
                """).SingleAsync().ConfigureAwait(false);
        Assert.Equal("Etc/UTC", utcResolution);
        Assert.Equal(
            0,
            await dbContext.Database.SqlQuery<int>($"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM properties.property_time_zone_catalog_resolutions
                    WHERE "CatalogVersion" = {CatalogVersion}
                      AND "RequestedTimeZoneId" =
                          'Pacific Standard Time'
                    """).SingleAsync().ConfigureAwait(false));
        Assert.Equal(
            0,
            await dbContext.Database.SqlQuery<int>($"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM properties.property_time_zone_operations
                    """).SingleAsync().ConfigureAwait(false));
        Assert.Equal(
            0,
            await dbContext.Database.SqlQuery<int>($"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM properties.property_time_zone_transaction_proofs
                    """).SingleAsync().ConfigureAwait(false));
    }

    private static async Task AssertCatalogRuntimeWritesRejectedAsync(
        PropertiesDbContext dbContext)
    {
        PostgresException entryInsert = await Assert.ThrowsAsync<
            PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO properties.property_time_zone_catalog_entries
                    ("CatalogVersion", "TimeZoneId", "Ordinal")
                VALUES ('TZDB: forged', 'Etc/Forged', 1);
                """));
        Assert.Equal(
            PostgresErrorCodes.ObjectNotInPrerequisiteState,
            entryInsert.SqlState);

        PostgresException resolutionUpdate = await Assert.ThrowsAsync<
            PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE properties.property_time_zone_catalog_resolutions
                SET "CanonicalTimeZoneId" = 'Europe/London'
                WHERE "CatalogVersion" = {CatalogVersion}
                  AND "RequestedTimeZoneId" = 'UTC';
                """));
        Assert.Equal(
            PostgresErrorCodes.ObjectNotInPrerequisiteState,
            resolutionUpdate.SqlState);

        PostgresException entryDelete = await Assert.ThrowsAsync<
            PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM properties.property_time_zone_catalog_entries
                WHERE "CatalogVersion" = {CatalogVersion}
                  AND "TimeZoneId" = 'Etc/UTC';
                """));
        Assert.Equal(
            PostgresErrorCodes.ObjectNotInPrerequisiteState,
            entryDelete.SqlState);
    }

    private static async Task AssertMigrationOwnedCatalogAppendSeamAsync(
        PropertiesDbContext dbContext)
    {
        await using IDbContextTransaction transaction = await dbContext
            .Database.BeginTransactionAsync().ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                LOCK TABLE
                    properties.property_time_zone_catalog_entries,
                    properties.property_time_zone_catalog_resolutions
                IN ACCESS EXCLUSIVE MODE;

                DROP TRIGGER
                    property_time_zone_catalog_entries_immutable
                ON properties.property_time_zone_catalog_entries;
                DROP TRIGGER
                    property_time_zone_catalog_resolutions_immutable
                ON properties.property_time_zone_catalog_resolutions;

                INSERT INTO properties.property_time_zone_catalog_entries
                    ("CatalogVersion", "TimeZoneId", "Ordinal")
                VALUES ({AppendSeamCatalogVersion}, 'Etc/UTC', 1);
                INSERT INTO properties.property_time_zone_catalog_resolutions
                    ("CatalogVersion", "RequestedTimeZoneId",
                     "CanonicalTimeZoneId", "Ordinal")
                VALUES ({AppendSeamCatalogVersion}, 'UTC', 'Etc/UTC', 1);

                CREATE TRIGGER
                    property_time_zone_catalog_entries_immutable
                BEFORE INSERT OR UPDATE OR DELETE
                ON properties.property_time_zone_catalog_entries
                FOR EACH ROW
                EXECUTE FUNCTION
                    properties.reject_property_time_zone_catalog_mutation();
                CREATE TRIGGER
                    property_time_zone_catalog_resolutions_immutable
                BEFORE INSERT OR UPDATE OR DELETE
                ON properties.property_time_zone_catalog_resolutions
                FOR EACH ROW
                EXECUTE FUNCTION
                    properties.reject_property_time_zone_catalog_mutation();
                """).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);

        Assert.Equal(
            340,
            await dbContext.Database.SqlQuery<int>($"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM properties.property_time_zone_catalog_entries
                    WHERE "CatalogVersion" = {CatalogVersion}
                    """).SingleAsync().ConfigureAwait(false));
        Assert.Equal(
            597,
            await dbContext.Database.SqlQuery<int>($"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM properties.property_time_zone_catalog_resolutions
                    WHERE "CatalogVersion" = {CatalogVersion}
                    """).SingleAsync().ConfigureAwait(false));
        Assert.Equal(
            1,
            await dbContext.Database.SqlQuery<int>($"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM properties.property_time_zone_catalog_entries
                    WHERE "CatalogVersion" = {AppendSeamCatalogVersion}
                      AND "TimeZoneId" = 'Etc/UTC'
                    """).SingleAsync().ConfigureAwait(false));
        Assert.Equal(
            "Etc/UTC",
            await dbContext.Database.SqlQuery<string>($"""
                    SELECT "CanonicalTimeZoneId" AS "Value"
                    FROM properties.property_time_zone_catalog_resolutions
                    WHERE "CatalogVersion" = {AppendSeamCatalogVersion}
                      AND "RequestedTimeZoneId" = 'UTC'
                    """).SingleAsync().ConfigureAwait(false));
    }

    private static async Task InsertValidNativeCreationAsync(
        PropertiesDbContext dbContext)
    {
        await using IDbContextTransaction transaction = await dbContext
            .Database.BeginTransactionAsync().ConfigureAwait(false);
        await transaction.CreateSavepointAsync("native_creation")
            .ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO properties.properties
                    ("Id", "Name", "Code", "TimeZoneId", "Status",
                     "CreatedAtUtc", "ScopeId")
                VALUES
                    ({NativePropertyId}, 'Native canonical zone',
                     'native-canonical-zone', 'Etc/UTC', 1,
                     {OccurredAtUtc}, {TenantId});

                INSERT INTO properties.property_operation_locks
                    ("Id", "PropertyId", "Revision", "ScopeId")
                VALUES
                    ({NativePropertyId}, {NativePropertyId}, 1,
                     {TenantId});

                INSERT INTO properties.property_time_zone_operations
                    ("ScopeId", "PropertyId", "OperationId", "RevisionId",
                     "ChangeKind", "RequestedTimeZoneId",
                     "PreviousTimeZoneId", "TimeZoneId", "CatalogVersion",
                     "ExpectedVersion", "ResultVersion", "ActorId",
                     "OccurredAtUtc")
                VALUES
                    ({TenantId}, {NativePropertyId}, {NativePropertyId},
                     {NativeCreatedRevisionId}, 1, 'UTC', NULL, 'Etc/UTC',
                     {CatalogVersion}, 0, 1, 'system:provider-test',
                     {OccurredAtUtc});

                SET CONSTRAINTS ALL IMMEDIATE;
                """).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);

        Assert.Equal(
            0,
            await CountTransactionProofsAsync(dbContext).ConfigureAwait(false));
    }

    private static async Task AssertCreatedReceiptNeedsActualInsertionAsync(
        PropertiesDbContext dbContext)
    {
        await using IDbContextTransaction transaction = await dbContext
            .Database.BeginTransactionAsync().ConfigureAwait(false);
        try
        {
            Guid revisionId = Guid.Parse(
                "a3000000-0000-0000-0000-000000000014");
            PostgresException failure = await Assert.ThrowsAsync<
                PostgresException>(() =>
                dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE properties.property_operation_locks
                    SET "Revision" = "Revision"
                    WHERE "ScopeId" = {TenantId}
                      AND "Id" = {CanonicalLegacyPropertyId};

                    UPDATE properties.properties
                    SET "Name" = "Name"
                    WHERE "ScopeId" = {TenantId}
                      AND "Id" = {CanonicalLegacyPropertyId};

                    INSERT INTO properties.property_time_zone_operations
                        ("ScopeId", "PropertyId", "OperationId",
                         "RevisionId", "ChangeKind",
                         "RequestedTimeZoneId", "PreviousTimeZoneId",
                         "TimeZoneId", "CatalogVersion",
                         "ExpectedVersion", "ResultVersion", "ActorId",
                         "OccurredAtUtc")
                    VALUES
                        ({TenantId}, {CanonicalLegacyPropertyId},
                         {CanonicalLegacyPropertyId}, {revisionId}, 1, 'UTC',
                         NULL, 'Etc/UTC', {CatalogVersion}, 0, 1,
                         'system:forged', {OccurredAtUtc});

                    SET CONSTRAINTS ALL IMMEDIATE;
                    """));
            Assert.Equal(
                PostgresErrorCodes.ObjectNotInPrerequisiteState,
                failure.SqlState);
            Assert.Contains(
                "lacks its same-transaction property insertion",
                failure.MessageText,
                StringComparison.Ordinal);
        }
        finally
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }

        Assert.Equal(
            0,
            await CountTransactionProofsAsync(dbContext).ConfigureAwait(false));
    }

    private static async Task AssertPropertyWriteWithoutLedgerRejectedAsync(
        PropertiesDbContext dbContext)
    {
        await using IDbContextTransaction transaction = await dbContext
            .Database.BeginTransactionAsync().ConfigureAwait(false);
        try
        {
            PostgresException failure = await Assert.ThrowsAsync<
                PostgresException>(() =>
                dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE properties.property_operation_locks
                    SET "Revision" = "Revision" + 1
                    WHERE "ScopeId" = {TenantId}
                      AND "Id" = {NativePropertyId};

                    UPDATE properties.properties
                    SET "TimeZoneId" = 'Europe/London',
                        "Version" = 2,
                        "UpdatedAtUtc" = {LondonOccurredAtUtc}
                    WHERE "ScopeId" = {TenantId}
                      AND "Id" = {NativePropertyId};

                    SET CONSTRAINTS ALL IMMEDIATE;
                    """));
            Assert.Equal(
                PostgresErrorCodes.ObjectNotInPrerequisiteState,
                failure.SqlState);
            Assert.Contains(
                "requires a same-transaction operation",
                failure.MessageText,
                StringComparison.Ordinal);
        }
        finally
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }

        Assert.Equal(
            "Etc/UTC",
            await ReadTimeZoneAsync(dbContext, NativePropertyId)
                .ConfigureAwait(false));
        Assert.Equal(
            0,
            await CountTransactionProofsAsync(dbContext).ConfigureAwait(false));
    }

    private static async Task AssertForgedRequestedIdentifierRejectedAsync(
        PropertiesDbContext dbContext)
    {
        await using IDbContextTransaction transaction = await dbContext
            .Database.BeginTransactionAsync().ConfigureAwait(false);
        try
        {
            Guid operationId = Guid.Parse(
                "a2000000-0000-0000-0000-000000000010");
            Guid revisionId = Guid.Parse(
                "a3000000-0000-0000-0000-000000000010");
            PostgresException failure = await Assert.ThrowsAsync<
                PostgresException>(() =>
                dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE properties.property_operation_locks
                    SET "Revision" = "Revision" + 1
                    WHERE "ScopeId" = {TenantId}
                      AND "Id" = {NativePropertyId};

                    INSERT INTO properties.property_time_zone_operations
                        ("ScopeId", "PropertyId", "OperationId",
                         "RevisionId", "ChangeKind", "RequestedTimeZoneId",
                         "PreviousTimeZoneId", "TimeZoneId",
                         "CatalogVersion", "ExpectedVersion",
                         "ResultVersion", "ActorId", "OccurredAtUtc")
                    VALUES
                        ({TenantId}, {NativePropertyId}, {operationId},
                         {revisionId}, 2, 'Pacific Standard Time',
                         'Etc/UTC', 'Etc/UTC', {CatalogVersion}, 1, 1,
                         'system:forged', {OccurredAtUtc});
                    """));
            Assert.Equal(
                PostgresErrorCodes.ForeignKeyViolation,
                failure.SqlState);
        }
        finally
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
    }

    private static async Task AssertFalseSemanticReceiptsRejectedAsync(
        PropertiesDbContext dbContext)
    {
        await AssertSemanticReceiptRejectedAsync(
                dbContext,
                NativePropertyId,
                Guid.Parse("a2000000-0000-0000-0000-000000000011"),
                Guid.Parse("a3000000-0000-0000-0000-000000000011"),
                changeKind: 3,
                requestedTimeZoneId: "Europe/London",
                previousTimeZoneId: "Etc/UTC",
                timeZoneId: "Europe/London",
                expectedVersion: 1,
                occurredAtUtc: LondonOccurredAtUtc,
                expectedMessage: "does not match the catalog resolution")
            .ConfigureAwait(false);
        await AssertSemanticReceiptRejectedAsync(
                dbContext,
                AliasPropertyId,
                Guid.Parse("a2000000-0000-0000-0000-000000000012"),
                Guid.Parse("a3000000-0000-0000-0000-000000000012"),
                changeKind: 4,
                requestedTimeZoneId: "UTC",
                previousTimeZoneId: "UTC",
                timeZoneId: "Etc/UTC",
                expectedVersion: 1,
                occurredAtUtc: LondonOccurredAtUtc,
                expectedMessage: "must not be a catalog canonicalization")
            .ConfigureAwait(false);
    }

    private static async Task AssertSemanticReceiptRejectedAsync(
        PropertiesDbContext dbContext,
        Guid propertyId,
        Guid operationId,
        Guid revisionId,
        int changeKind,
        string requestedTimeZoneId,
        string previousTimeZoneId,
        string timeZoneId,
        long expectedVersion,
        DateTimeOffset occurredAtUtc,
        string expectedMessage)
    {
        await using IDbContextTransaction transaction = await dbContext
            .Database.BeginTransactionAsync().ConfigureAwait(false);
        try
        {
            PostgresException failure = await Assert.ThrowsAsync<
                PostgresException>(() =>
                dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE properties.property_operation_locks
                    SET "Revision" = "Revision" + 1
                    WHERE "ScopeId" = {TenantId}
                      AND "Id" = {propertyId};

                    UPDATE properties.properties
                    SET "TimeZoneId" = {timeZoneId},
                        "Version" = {expectedVersion + 1},
                        "UpdatedAtUtc" = {occurredAtUtc}
                    WHERE "ScopeId" = {TenantId}
                      AND "Id" = {propertyId}
                      AND "Version" = {expectedVersion}
                      AND "TimeZoneId" = {previousTimeZoneId};

                    INSERT INTO properties.property_time_zone_operations
                        ("ScopeId", "PropertyId", "OperationId",
                         "RevisionId", "ChangeKind", "RequestedTimeZoneId",
                         "PreviousTimeZoneId", "TimeZoneId",
                         "CatalogVersion", "ExpectedVersion",
                         "ResultVersion", "ActorId", "OccurredAtUtc")
                    VALUES
                        ({TenantId}, {propertyId}, {operationId}, {revisionId},
                         {changeKind}, {requestedTimeZoneId},
                         {previousTimeZoneId}, {timeZoneId}, {CatalogVersion},
                         {expectedVersion}, {expectedVersion + 1},
                         'system:forged', {occurredAtUtc});

                    SET CONSTRAINTS ALL IMMEDIATE;
                    """));
            Assert.Equal(
                PostgresErrorCodes.ObjectNotInPrerequisiteState,
                failure.SqlState);
            Assert.Contains(
                expectedMessage,
                failure.MessageText,
                StringComparison.Ordinal);
        }
        finally
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }

        Assert.Equal(
            previousTimeZoneId,
            await ReadTimeZoneAsync(dbContext, propertyId)
                .ConfigureAwait(false));
    }

    private static async Task
        AssertChangedReceiptNeedsAnActualTimeZoneTransitionAsync(
            PropertiesDbContext dbContext)
    {
        await using IDbContextTransaction transaction = await dbContext
            .Database.BeginTransactionAsync().ConfigureAwait(false);
        try
        {
            Guid operationId = Guid.Parse(
                "a2000000-0000-0000-0000-000000000013");
            Guid revisionId = Guid.Parse(
                "a3000000-0000-0000-0000-000000000013");
            PostgresException failure = await Assert.ThrowsAsync<
                PostgresException>(() =>
                dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE properties.property_operation_locks
                    SET "Revision" = "Revision" + 1
                    WHERE "ScopeId" = {TenantId}
                      AND "Id" = {NativePropertyId};

                    UPDATE properties.properties
                    SET "Name" = 'Unrelated row update',
                        "Version" = 2,
                        "UpdatedAtUtc" = {LondonOccurredAtUtc}
                    WHERE "ScopeId" = {TenantId}
                      AND "Id" = {NativePropertyId};

                    INSERT INTO properties.property_time_zone_operations
                        ("ScopeId", "PropertyId", "OperationId",
                         "RevisionId", "ChangeKind", "RequestedTimeZoneId",
                         "PreviousTimeZoneId", "TimeZoneId",
                         "CatalogVersion", "ExpectedVersion",
                         "ResultVersion", "ActorId", "OccurredAtUtc")
                    VALUES
                        ({TenantId}, {NativePropertyId}, {operationId},
                         {revisionId}, 4, 'UTC', 'Europe/London', 'Etc/UTC',
                         {CatalogVersion}, 1, 2, 'system:forged',
                         {LondonOccurredAtUtc});

                    SET CONSTRAINTS ALL IMMEDIATE;
                    """));
            Assert.Equal(
                PostgresErrorCodes.ObjectNotInPrerequisiteState,
                failure.SqlState);
            Assert.Contains(
                "lacks its same-transaction time-zone transition",
                failure.MessageText,
                StringComparison.Ordinal);
        }
        finally
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
    }

    private static async Task InsertNativeRoundTripHistoryAsync(
        PropertiesDbContext dbContext)
    {
        await ApplyValidTransitionAsync(
                dbContext,
                NativePropertyId,
                NativeLondonOperationId,
                NativeLondonRevisionId,
                changeKind: 4,
                requestedTimeZoneId: "Europe/London",
                previousTimeZoneId: "Etc/UTC",
                timeZoneId: "Europe/London",
                expectedVersion: 1,
                LondonOccurredAtUtc)
            .ConfigureAwait(false);
        await ApplyValidTransitionAsync(
                dbContext,
                NativePropertyId,
                NativeUtcOperationId,
                NativeUtcRevisionId,
                changeKind: 4,
                requestedTimeZoneId: "UTC",
                previousTimeZoneId: "Europe/London",
                timeZoneId: "Etc/UTC",
                expectedVersion: 2,
                UtcOccurredAtUtc)
            .ConfigureAwait(false);
    }

    private static async Task
        AssertHistoricalLedgerCannotAuthorizeNewTransitionAsync(
            PropertiesDbContext dbContext)
    {
        await using IDbContextTransaction transaction = await dbContext
            .Database.BeginTransactionAsync().ConfigureAwait(false);
        try
        {
            PostgresException failure = await Assert.ThrowsAsync<
                PostgresException>(() =>
                dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE properties.property_operation_locks
                    SET "Revision" = "Revision" + 1
                    WHERE "ScopeId" = {TenantId}
                      AND "Id" = {NativePropertyId};

                    UPDATE properties.properties
                    SET "Version" = 1
                    WHERE "ScopeId" = {TenantId}
                      AND "Id" = {NativePropertyId};

                    UPDATE properties.properties
                    SET "TimeZoneId" = 'Europe/London',
                        "Version" = 2,
                        "UpdatedAtUtc" = {LondonOccurredAtUtc}
                    WHERE "ScopeId" = {TenantId}
                      AND "Id" = {NativePropertyId};

                    SET CONSTRAINTS ALL IMMEDIATE;
                    """));
            Assert.Equal(
                PostgresErrorCodes.ObjectNotInPrerequisiteState,
                failure.SqlState);
            Assert.Contains(
                "requires a same-transaction operation",
                failure.MessageText,
                StringComparison.Ordinal);
        }
        finally
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }

        Assert.Equal(
            "Etc/UTC",
            await ReadTimeZoneAsync(dbContext, NativePropertyId)
                .ConfigureAwait(false));
    }

    private static async Task InsertTimeZoneHistoryAsync(
        PropertiesDbContext dbContext)
    {
        await ApplyValidTransitionAsync(
                dbContext,
                LegacyPropertyId,
                TimeZoneOperationId,
                TimeZoneRevisionId,
                changeKind: 4,
                requestedTimeZoneId: "UTC",
                previousTimeZoneId: "Pacific Standard Time",
                timeZoneId: "Etc/UTC",
                expectedVersion: 1,
                OccurredAtUtc.AddMinutes(3))
            .ConfigureAwait(false);
        await ApplyValidTransitionAsync(
                dbContext,
                AliasPropertyId,
                TimeZoneOperationId,
                AliasTimeZoneRevisionId,
                changeKind: 3,
                requestedTimeZoneId: "UTC",
                previousTimeZoneId: "UTC",
                timeZoneId: "Etc/UTC",
                expectedVersion: 1,
                OccurredAtUtc.AddMinutes(4))
            .ConfigureAwait(false);

        Assert.Equal(
            2,
            await dbContext.Database.SqlQuery<int>($"""
                    SELECT COUNT(*)::integer AS "Value"
                    FROM properties.property_time_zone_operations
                    WHERE "ScopeId" = {TenantId}
                      AND "OperationId" = {TimeZoneOperationId}
                    """).SingleAsync().ConfigureAwait(false));
    }

    private static async Task ApplyValidTransitionAsync(
        PropertiesDbContext dbContext,
        Guid propertyId,
        Guid operationId,
        Guid revisionId,
        int changeKind,
        string requestedTimeZoneId,
        string previousTimeZoneId,
        string timeZoneId,
        long expectedVersion,
        DateTimeOffset occurredAtUtc)
    {
        await using IDbContextTransaction transaction = await dbContext
            .Database.BeginTransactionAsync().ConfigureAwait(false);
        await transaction.CreateSavepointAsync("native_transition")
            .ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE properties.property_operation_locks
                SET "Revision" = "Revision" + 1
                WHERE "ScopeId" = {TenantId}
                  AND "Id" = {propertyId};

                UPDATE properties.properties
                SET "TimeZoneId" = {timeZoneId},
                    "Version" = {expectedVersion + 1},
                    "UpdatedAtUtc" = {occurredAtUtc}
                WHERE "ScopeId" = {TenantId}
                  AND "Id" = {propertyId}
                  AND "Version" = {expectedVersion}
                  AND "TimeZoneId" = {previousTimeZoneId};

                INSERT INTO properties.property_time_zone_operations
                    ("ScopeId", "PropertyId", "OperationId", "RevisionId",
                     "ChangeKind", "RequestedTimeZoneId",
                     "PreviousTimeZoneId", "TimeZoneId", "CatalogVersion",
                     "ExpectedVersion", "ResultVersion", "ActorId",
                     "OccurredAtUtc")
                VALUES
                    ({TenantId}, {propertyId}, {operationId}, {revisionId},
                     {changeKind}, {requestedTimeZoneId},
                     {previousTimeZoneId}, {timeZoneId}, {CatalogVersion},
                     {expectedVersion}, {expectedVersion + 1},
                     'user:time-zone-operator', {occurredAtUtc});

                SET CONSTRAINTS ALL IMMEDIATE;
                """).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        Assert.Equal(
            timeZoneId,
            await ReadTimeZoneAsync(dbContext, propertyId)
                .ConfigureAwait(false));
        Assert.Equal(
            0,
            await CountTransactionProofsAsync(dbContext).ConfigureAwait(false));
    }

    private static Task<string> ReadTimeZoneAsync(
        PropertiesDbContext dbContext,
        Guid propertyId) =>
        dbContext.Database.SqlQuery<string>($"""
                SELECT "TimeZoneId" AS "Value"
                FROM properties.properties
                WHERE "ScopeId" = {TenantId}
                  AND "Id" = {propertyId}
                """).SingleAsync();

    private static Task<int> CountTransactionProofsAsync(
        PropertiesDbContext dbContext) =>
        dbContext.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::integer AS "Value"
                FROM properties.property_time_zone_transaction_proofs
                """).SingleAsync();

    private static async Task AssertRawMutationsRejectedAsync(
        PropertiesDbContext dbContext)
    {
        PostgresException update = await Assert.ThrowsAsync<
            PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE properties.property_time_zone_operations
                SET "ActorId" = 'user:changed'
                WHERE "ScopeId" = {TenantId}
                  AND "PropertyId" = {LegacyPropertyId}
                  AND "OperationId" = {TimeZoneOperationId};
                """));
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
            update.SqlState);

        PostgresException delete = await Assert.ThrowsAsync<
            PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM properties.property_time_zone_operations
                WHERE "ScopeId" = {TenantId}
                  AND "PropertyId" = {LegacyPropertyId}
                  AND "OperationId" = {TimeZoneOperationId};
                """));
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
            delete.SqlState);
    }

    private static async Task<Guid> SeedClosingDestructionAsync(
        PropertiesDbContext dbContext)
    {
        Guid operationId = Guid.Parse(
            "a4000000-0000-0000-0000-000000000002");
        await InsertClosingDestructionAsync(
                dbContext,
                operationId,
                stage: 12)
            .ConfigureAwait(false);
        return operationId;
    }

    private static Task<int> InsertClosingDestructionAsync(
        PropertiesDbContext dbContext,
        Guid operationId,
        int stage) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            WITH state AS (
                INSERT INTO properties.tenant_revisions
                    ("ScopeId", "Revision", "LifecycleStatus",
                     "DestroyOperationId", "DestroyRequestSha256",
                     "DestroyStartedAtUtc")
                VALUES
                    ({TenantId}, 2, 2, {operationId}, {Digest},
                     {OccurredAtUtc})
                ON CONFLICT ("ScopeId") DO UPDATE
                SET "Revision" =
                        properties.tenant_revisions."Revision" + 1,
                    "LifecycleStatus" = 2,
                    "DestroyOperationId" = {operationId},
                    "DestroyRequestSha256" = {Digest},
                    "DestroyStartedAtUtc" = {OccurredAtUtc}
                RETURNING "Revision")
            INSERT INTO properties.tenant_destroy_operations
                ("OperationId", "ScopeId", "RequestSha256",
                 "SelectedRevision", "ResultingRevision", "BatchSize",
                 "Stage", "RemovedRecordCount", "CompletedBatchCount",
                 "ProofVersion", "RemovalProofSha256", "StartedAtUtc",
                 "UpdatedAtUtc", "ConcurrencyVersion")
            SELECT
                {operationId}, {TenantId}, {Digest}, "Revision" - 1,
                "Revision", 100, {stage}, 0, 0, 1, {Digest},
                {OccurredAtUtc}, {OccurredAtUtc}, 1
            FROM state;
            """);

    private static async Task AssertMismatchedDestructionGucRejectedAsync(
        PropertiesDbContext dbContext)
    {
        await using IDbContextTransaction transaction = await dbContext.Database
            .BeginTransactionAsync()
            .ConfigureAwait(false);
        try
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT set_config('bunkfy.properties_tenant_destroy_operation_id', {Guid.NewGuid().ToString("D")}, true)")
                .ConfigureAwait(false);
            PostgresException failure = await Assert.ThrowsAsync<
                PostgresException>(() =>
                dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM properties.property_time_zone_operations
                    WHERE "ScopeId" = {TenantId}
                      AND "PropertyId" = {LegacyPropertyId}
                      AND "OperationId" = {TimeZoneOperationId};
                    """));
            Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
                failure.SqlState);
        }
        finally
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
    }

    private static async Task DeleteWithAuthorizedDestructionGucAsync(
        PropertiesDbContext dbContext,
        Guid destroyOperationId)
    {
        await using IDbContextTransaction transaction = await dbContext.Database
            .BeginTransactionAsync()
            .ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('bunkfy.properties_tenant_destroy_operation_id', {destroyOperationId.ToString("D")}, true)")
            .ConfigureAwait(false);
        Assert.Equal(
            5,
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM properties.property_time_zone_operations
                    WHERE "ScopeId" = {TenantId};
                    """).ConfigureAwait(false));
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static Task<int> RemoveClosingDestructionAsync(
        PropertiesDbContext dbContext,
        Guid operationId) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM properties.tenant_destroy_operations
            WHERE "OperationId" = {operationId};

            UPDATE properties.tenant_revisions
            SET "LifecycleStatus" = 1,
                "DestroyOperationId" = NULL,
                "DestroyRequestSha256" = NULL,
                "DestroyStartedAtUtc" = NULL
            WHERE "ScopeId" = {TenantId};
            """);

    private static PropertiesDbContext CreateDbContext(
        string connectionString)
    {
        DbContextOptions<PropertiesDbContext> options =
            new DbContextOptionsBuilder<PropertiesDbContext>()
                .UseNpgsql(
                    connectionString,
                    postgreSql => postgreSql
                        .MigrationsAssembly(
                            PropertiesMigrations.PostgreSqlAssembly)
                        .MigrationsHistoryTable(
                            PropertiesMigrations.HistoryTable,
                            PropertiesMigrations.Schema))
                .Options;
        return new(
            options,
            new TestScopeContext(),
            new NoTerminationFenceReader());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class NoTerminationFenceReader
        : IWorkspaceTerminationFenceReader
    {
        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<WorkspaceTerminationFenceSnapshot?>(null);
        }
    }
}
