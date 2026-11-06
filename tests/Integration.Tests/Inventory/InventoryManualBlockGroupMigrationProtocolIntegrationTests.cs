namespace Integration.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed partial class InventoryManualBlockGroupMigrationIntegrationTests
{
    private static readonly Guid UnitC =
        Guid.Parse("30000000-0000-0000-0000-000000000003");

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Deferred_graph_rejects_forged_membership_definition_state_and_lineage()
    {
        await using PostgreSqlContainer postgres = await StartAsync(
            "bunkfy_inventory_group_graph_attacks");
        string connectionString = postgres.GetConnectionString();
        await MigrateAsync(connectionString, PreviousMigration);
        await MigrateAsync(connectionString, CurrentMigration);
        await SeedUnitsAsync(connectionString, PropertyId, [UnitA, UnitB, UnitC]);

        Guid heterogeneousGroup =
            Guid.Parse("21000000-0000-0000-0000-000000000001");
        await AssertProtocolFailureAsync(
            () => ExecuteAsync(
                connectionString,
                "BEGIN;" + BuildNativeGroupSql(
                    heterogeneousGroup,
                    targetKind: 1,
                    buildingLabel: null,
                    floorLabel: null,
                    roomId: null,
                    inventoryUnitId: null,
                    [UnitA, UnitB],
                    [
                        Guid.Parse("41000000-0000-0000-0000-000000000001"),
                        Guid.Parse("41000000-0000-0000-0000-000000000002")
                    ],
                    CreatedAtUtc,
                    reason: "Maintenance",
                    childReasonOverride: "Forged child") +
                "COMMIT;"),
            "Inventory manual block-group graph is inconsistent");

        Guid timestampGroup =
            Guid.Parse("21000000-0000-0000-0000-000000000002");
        await AssertProtocolFailureAsync(
            () => ExecuteAsync(
                connectionString,
                "BEGIN;" + BuildNativeGroupSql(
                    timestampGroup,
                    targetKind: 1,
                    buildingLabel: null,
                    floorLabel: null,
                    roomId: null,
                    inventoryUnitId: null,
                    [UnitA, UnitB],
                    [
                        Guid.Parse("41000000-0000-0000-0000-000000000003"),
                        Guid.Parse("41000000-0000-0000-0000-000000000004")
                    ],
                    CreatedAtUtc,
                    childCreatedAtOverride: CreatedAtUtc.AddSeconds(1)) +
                "COMMIT;"),
            "Inventory manual block-group graph is inconsistent");

        Guid digestGroup =
            Guid.Parse("21000000-0000-0000-0000-000000000003");
        await AssertProtocolFailureAsync(
            () => ExecuteAsync(
                connectionString,
                "BEGIN;" + BuildNativeGroupSql(
                    digestGroup,
                    targetKind: 1,
                    buildingLabel: null,
                    floorLabel: null,
                    roomId: null,
                    inventoryUnitId: null,
                    [UnitA, UnitB],
                    [
                        Guid.Parse("41000000-0000-0000-0000-000000000005"),
                        Guid.Parse("41000000-0000-0000-0000-000000000006")
                    ],
                    CreatedAtUtc,
                    membershipDigestOverride: new string('f', 64)) +
                "COMMIT;"),
            "Inventory manual block-group graph is inconsistent");

        Guid countGroup =
            Guid.Parse("21000000-0000-0000-0000-000000000004");
        await AssertProtocolFailureAsync(
            () => ExecuteAsync(
                connectionString,
                "BEGIN;" + BuildNativeGroupSql(
                    countGroup,
                    targetKind: 1,
                    buildingLabel: null,
                    floorLabel: null,
                    roomId: null,
                    inventoryUnitId: null,
                    [UnitA, UnitB],
                    [
                        Guid.Parse("41000000-0000-0000-0000-000000000007"),
                        Guid.Parse("41000000-0000-0000-0000-000000000008")
                    ],
                    CreatedAtUtc,
                    initialCountOverride: 1) +
                "COMMIT;"),
            "Inventory manual block-group graph is inconsistent");

        Guid stateGroup =
            Guid.Parse("21000000-0000-0000-0000-000000000005");
        Guid stateBlockA =
            Guid.Parse("41000000-0000-0000-0000-000000000009");
        Guid stateBlockB =
            Guid.Parse("41000000-0000-0000-0000-000000000010");
        await ExecuteAsync(
            connectionString,
            "BEGIN;" + BuildNativeGroupSql(
                stateGroup,
                targetKind: 1,
                buildingLabel: null,
                floorLabel: null,
                roomId: null,
                inventoryUnitId: null,
                [UnitA, UnitB],
                [stateBlockA, stateBlockB],
                CreatedAtUtc) +
            "COMMIT;");
        DateTimeOffset stateChangedAt = CreatedAtUtc.AddMinutes(1);
        await AssertProtocolFailureAsync(
            () => ExecuteAsync(connectionString, $"""
                BEGIN;
                UPDATE inventory.manual_block_groups
                SET "ActiveBlockCount" = 0,
                    "State" = 3,
                    "Version" = 2,
                    "UpdatedAtUtc" = '{stateChangedAt:O}',
                    "ReleasedAtUtc" = '{stateChangedAt:O}',
                    "LastModifiedByActorId" = 'operator-b'
                WHERE "ScopeId" = '{Tenant}'
                  AND "PropertyId" = '{PropertyId}'
                  AND "Id" = '{stateGroup}';
                UPDATE inventory.manual_blocks
                SET "Status" = 2,
                    "Version" = 2,
                    "ReleasedAtUtc" = '{stateChangedAt:O}'
                WHERE "ScopeId" = '{Tenant}' AND "Id" = '{stateBlockA}';
                COMMIT;
                """),
            "Inventory manual block-group graph is inconsistent");

        Guid unitTargetGroup =
            Guid.Parse("21000000-0000-0000-0000-000000000006");
        await AssertProtocolFailureAsync(
            () => ExecuteAsync(
                connectionString,
                "BEGIN;" + BuildNativeGroupSql(
                    unitTargetGroup,
                    targetKind: 5,
                    buildingLabel: null,
                    floorLabel: null,
                    roomId: null,
                    inventoryUnitId: UnitA,
                    [UnitB],
                    [Guid.Parse("41000000-0000-0000-0000-000000000011")],
                    CreatedAtUtc) +
                "COMMIT;"),
            "Inventory manual block-group graph is inconsistent");

        Guid activePredecessor =
            Guid.Parse("21000000-0000-0000-0000-000000000007");
        await ExecuteAsync(
            connectionString,
            "BEGIN;" + BuildNativeGroupSql(
                activePredecessor,
                targetKind: 1,
                buildingLabel: null,
                floorLabel: null,
                roomId: null,
                inventoryUnitId: null,
                [UnitA],
                [Guid.Parse("41000000-0000-0000-0000-000000000012")],
                CreatedAtUtc) +
            "COMMIT;");
        Guid illegalSuccessor =
            Guid.Parse("21000000-0000-0000-0000-000000000008");
        await AssertProtocolFailureAsync(
            () => ExecuteAsync(
                connectionString,
                "BEGIN;" + BuildNativeGroupSql(
                    illegalSuccessor,
                    targetKind: 1,
                    buildingLabel: null,
                    floorLabel: null,
                    roomId: null,
                    inventoryUnitId: null,
                    [UnitC],
                    [Guid.Parse("41000000-0000-0000-0000-000000000013")],
                    CreatedAtUtc.AddMinutes(1),
                    replacesGroupId: activePredecessor) +
                "COMMIT;"),
            "Inventory manual block-group graph is inconsistent");
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task PostgreSql_membership_digest_matches_dotnet_ordinal_utf8_contract()
    {
        await using PostgreSqlContainer postgres = await StartAsync(
            "bunkfy_inventory_group_digest_utf8");
        string connectionString = postgres.GetConnectionString();
        await MigrateAsync(connectionString, PreviousMigration);
        await MigrateAsync(connectionString, CurrentMigration);
        await SeedUnitsAsync(connectionString, PropertyId, [UnitA, UnitB]);

        const string building = "North 🏨 𠀀";
        Guid groupId = Guid.Parse("22000000-0000-0000-0000-000000000001");
        string expected = ComputeMembershipDigest(
            2,
            building,
            null,
            null,
            null,
            [UnitA, UnitB]);
        await ExecuteAsync(
            connectionString,
            "BEGIN;" + BuildNativeGroupSql(
                groupId,
                targetKind: 2,
                buildingLabel: building,
                floorLabel: null,
                roomId: null,
                inventoryUnitId: null,
                [UnitA, UnitB],
                [
                    Guid.Parse("42000000-0000-0000-0000-000000000001"),
                    Guid.Parse("42000000-0000-0000-0000-000000000002")
                ],
                CreatedAtUtc) +
            "COMMIT;");

        Assert.Equal(
            expected,
            await ScalarAsync<string>(connectionString, $"""
                SELECT "MembershipDigest"
                FROM inventory.manual_block_groups
                WHERE "ScopeId" = '{Tenant}'
                  AND "PropertyId" = '{PropertyId}'
                  AND "Id" = '{groupId}';
            """));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task V2_receipts_are_bound_to_durable_group_lineage_counts_and_completion_times()
    {
        await using PostgreSqlContainer postgres = await StartAsync(
            "bunkfy_inventory_group_receipt_attacks");
        string connectionString = postgres.GetConnectionString();
        await MigrateAsync(connectionString, PreviousMigration);
        await MigrateAsync(connectionString, CurrentMigration);
        await SeedUnitsAsync(connectionString, PropertyId, [UnitA, UnitB, UnitC]);

        Guid createGroup =
            Guid.Parse("23000000-0000-0000-0000-000000000001");
        string createMembership = ComputeMembershipDigest(
            1, null, null, null, null, [UnitA]);
        await ExecuteAsync(
            connectionString,
            "BEGIN;" + BuildNativeGroupSql(
                createGroup,
                1,
                null,
                null,
                null,
                null,
                [UnitA],
                [Guid.Parse("43000000-0000-0000-0000-000000000001")],
                CreatedAtUtc) +
            "COMMIT;");
        await AssertProtocolFailureAsync(
            () => ExecuteAsync(connectionString, BuildV2ReceiptSql(
                Guid.Parse("53000000-0000-0000-0000-000000000001"),
                resourceKind: 2,
                resourceId: PropertyId,
                kind: 12,
                expectedVersion: 0,
                resultGroupId: createGroup,
                resultStatus: 1,
                resultVersion: 1,
                affectedCount: 1,
                previousGroupId: null,
                totalCount: 1,
                activeCount: 1,
                releasedCount: 0,
                alreadyReleasedCount: 0,
                createdCount: 1,
                membershipDigest: createMembership,
                completedAtUtc: CreatedAtUtc.AddSeconds(1))),
            "Inventory block-group create receipt has invalid lineage");
        await ExecuteAsync(connectionString, BuildV2ReceiptSql(
            Guid.Parse("53000000-0000-0000-0000-000000000002"),
            2,
            PropertyId,
            12,
            0,
            createGroup,
            1,
            1,
            1,
            null,
            1,
            1,
            0,
            0,
            1,
            createMembership,
            CreatedAtUtc));

        Guid predecessor =
            Guid.Parse("23000000-0000-0000-0000-000000000002");
        Guid predecessorBlockA =
            Guid.Parse("43000000-0000-0000-0000-000000000002");
        Guid predecessorBlockB =
            Guid.Parse("43000000-0000-0000-0000-000000000003");
        await ExecuteAsync(
            connectionString,
            "BEGIN;" + BuildNativeGroupSql(
                predecessor,
                1,
                null,
                null,
                null,
                null,
                [UnitA, UnitB],
                [predecessorBlockA, predecessorBlockB],
                CreatedAtUtc) +
            "COMMIT;");
        Guid successor =
            Guid.Parse("23000000-0000-0000-0000-000000000003");
        DateTimeOffset replacedAt = CreatedAtUtc.AddMinutes(5);
        string successorMembership = ComputeMembershipDigest(
            1, null, null, null, null, [UnitC]);
        await ExecuteAsync(
            connectionString,
            $"""
            BEGIN;
            UPDATE inventory.manual_blocks
            SET "Status" = 2,
                "Version" = 2,
                "ReleasedAtUtc" = '{replacedAt:O}'
            WHERE "ScopeId" = '{Tenant}'
              AND "PropertyId" = '{PropertyId}'
              AND "BlockGroupId" = '{predecessor}';
            UPDATE inventory.manual_block_groups
            SET "ActiveBlockCount" = 0,
                "State" = 4,
                "Version" = 2,
                "UpdatedAtUtc" = '{replacedAt:O}',
                "ReleasedAtUtc" = '{replacedAt:O}',
                "LastModifiedByActorId" = 'operator-b'
            WHERE "ScopeId" = '{Tenant}'
              AND "PropertyId" = '{PropertyId}'
              AND "Id" = '{predecessor}';
            """ + BuildNativeGroupSql(
                successor,
                1,
                null,
                null,
                null,
                null,
                [UnitC],
                [Guid.Parse("43000000-0000-0000-0000-000000000004")],
                replacedAt,
                replacesGroupId: predecessor) +
            "COMMIT;");

        await AssertProtocolFailureAsync(
            () => ExecuteAsync(connectionString, BuildV2ReceiptSql(
                Guid.Parse("53000000-0000-0000-0000-000000000003"),
                4,
                predecessor,
                13,
                expectedVersion: 2,
                successor,
                1,
                1,
                affectedCount: 3,
                predecessor,
                1,
                1,
                2,
                0,
                1,
                successorMembership,
                replacedAt)),
            "Inventory block-group replace receipt has invalid lineage");
        await AssertProtocolFailureAsync(
            () => ExecuteAsync(connectionString, BuildV2ReceiptSql(
                Guid.Parse("53000000-0000-0000-0000-000000000004"),
                4,
                predecessor,
                13,
                1,
                successor,
                1,
                1,
                affectedCount: 2,
                predecessor,
                1,
                1,
                releasedCount: 1,
                alreadyReleasedCount: 0,
                createdCount: 1,
                successorMembership,
                replacedAt)),
            "Inventory block-group replace receipt has invalid lineage");
        await AssertProtocolFailureAsync(
            () => ExecuteAsync(connectionString, BuildV2ReceiptSql(
                Guid.Parse("53000000-0000-0000-0000-000000000005"),
                4,
                predecessor,
                13,
                1,
                successor,
                1,
                1,
                3,
                predecessor,
                1,
                1,
                2,
                0,
                1,
                successorMembership,
                replacedAt.AddSeconds(1))),
            "Inventory block-group replace receipt has invalid lineage");
        await AssertProtocolFailureAsync(
            () => ExecuteAsync(connectionString, BuildV2ReceiptSql(
                Guid.Parse("53000000-0000-0000-0000-000000000006"),
                4,
                predecessor,
                13,
                1,
                successor,
                1,
                1,
                3,
                predecessor,
                1,
                1,
                2,
                0,
                1,
                new string('b', 64),
                replacedAt)),
            "Inventory block-group operation receipt does not match durable group evidence");
        await ExecuteAsync(connectionString, BuildV2ReceiptSql(
            Guid.Parse("53000000-0000-0000-0000-000000000007"),
            4,
            predecessor,
            13,
            1,
            successor,
            1,
            1,
            3,
            predecessor,
            1,
            1,
            2,
            0,
            1,
            successorMembership,
            replacedAt));

        Guid releasedGroup =
            Guid.Parse("23000000-0000-0000-0000-000000000004");
        Guid releasedBlock =
            Guid.Parse("43000000-0000-0000-0000-000000000005");
        DateTimeOffset releasedAt = CreatedAtUtc.AddMinutes(10);
        string releasedMembership = ComputeMembershipDigest(
            1, null, null, null, null, [UnitB]);
        await ExecuteAsync(
            connectionString,
            "BEGIN;" + BuildNativeGroupSql(
                releasedGroup,
                1,
                null,
                null,
                null,
                null,
                [UnitB],
                [releasedBlock],
                CreatedAtUtc) + $"""
            UPDATE inventory.manual_blocks
            SET "Status" = 2,
                "Version" = 2,
                "ReleasedAtUtc" = '{releasedAt:O}'
            WHERE "ScopeId" = '{Tenant}' AND "Id" = '{releasedBlock}';
            UPDATE inventory.manual_block_groups
            SET "ActiveBlockCount" = 0,
                "State" = 3,
                "Version" = 2,
                "UpdatedAtUtc" = '{releasedAt:O}',
                "ReleasedAtUtc" = '{releasedAt:O}',
                "LastModifiedByActorId" = 'operator-b'
            WHERE "ScopeId" = '{Tenant}'
              AND "PropertyId" = '{PropertyId}'
              AND "Id" = '{releasedGroup}';
            COMMIT;
            """);
        await AssertProtocolFailureAsync(
            () => ExecuteAsync(connectionString, BuildV2ReceiptSql(
                Guid.Parse("53000000-0000-0000-0000-000000000008"),
                4,
                releasedGroup,
                14,
                1,
                releasedGroup,
                3,
                2,
                1,
                null,
                1,
                0,
                1,
                0,
                0,
                releasedMembership,
                releasedAt.AddSeconds(1))),
            "Inventory block-group release receipt does not match durable completion evidence");
        await ExecuteAsync(connectionString, BuildV2ReceiptSql(
            Guid.Parse("53000000-0000-0000-0000-000000000009"),
            4,
            releasedGroup,
            14,
            1,
            releasedGroup,
            3,
            2,
            1,
            null,
            1,
            0,
            1,
            0,
            0,
            releasedMembership,
            releasedAt));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Five_hundred_member_create_and_release_are_atomic_bounded_and_cardinality_exact()
    {
        await using PostgreSqlContainer postgres = await StartAsync(
            "bunkfy_inventory_group_maximum_atomic");
        string connectionString = postgres.GetConnectionString();
        await MigrateAsync(connectionString, PreviousMigration);
        await MigrateAsync(connectionString, CurrentMigration);
        Guid groupId = Guid.Parse("24000000-0000-0000-0000-000000000001");
        DateTimeOffset releasedAt = CreatedAtUtc.AddMinutes(15);

        Stopwatch createTimer = Stopwatch.StartNew();
        await ExecuteAsync(connectionString, $"""
            BEGIN;
            INSERT INTO inventory.property_topology (
                "Id", "Name", "Code", "TimeZoneId", "Status",
                "SourceVersion", "DetailsVersion", "IsKnown",
                "AvailabilitySelectionVersion", "ScopeId")
            VALUES (
                '{PropertyId}', 'Maximum property', 'MAXIMUM', 'UTC', 1,
                1, 1, true, 1, '{Tenant}');
            CREATE TEMPORARY TABLE candidate_manual_blocks ON COMMIT DROP AS
            SELECT number,
                   md5('maximum-unit-' || number::text)::uuid AS unit_id,
                   md5('maximum-block-' || number::text)::uuid AS block_id
            FROM generate_series(1, 500) AS number;

            INSERT INTO inventory.inventory_units (
                "Id", "PropertyId", "RoomId", "BedId", "Kind", "Label",
                "IsTopologyActive", "SourceVersion", "DetailsVersion",
                "IsKnown", "AvailabilityMutationVersion", "ScopeId")
            SELECT candidate.unit_id, '{PropertyId}', candidate.unit_id, NULL,
                   1, 'Maximum ' || candidate.number::text,
                   true, 1, 1, true, 1, '{Tenant}'
            FROM candidate_manual_blocks candidate;

            WITH canonical AS (
                SELECT string_agg(
                    lower(replace(candidate.unit_id::text, '-', '')),
                    ',' ORDER BY
                        lower(replace(candidate.unit_id::text, '-', ''))
                            COLLATE "C") AS members
                FROM candidate_manual_blocks candidate
            )
            INSERT INTO inventory.manual_block_groups (
                "Id", "ScopeId", "PropertyId", "TargetKind",
                "Arrival", "Departure", "Reason", "SelectionDigest",
                "MembershipDigest", "MembershipDigestVersion",
                "InitialBlockCount", "ActiveBlockCount", "State", "Version",
                "CreatedAtUtc", "CreatedByActorId", "LastModifiedByActorId")
            SELECT '{groupId}', '{Tenant}', '{PropertyId}', 1,
                   '{Arrival:yyyy-MM-dd}', '{Departure:yyyy-MM-dd}',
                   'Maximum atomic maintenance', '{new string('a', 64)}',
                   encode(sha256(convert_to(
                       'bunkfy-inventory-manual-block-group-membership/v1' ||
                       '|kind=1|building=0:|floor=0:|room=|unit=' ||
                       '|arrival={Arrival:yyyy-MM-dd}' ||
                       '|departure={Departure:yyyy-MM-dd}' ||
                       '|members=500|' || canonical.members,
                       'UTF8')), 'hex'),
                   1, 500, 500, 1, 1, '{CreatedAtUtc:O}',
                   'operator-a', 'operator-a'
            FROM canonical;

            INSERT INTO inventory.manual_blocks (
                "Id", "PropertyId", "InventoryUnitId", "Arrival", "Departure",
                "Reason", "Status", "Version", "CreatedAtUtc", "ReleasedAtUtc",
                "ScopeId", "BlockGroupId")
            SELECT candidate.block_id, '{PropertyId}', candidate.unit_id,
                   '{Arrival:yyyy-MM-dd}', '{Departure:yyyy-MM-dd}',
                   'Maximum atomic maintenance', 1, 1, '{CreatedAtUtc:O}', NULL,
                   '{Tenant}', '{groupId}'
            FROM candidate_manual_blocks candidate;
            COMMIT;
            """);
        createTimer.Stop();
        Assert.True(
            createTimer.Elapsed < TimeSpan.FromSeconds(15),
            $"500-member atomic create took {createTimer.Elapsed}.");
        Assert.Equal(500L, await ScalarAsync<long>(connectionString, $"""
            SELECT count(*)
            FROM inventory.manual_blocks
            WHERE "ScopeId" = '{Tenant}' AND "BlockGroupId" = '{groupId}';
            """));
        Assert.Equal("500:500:1:1", await ScalarAsync<string>(connectionString, $"""
            SELECT "InitialBlockCount"::text || ':' ||
                   "ActiveBlockCount"::text || ':' ||
                   "State"::text || ':' || "Version"::text
            FROM inventory.manual_block_groups
            WHERE "ScopeId" = '{Tenant}' AND "Id" = '{groupId}';
            """));

        Stopwatch releaseTimer = Stopwatch.StartNew();
        await ExecuteAsync(connectionString, $"""
            BEGIN;
            UPDATE inventory.manual_blocks
            SET "Status" = 2,
                "Version" = 2,
                "ReleasedAtUtc" = '{releasedAt:O}'
            WHERE "ScopeId" = '{Tenant}'
              AND "PropertyId" = '{PropertyId}'
              AND "BlockGroupId" = '{groupId}';
            UPDATE inventory.manual_block_groups
            SET "ActiveBlockCount" = 0,
                "State" = 3,
                "Version" = 2,
                "UpdatedAtUtc" = '{releasedAt:O}',
                "ReleasedAtUtc" = '{releasedAt:O}',
                "LastModifiedByActorId" = 'operator-b'
            WHERE "ScopeId" = '{Tenant}'
              AND "PropertyId" = '{PropertyId}'
              AND "Id" = '{groupId}';
            COMMIT;
            """);
        releaseTimer.Stop();
        Assert.True(
            releaseTimer.Elapsed < TimeSpan.FromSeconds(15),
            $"500-member atomic release took {releaseTimer.Elapsed}.");
        Assert.Equal(500L, await ScalarAsync<long>(connectionString, $"""
            SELECT count(*)
            FROM inventory.manual_blocks
            WHERE "ScopeId" = '{Tenant}' AND "BlockGroupId" = '{groupId}'
              AND "Status" = 2 AND "ReleasedAtUtc" = '{releasedAt:O}';
            """));
        Assert.Equal("500:0:3:2", await ScalarAsync<string>(connectionString, $"""
            SELECT "InitialBlockCount"::text || ':' ||
                   "ActiveBlockCount"::text || ':' ||
                   "State"::text || ':' || "Version"::text
            FROM inventory.manual_block_groups
            WHERE "ScopeId" = '{Tenant}' AND "Id" = '{groupId}';
            """));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Upgrade_refuses_active_group_above_limit_but_preserves_released_legacy_truth()
    {
        await using PostgreSqlContainer postgres = await StartAsync(
            "bunkfy_inventory_group_legacy_above_limit");
        string connectionString = postgres.GetConnectionString();
        await MigrateAsync(connectionString, PreviousMigration);
        Guid legacyGroup =
            Guid.Parse("25000000-0000-0000-0000-000000000001");
        Guid conflictingUnitA =
            Guid.Parse("35000000-0000-0000-0000-000000000001");
        Guid conflictingUnitB =
            Guid.Parse("35000000-0000-0000-0000-000000000002");
        await ExecuteAsync(connectionString, $"""
            INSERT INTO inventory.inventory_units (
                "Id", "PropertyId", "RoomId", "BedId", "Kind", "Label",
                "IsTopologyActive", "SourceVersion", "DetailsVersion",
                "IsKnown", "AvailabilityMutationVersion", "ScopeId")
            VALUES
                ('{conflictingUnitA}', '{PropertyId}', '{conflictingUnitA}', NULL,
                 1, 'First tenant unit', true, 1, 1, true, 1, '{Tenant}'),
                ('{conflictingUnitB}', '{PropertyId}', '{conflictingUnitB}', NULL,
                 1, 'Conflicting tenant unit', true, 1, 1, true, 1, 'tenant-b');
            """);
        await AssertProtocolFailureAsync(
            () => MigrateAsync(connectionString, CurrentMigration),
            "conflicting Inventory property coordinates prevent block-group upgrade");
        await ExecuteAsync(connectionString, $"""
            DELETE FROM inventory.inventory_units
            WHERE "Id" IN ('{conflictingUnitA}', '{conflictingUnitB}');
            """);

        await ExecuteAsync(connectionString, $"""
            WITH candidates AS (
                SELECT number,
                       md5('legacy-large-unit-' || number::text)::uuid AS unit_id,
                       md5('legacy-large-block-' || number::text)::uuid AS block_id
                FROM generate_series(1, 501) AS number
            )
            INSERT INTO inventory.inventory_units (
                "Id", "PropertyId", "RoomId", "BedId", "Kind", "Label",
                "IsTopologyActive", "SourceVersion", "DetailsVersion",
                "IsKnown", "AvailabilityMutationVersion", "ScopeId")
            SELECT candidate.unit_id, '{PropertyId}', candidate.unit_id, NULL,
                   1, 'Legacy ' || candidate.number::text,
                   true, 1, 1, true, 1, '{Tenant}'
            FROM candidates candidate;

            WITH candidates AS (
                SELECT number,
                       md5('legacy-large-unit-' || number::text)::uuid AS unit_id,
                       md5('legacy-large-block-' || number::text)::uuid AS block_id
                FROM generate_series(1, 501) AS number
            )
            INSERT INTO inventory.manual_blocks (
                "Id", "PropertyId", "InventoryUnitId", "Arrival", "Departure",
                "Reason", "Status", "Version", "CreatedAtUtc", "ReleasedAtUtc",
                "ScopeId", "BlockGroupId")
            SELECT candidate.block_id, '{PropertyId}', candidate.unit_id,
                   '{Arrival:yyyy-MM-dd}', '{Departure:yyyy-MM-dd}',
                   'Historical maintenance', 1, 1, '{CreatedAtUtc:O}', NULL,
                   '{Tenant}', '{legacyGroup}'
            FROM candidates candidate;
            """);

        await AssertProtocolFailureAsync(
            () => MigrateAsync(connectionString, CurrentMigration),
            "active legacy Inventory block group exceeds 500 members");
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
            SELECT count(*)
            FROM information_schema.tables
            WHERE table_schema = 'inventory'
              AND table_name = 'manual_block_groups';
            """));

        DateTimeOffset releasedAt = CreatedAtUtc.AddHours(1);
        await ExecuteAsync(connectionString, $"""
            UPDATE inventory.manual_blocks
            SET "Status" = 2,
                "Version" = 2,
                "ReleasedAtUtc" = '{releasedAt:O}'
            WHERE "ScopeId" = '{Tenant}' AND "BlockGroupId" = '{legacyGroup}';
            """);
        await MigrateAsync(connectionString, CurrentMigration);
        Assert.Equal("501:0:3:1", await ScalarAsync<string>(connectionString, $"""
            SELECT "InitialBlockCount"::text || ':' ||
                   "ActiveBlockCount"::text || ':' ||
                   "State"::text || ':' || "Version"::text
            FROM inventory.manual_block_groups
            WHERE "ScopeId" = '{Tenant}' AND "Id" = '{legacyGroup}';
            """));
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, $"""
            SELECT count(*)
            FROM inventory.manual_block_groups
            WHERE "ScopeId" = '{Tenant}' AND "Id" = '{legacyGroup}'
              AND "ReleasedAtUtc" = '{releasedAt:O}';
            """));

        await MigrateAsync(connectionString, PreviousMigration);
        Assert.Equal(501L, await ScalarAsync<long>(connectionString, $"""
            SELECT count(*)
            FROM inventory.manual_blocks
            WHERE "ScopeId" = '{Tenant}' AND "BlockGroupId" = '{legacyGroup}'
              AND "Status" = 2 AND "ReleasedAtUtc" = '{releasedAt:O}';
            """));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Up_and_down_fail_fast_when_protocol_tables_are_contended()
    {
        await using PostgreSqlContainer postgres = await StartAsync(
            "bunkfy_inventory_group_lock_contention");
        string connectionString = postgres.GetConnectionString();
        await MigrateAsync(connectionString, PreviousMigration);

        await using (NpgsqlConnection blocker = new(connectionString))
        {
            await blocker.OpenAsync();
            await using NpgsqlTransaction transaction =
                await blocker.BeginTransactionAsync();
            await using NpgsqlCommand command = new(
                "LOCK TABLE inventory.manual_blocks IN ACCESS EXCLUSIVE MODE;",
                blocker,
                transaction);
            await command.ExecuteNonQueryAsync();
            await AssertPostgresSqlStateAsync(
                () => MigrateAsync(connectionString, CurrentMigration),
                PostgresErrorCodes.LockNotAvailable);
            await transaction.RollbackAsync();
        }

        await MigrateAsync(connectionString, CurrentMigration);
        await using (NpgsqlConnection blocker = new(connectionString))
        {
            await blocker.OpenAsync();
            await using NpgsqlTransaction transaction =
                await blocker.BeginTransactionAsync();
            await using NpgsqlCommand command = new(
                "LOCK TABLE inventory.manual_block_groups IN ACCESS EXCLUSIVE MODE;",
                blocker,
                transaction);
            await command.ExecuteNonQueryAsync();
            await AssertPostgresSqlStateAsync(
                () => MigrateAsync(connectionString, PreviousMigration),
                PostgresErrorCodes.LockNotAvailable);
            await transaction.RollbackAsync();
        }

        await MigrateAsync(connectionString, PreviousMigration);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Down_reports_exact_stage_twenty_one_and_v2_history_refusals()
    {
        await using PostgreSqlContainer postgres = await StartAsync(
            "bunkfy_inventory_group_down_refusals");
        string connectionString = postgres.GetConnectionString();
        await MigrateAsync(connectionString, PreviousMigration);
        await SeedLegacyTopologyAndBlocksAsync(connectionString);
        await MigrateAsync(connectionString, CurrentMigration);

        Guid destroyOperation =
            Guid.Parse("56000000-0000-0000-0000-000000000001");
        await ExecuteAsync(connectionString, $"""
            INSERT INTO inventory.tenant_destroy_operations (
                "OperationId", "ScopeId", "RequestSha256",
                "SelectedRevision", "ResultingRevision", "BatchSize", "Stage",
                "RemovedRecordCount", "CompletedBatchCount", "ProofVersion",
                "RemovalProofSha256", "StartedAtUtc", "UpdatedAtUtc",
                "ConcurrencyVersion")
            VALUES (
                '{destroyOperation}', 'stage-21-scope', '{new string('a', 64)}',
                0, 1, 100, 21, 0, 0, 1, '{new string('b', 64)}',
                '{CreatedAtUtc:O}', '{CreatedAtUtc:O}', 1);
            """);
        await AssertProtocolFailureAsync(
            () => MigrateAsync(connectionString, PreviousMigration),
            "Inventory tenant destruction stage 21 prevents block-group downgrade");
        await ExecuteAsync(connectionString, $"""
            DELETE FROM inventory.tenant_destroy_operations
            WHERE "OperationId" = '{destroyOperation}';
            """);

        string legacyMembership = await ScalarAsync<string>(
            connectionString,
            $"""
            SELECT "MembershipDigest"
            FROM inventory.manual_block_groups
            WHERE "ScopeId" = '{Tenant}'
              AND "PropertyId" = '{PropertyId}'
              AND "Id" = '{GroupId}';
            """);
        await ExecuteAsync(connectionString, BuildV2ReceiptSql(
            Guid.Parse("56000000-0000-0000-0000-000000000002"),
            resourceKind: 4,
            resourceId: GroupId,
            kind: 13,
            expectedVersion: 1,
            resultGroupId: GroupId,
            resultStatus: 2,
            resultVersion: 1,
            affectedCount: 0,
            previousGroupId: GroupId,
            totalCount: 2,
            activeCount: 1,
            releasedCount: 0,
            alreadyReleasedCount: 1,
            createdCount: 0,
            membershipDigest: legacyMembership,
            completedAtUtc: CreatedAtUtc.AddHours(2)));
        await AssertProtocolFailureAsync(
            () => MigrateAsync(connectionString, PreviousMigration),
            "Cannot downgrade Inventory while manual block-group convergence history exists");
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Selection_affecting_provider_writes_require_one_legal_property_epoch_advance()
    {
        await using PostgreSqlContainer postgres = await StartAsync(
            "bunkfy_inventory_selection_epoch_protocol");
        string connectionString = postgres.GetConnectionString();
        await MigrateAsync(connectionString, PreviousMigration);
        await MigrateAsync(connectionString, CurrentMigration);
        Guid propertyId =
            Guid.Parse("17000000-0000-0000-0000-000000000001");
        Guid roomId =
            Guid.Parse("37000000-0000-0000-0000-000000000001");
        Guid unitId =
            Guid.Parse("38000000-0000-0000-0000-000000000001");
        Guid bedRetirementId =
            Guid.Parse("58000000-0000-0000-0000-000000000001");
        await ExecuteAsync(connectionString, $"""
            INSERT INTO inventory.property_topology (
                "Id", "Name", "Code", "TimeZoneId", "Status",
                "SourceVersion", "DetailsVersion", "IsKnown",
                "AvailabilitySelectionVersion", "ScopeId")
            VALUES (
                '{propertyId}', 'Epoch property', 'EPOCH', 'UTC', 1,
                1, 1, true, 1, '{Tenant}');
            """);

        await AssertProtocolFailureAsync(
            () => ExecuteAsync(connectionString, $"""
                INSERT INTO inventory.room_topology (
                    "Id", "PropertyId", "Name", "BuildingLabel", "FloorLabel",
                    "Status", "SourceVersion", "DetailsVersion", "IsKnown",
                    "ScopeId")
                VALUES (
                    '{roomId}', '{propertyId}', 'Epoch room', NULL, NULL,
                    1, 1, 1, true, '{Tenant}');
                """),
            "Inventory selection-affecting write requires an advanced property epoch");
        await ExecuteAsync(connectionString, $"""
            BEGIN;
            UPDATE inventory.property_topology
            SET "AvailabilitySelectionVersion" = 2
            WHERE "ScopeId" = '{Tenant}' AND "Id" = '{propertyId}';
            INSERT INTO inventory.room_topology (
                "Id", "PropertyId", "Name", "BuildingLabel", "FloorLabel",
                "Status", "SourceVersion", "DetailsVersion", "IsKnown",
                "ScopeId")
            VALUES (
                '{roomId}', '{propertyId}', 'Epoch room', NULL, NULL,
                1, 1, 1, true, '{Tenant}');
            INSERT INTO inventory.inventory_units (
                "Id", "PropertyId", "RoomId", "BedId", "Kind", "Label",
                "IsTopologyActive", "SourceVersion", "DetailsVersion",
                "IsKnown", "AvailabilityMutationVersion", "ScopeId")
            VALUES (
                '{unitId}', '{propertyId}', '{roomId}', NULL, 1, 'Epoch room',
                true, 1, 1, true, 1, '{Tenant}');
            INSERT INTO inventory.room_configurations (
                "Id", "PropertyId", "SalesMode", "Version",
                "AvailabilityMutationVersion", "CreatedAtUtc", "UpdatedAtUtc",
                "ScopeId")
            VALUES (
                '{roomId}', '{propertyId}', 2, 1, 1,
                '{CreatedAtUtc:O}', NULL, '{Tenant}');
            INSERT INTO inventory.bed_retirements (
                "Id", "PropertyId", "RoomId", "BedId", "Reason",
                "RequestedBy", "State", "Version", "CreatedAtUtc",
                "ScopeId")
            VALUES (
                '{bedRetirementId}', '{propertyId}', '{roomId}', '{unitId}',
                'Retire epoch unit', 'operator-a', 1, 1,
                '{CreatedAtUtc:O}', '{Tenant}');
            COMMIT;
            """);

        await AssertProtocolFailureAsync(
            () => ExecuteAsync(connectionString, $"""
                UPDATE inventory.property_topology
                SET "AvailabilitySelectionVersion" =
                    "AvailabilitySelectionVersion"
                WHERE "ScopeId" = '{Tenant}' AND "Id" = '{propertyId}';
                """),
            "Inventory property topology no-op updates are forbidden");
        await AssertProtocolFailureAsync(
            () => ExecuteAsync(connectionString, $"""
                UPDATE inventory.property_topology
                SET "AvailabilitySelectionVersion" = 4
                WHERE "ScopeId" = '{Tenant}' AND "Id" = '{propertyId}';
                """),
            "Inventory property availability-selection transition is invalid");
        await AssertProtocolFailureAsync(
            () => ExecuteAsync(connectionString, $"""
                UPDATE inventory.room_configurations
                SET "SalesMode" = 3,
                    "Version" = 2,
                    "AvailabilityMutationVersion" = 2,
                    "UpdatedAtUtc" = '{CreatedAtUtc.AddMinutes(1):O}'
                WHERE "ScopeId" = '{Tenant}' AND "Id" = '{roomId}';
                """),
            "Inventory selection-affecting write requires an advanced property epoch");

        await ExecuteAsync(connectionString, $"""
            UPDATE inventory.room_configurations
            SET "AvailabilityMutationVersion" = 2
            WHERE "ScopeId" = '{Tenant}' AND "Id" = '{roomId}';
            UPDATE inventory.inventory_units
            SET "AvailabilityMutationVersion" = 2
            WHERE "ScopeId" = '{Tenant}' AND "Id" = '{unitId}';
            UPDATE inventory.bed_retirements
            SET "State" = 2,
                "Version" = 2,
                "UpdatedAtUtc" = '{CreatedAtUtc.AddMinutes(1):O}'
            WHERE "ScopeId" = '{Tenant}' AND "Id" = '{bedRetirementId}';
            """);
        Assert.Equal(2L, await ScalarAsync<long>(connectionString, $"""
            SELECT "AvailabilitySelectionVersion"
            FROM inventory.property_topology
            WHERE "ScopeId" = '{Tenant}' AND "Id" = '{propertyId}';
            """));

        await AssertProtocolFailureAsync(
            () => ExecuteAsync(connectionString, $"""
                UPDATE inventory.bed_retirements
                SET "State" = 4,
                    "Version" = 3,
                    "UpdatedAtUtc" = '{CreatedAtUtc.AddMinutes(2):O}',
                    "CompletedAtUtc" = '{CreatedAtUtc.AddMinutes(2):O}'
                WHERE "ScopeId" = '{Tenant}' AND "Id" = '{bedRetirementId}';
                """),
            "Inventory selection-affecting write requires an advanced property epoch");
        await ExecuteAsync(connectionString, $"""
            BEGIN;
            UPDATE inventory.property_topology
            SET "AvailabilitySelectionVersion" = 3
            WHERE "ScopeId" = '{Tenant}' AND "Id" = '{propertyId}';
            UPDATE inventory.bed_retirements
            SET "State" = 4,
                "Version" = 3,
                "UpdatedAtUtc" = '{CreatedAtUtc.AddMinutes(2):O}',
                "CompletedAtUtc" = '{CreatedAtUtc.AddMinutes(2):O}'
            WHERE "ScopeId" = '{Tenant}' AND "Id" = '{bedRetirementId}';
            COMMIT;
            """);
        Assert.Equal(3L, await ScalarAsync<long>(connectionString, $"""
            SELECT "AvailabilitySelectionVersion"
            FROM inventory.property_topology
            WHERE "ScopeId" = '{Tenant}' AND "Id" = '{propertyId}';
            """));
    }

    private static async Task AssertProtocolFailureAsync(
        Func<Task> action,
        string expectedMessage)
    {
        PostgresException exception =
            await Assert.ThrowsAsync<PostgresException>(action);
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
        Assert.Equal(expectedMessage, exception.MessageText);
    }

    private static async Task AssertPostgresSqlStateAsync(
        Func<Task> action,
        string expectedSqlState)
    {
        Exception exception = await Assert.ThrowsAnyAsync<Exception>(action);
        PostgresException? providerException = exception as PostgresException ??
            exception.InnerException as PostgresException ??
            exception.GetBaseException() as PostgresException;
        Assert.NotNull(providerException);
        Assert.Equal(expectedSqlState, providerException.SqlState);
    }

    private static async Task SeedUnitsAsync(
        string connectionString,
        Guid propertyId,
        IReadOnlyCollection<Guid> unitIds)
    {
        string values = string.Join(
            ",\n",
            unitIds.Select(unitId => $"""
                ('{unitId}', '{propertyId}', '{unitId}', NULL, 1,
                 'Unit {unitId:N}', true, 1, 1, true, 1, '{Tenant}')
                """));
        await ExecuteAsync(connectionString, $"""
            INSERT INTO inventory.property_topology (
                "Id", "Name", "Code", "TimeZoneId", "Status",
                "SourceVersion", "DetailsVersion", "IsKnown",
                "AvailabilitySelectionVersion", "ScopeId")
            VALUES (
                '{propertyId}', 'Migration test property',
                'TEST-{propertyId.ToString("N")[..8]}', 'UTC', 1,
                1, 1, true, 1, '{Tenant}')
            ON CONFLICT ("Id") DO UPDATE
            SET "AvailabilitySelectionVersion" =
                inventory.property_topology."AvailabilitySelectionVersion" + 1;
            INSERT INTO inventory.inventory_units (
                "Id", "PropertyId", "RoomId", "BedId", "Kind", "Label",
                "IsTopologyActive", "SourceVersion", "DetailsVersion",
                "IsKnown", "AvailabilityMutationVersion", "ScopeId")
            VALUES {values};
            """);
    }

    private static string BuildNativeGroupSql(
        Guid groupId,
        int targetKind,
        string? buildingLabel,
        string? floorLabel,
        Guid? roomId,
        Guid? inventoryUnitId,
        IReadOnlyList<Guid> unitIds,
        IReadOnlyList<Guid> blockIds,
        DateTimeOffset createdAtUtc,
        string reason = "Maintenance",
        Guid? replacesGroupId = null,
        string? membershipDigestOverride = null,
        int? initialCountOverride = null,
        string? childReasonOverride = null,
        DateTimeOffset? childCreatedAtOverride = null)
    {
        Assert.Equal(unitIds.Count, blockIds.Count);
        string membershipDigest = membershipDigestOverride ??
            ComputeMembershipDigest(
                targetKind,
                buildingLabel,
                floorLabel,
                roomId,
                inventoryUnitId,
                unitIds);
        int initialCount = initialCountOverride ?? unitIds.Count;
        string groupInsert = $"""
            INSERT INTO inventory.manual_block_groups (
                "Id", "ScopeId", "PropertyId", "TargetKind",
                "BuildingLabel", "FloorLabel", "RoomId", "InventoryUnitId",
                "Arrival", "Departure", "Reason", "SelectionDigest",
                "MembershipDigest", "MembershipDigestVersion",
                "InitialBlockCount", "ActiveBlockCount", "State", "Version",
                "ReplacesGroupId", "CreatedAtUtc", "UpdatedAtUtc",
                "ReleasedAtUtc", "CreatedByActorId", "LastModifiedByActorId")
            VALUES (
                '{groupId}', '{Tenant}', '{PropertyId}', {targetKind},
                {SqlLiteral(buildingLabel)}, {SqlLiteral(floorLabel)},
                {SqlUuid(roomId)}, {SqlUuid(inventoryUnitId)},
                '{Arrival:yyyy-MM-dd}', '{Departure:yyyy-MM-dd}',
                {SqlLiteral(reason)}, '{new string('a', 64)}',
                '{membershipDigest}', 1,
                {initialCount}, {initialCount}, 1, 1,
                {SqlUuid(replacesGroupId)}, '{createdAtUtc:O}', NULL, NULL,
                'operator-a', 'operator-a');
            """;
        string childValues = string.Join(
            ",\n",
            unitIds.Select((unitId, index) => $"""
                ('{blockIds[index]}', '{PropertyId}', '{unitId}',
                 '{Arrival:yyyy-MM-dd}', '{Departure:yyyy-MM-dd}',
                 {SqlLiteral(index == unitIds.Count - 1 && childReasonOverride is not null
                     ? childReasonOverride
                     : reason)},
                 1, 1,
                 '{(index == unitIds.Count - 1 && childCreatedAtOverride.HasValue
                     ? childCreatedAtOverride.Value
                     : createdAtUtc):O}',
                 NULL, '{Tenant}', '{groupId}')
                """));
        return groupInsert + $"""
            INSERT INTO inventory.manual_blocks (
                "Id", "PropertyId", "InventoryUnitId", "Arrival", "Departure",
                "Reason", "Status", "Version", "CreatedAtUtc", "ReleasedAtUtc",
                "ScopeId", "BlockGroupId")
            VALUES {childValues};
            """;
    }

    private static string BuildV2ReceiptSql(
        Guid operationId,
        int resourceKind,
        Guid resourceId,
        int kind,
        long expectedVersion,
        Guid resultGroupId,
        int resultStatus,
        long resultVersion,
        int affectedCount,
        Guid? previousGroupId,
        int totalCount,
        int activeCount,
        int releasedCount,
        int alreadyReleasedCount,
        int createdCount,
        string membershipDigest,
        DateTimeOffset completedAtUtc) => $"""
            INSERT INTO inventory.management_operations (
                "Id", "ScopeId", "ResourceKind", "ResourceId", "PropertyId",
                "Kind", "ExpectedVersion", "RequestFingerprint",
                "ResultBlockGroupId", "ResultAffectedBlockCount", "ResultVersion",
                "CompletedAtUtc", "ResultBlockGroupStatus",
                "ResultPreviousBlockGroupId", "ResultTotalBlockCount",
                "ResultActiveBlockCount", "ResultReleasedBlockCount",
                "ResultAlreadyReleasedBlockCount", "ResultCreatedBlockCount",
                "ResultMembershipDigest")
            VALUES (
                '{operationId}', '{Tenant}', {resourceKind}, '{resourceId}',
                '{PropertyId}', {kind}, {expectedVersion}, '{new string('a', 64)}',
                '{resultGroupId}', {affectedCount}, {resultVersion},
                '{completedAtUtc:O}', {resultStatus}, {SqlUuid(previousGroupId)},
                {totalCount}, {activeCount}, {releasedCount},
                {alreadyReleasedCount}, {createdCount}, '{membershipDigest}');
            """;

    private static string ComputeMembershipDigest(
        int targetKind,
        string? buildingLabel,
        string? floorLabel,
        Guid? roomId,
        Guid? inventoryUnitId,
        IReadOnlyCollection<Guid> unitIds)
    {
        string building = buildingLabel?.Trim() ?? string.Empty;
        string floor = floorLabel?.Trim() ?? string.Empty;
        string members = string.Join(
            ',',
            unitIds.Select(id => id.ToString("N"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal));
        string payload =
            "bunkfy-inventory-manual-block-group-membership/v1" +
            $"|kind={targetKind}" +
            $"|building={Encoding.UTF8.GetByteCount(building)}:{building}" +
            $"|floor={Encoding.UTF8.GetByteCount(floor)}:{floor}" +
            $"|room={roomId?.ToString("N") ?? string.Empty}" +
            $"|unit={inventoryUnitId?.ToString("N") ?? string.Empty}" +
            $"|arrival={Arrival:yyyy-MM-dd}" +
            $"|departure={Departure:yyyy-MM-dd}" +
            $"|members={unitIds.Distinct().Count()}|{members}";
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string SqlLiteral(string? value) => value is null
        ? "NULL"
        : $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string SqlUuid(Guid? value) => value.HasValue
        ? $"'{value.Value}'"
        : "NULL";
}
