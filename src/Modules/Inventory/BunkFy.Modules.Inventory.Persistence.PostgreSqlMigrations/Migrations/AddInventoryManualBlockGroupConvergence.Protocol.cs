namespace BunkFy.Modules.Inventory.Persistence.PostgreSqlMigrations.Migrations;

public partial class AddInventoryManualBlockGroupConvergence
{
    internal const string UpLockAndPreflightSql =
        """
        LOCK TABLE inventory.bed_retirements
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.inventory_units
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.management_operations
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.manual_blocks
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.property_topology
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.room_configurations
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.room_retirements
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.room_topology
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.tenant_destroy_operations
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.tenant_revisions
            IN ACCESS EXCLUSIVE MODE NOWAIT;

        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM inventory.tenant_destroy_operations operation
                INNER JOIN inventory.tenant_revisions revision
                    ON revision."ScopeId" = operation."ScopeId"
                WHERE revision."LifecycleStatus" = 2
                  AND revision."DestroyOperationId" = operation."OperationId")
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'active Inventory tenant destruction prevents block-group upgrade';
            END IF;

            IF EXISTS (
                SELECT 1
                FROM inventory.manual_blocks block
                LEFT JOIN inventory.inventory_units unit
                    ON unit."ScopeId" = block."ScopeId"
                   AND unit."Id" = block."InventoryUnitId"
                WHERE unit."Id" IS NULL OR
                      unit."PropertyId" <> block."PropertyId")
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'invalid Inventory manual-block unit property coordinates prevent upgrade';
            END IF;

            IF EXISTS (
                SELECT 1
                FROM inventory.manual_blocks block
                GROUP BY block."ScopeId", block."PropertyId",
                         block."BlockGroupId", block."InventoryUnitId"
                HAVING count(*) > 1)
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'duplicate Inventory block-group membership prevents upgrade';
            END IF;

            IF EXISTS (
                SELECT 1
                FROM inventory.manual_blocks block
                GROUP BY block."ScopeId", block."PropertyId",
                         block."BlockGroupId"
                HAVING min(block."Arrival") <> max(block."Arrival")
                    OR min(block."Departure") <> max(block."Departure")
                    OR min(block."Reason") <> max(block."Reason")
                    OR min(block."CreatedAtUtc") <> max(block."CreatedAtUtc"))
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'heterogeneous legacy Inventory block-group definition prevents upgrade';
            END IF;

            IF EXISTS (
                SELECT 1
                FROM inventory.manual_blocks block
                GROUP BY block."ScopeId", block."PropertyId",
                         block."BlockGroupId"
                HAVING bool_or(block."Status" NOT IN (1, 2))
                    OR bool_or(block."Status" = 1 AND
                               block."ReleasedAtUtc" IS NOT NULL)
                    OR bool_or(block."Status" = 2 AND
                               block."ReleasedAtUtc" IS NULL))
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'invalid legacy Inventory block-group lifecycle prevents upgrade';
            END IF;

            IF EXISTS (
                SELECT 1
                FROM inventory.manual_blocks block
                GROUP BY block."ScopeId", block."PropertyId",
                         block."BlockGroupId"
                HAVING count(*) > 500 AND
                       count(*) FILTER (WHERE block."Status" = 1) > 0)
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'active legacy Inventory block group exceeds 500 members';
            END IF;

            IF EXISTS (
                WITH property_candidates AS (
                    SELECT room."ScopeId", room."PropertyId"
                    FROM inventory.room_topology room
                    UNION
                    SELECT unit."ScopeId", unit."PropertyId"
                    FROM inventory.inventory_units unit
                    UNION
                    SELECT configuration."ScopeId", configuration."PropertyId"
                    FROM inventory.room_configurations configuration
                    UNION
                    SELECT retirement."ScopeId", retirement."PropertyId"
                    FROM inventory.bed_retirements retirement
                    UNION
                    SELECT retirement."ScopeId", retirement."PropertyId"
                    FROM inventory.room_retirements retirement
                )
                SELECT 1
                FROM property_candidates candidate
                GROUP BY candidate."PropertyId"
                HAVING count(DISTINCT candidate."ScopeId") > 1
            ) OR EXISTS (
                WITH property_candidates AS (
                    SELECT room."ScopeId", room."PropertyId"
                    FROM inventory.room_topology room
                    UNION
                    SELECT unit."ScopeId", unit."PropertyId"
                    FROM inventory.inventory_units unit
                    UNION
                    SELECT configuration."ScopeId", configuration."PropertyId"
                    FROM inventory.room_configurations configuration
                    UNION
                    SELECT retirement."ScopeId", retirement."PropertyId"
                    FROM inventory.bed_retirements retirement
                    UNION
                    SELECT retirement."ScopeId", retirement."PropertyId"
                    FROM inventory.room_retirements retirement
                )
                SELECT 1
                FROM property_candidates candidate
                INNER JOIN inventory.property_topology property
                    ON property."Id" = candidate."PropertyId"
                WHERE property."ScopeId" <> candidate."ScopeId"
            )
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'conflicting Inventory property coordinates prevent block-group upgrade';
            END IF;

            IF EXISTS (
                SELECT 1
                FROM inventory.tenant_revisions revision
                WHERE revision."ScopeId" IN (
                    SELECT property."ScopeId"
                    FROM inventory.property_topology property
                    UNION
                    SELECT block."ScopeId"
                    FROM inventory.manual_blocks block
                    UNION
                    SELECT room."ScopeId"
                    FROM inventory.room_topology room
                    UNION
                    SELECT unit."ScopeId"
                    FROM inventory.inventory_units unit
                    UNION
                    SELECT configuration."ScopeId"
                    FROM inventory.room_configurations configuration
                    UNION
                    SELECT retirement."ScopeId"
                    FROM inventory.bed_retirements retirement
                    UNION
                    SELECT retirement."ScopeId"
                    FROM inventory.room_retirements retirement)
                  AND (revision."LifecycleStatus" <> 1 OR
                       revision."Revision" = 9223372036854775807))
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory tenant revision prevents block-group upgrade';
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class index_relation
                INNER JOIN pg_catalog.pg_namespace index_namespace
                    ON index_namespace.oid = index_relation.relnamespace
                INNER JOIN pg_catalog.pg_index index_metadata
                    ON index_metadata.indexrelid = index_relation.oid
                INNER JOIN pg_catalog.pg_class table_relation
                    ON table_relation.oid = index_metadata.indrelid
                INNER JOIN pg_catalog.pg_namespace table_namespace
                    ON table_namespace.oid = table_relation.relnamespace
                WHERE index_namespace.nspname = 'inventory'
                  AND table_namespace.nspname = 'inventory'
                  AND index_relation.relname =
                      'UX_bed_retirements_ScopeId_BedId_active'
                  AND table_relation.relname = 'bed_retirements'
                  AND index_metadata.indisunique
                  AND index_metadata.indisvalid
                  AND index_metadata.indisready
                  AND index_metadata.indnkeyatts = 2
                  AND (
                      SELECT array_agg(attribute.attname ORDER BY key.ordinality)
                      FROM unnest(index_metadata.indkey)
                           WITH ORDINALITY AS key(attribute_number, ordinality)
                      INNER JOIN pg_catalog.pg_attribute attribute
                          ON attribute.attrelid = table_relation.oid
                         AND attribute.attnum = key.attribute_number
                      WHERE key.ordinality <= index_metadata.indnkeyatts
                  ) = ARRAY['ScopeId', 'BedId']::name[]
                  AND pg_get_expr(
                      index_metadata.indpred,
                      index_metadata.indrelid) =
                      '("State" = ANY (ARRAY[1, 2, 3, 5]))')
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory active bed-retirement uniqueness index is invalid';
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class index_relation
                INNER JOIN pg_catalog.pg_namespace index_namespace
                    ON index_namespace.oid = index_relation.relnamespace
                INNER JOIN pg_catalog.pg_index index_metadata
                    ON index_metadata.indexrelid = index_relation.oid
                INNER JOIN pg_catalog.pg_class table_relation
                    ON table_relation.oid = index_metadata.indrelid
                INNER JOIN pg_catalog.pg_namespace table_namespace
                    ON table_namespace.oid = table_relation.relnamespace
                WHERE index_namespace.nspname = 'inventory'
                  AND table_namespace.nspname = 'inventory'
                  AND index_relation.relname =
                      'UX_room_retirements_ScopeId_RoomId_active'
                  AND table_relation.relname = 'room_retirements'
                  AND index_metadata.indisunique
                  AND index_metadata.indisvalid
                  AND index_metadata.indisready
                  AND index_metadata.indnkeyatts = 2
                  AND (
                      SELECT array_agg(attribute.attname ORDER BY key.ordinality)
                      FROM unnest(index_metadata.indkey)
                           WITH ORDINALITY AS key(attribute_number, ordinality)
                      INNER JOIN pg_catalog.pg_attribute attribute
                          ON attribute.attrelid = table_relation.oid
                         AND attribute.attnum = key.attribute_number
                      WHERE key.ordinality <= index_metadata.indnkeyatts
                  ) = ARRAY['ScopeId', 'RoomId']::name[]
                  AND pg_get_expr(
                      index_metadata.indpred,
                      index_metadata.indrelid) =
                      '("State" = ANY (ARRAY[1, 2, 3, 5]))')
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory active room-retirement uniqueness index is invalid';
            END IF;
        END;
        $$;
        """;

    internal const string UpBackfillSql =
        """
        WITH property_candidates AS (
            SELECT room."ScopeId", room."PropertyId"
            FROM inventory.room_topology room
            UNION
            SELECT unit."ScopeId", unit."PropertyId"
            FROM inventory.inventory_units unit
            UNION
            SELECT configuration."ScopeId", configuration."PropertyId"
            FROM inventory.room_configurations configuration
            UNION
            SELECT retirement."ScopeId", retirement."PropertyId"
            FROM inventory.bed_retirements retirement
            UNION
            SELECT retirement."ScopeId", retirement."PropertyId"
            FROM inventory.room_retirements retirement
        )
        INSERT INTO inventory.property_topology (
            "Id", "Name", "Code", "TimeZoneId", "Status",
            "SourceVersion", "DetailsVersion", "IsKnown",
            "AvailabilitySelectionVersion", "ScopeId")
        SELECT candidate."PropertyId", '', '', '', 0,
               0, 0, false, 1, candidate."ScopeId"
        FROM property_candidates candidate
        LEFT JOIN inventory.property_topology property
            ON property."Id" = candidate."PropertyId"
        WHERE property."Id" IS NULL;

        WITH grouped AS (
            SELECT block."ScopeId",
                   block."PropertyId",
                   block."BlockGroupId",
                   min(block."Arrival") AS arrival,
                   min(block."Departure") AS departure,
                   min(block."Reason") AS reason,
                   count(*)::integer AS initial_count,
                   count(*) FILTER (WHERE block."Status" = 1)::integer
                       AS active_count,
                   min(block."CreatedAtUtc") AS created_at,
                   max(block."ReleasedAtUtc") AS last_released_at,
                   string_agg(
                       lower(replace(block."InventoryUnitId"::text, '-', '')),
                       ',' ORDER BY
                           lower(replace(block."InventoryUnitId"::text, '-', ''))
                               COLLATE "C"
                   ) AS members
            FROM inventory.manual_blocks block
            GROUP BY block."ScopeId", block."PropertyId",
                     block."BlockGroupId"
        ), canonical AS (
            SELECT grouped.*,
                   'bunkfy-inventory-manual-block-group-membership/v1' ||
                   '|kind=0|building=0:|floor=0:|room=|unit=' ||
                   '|arrival=' || to_char(grouped.arrival, 'YYYY-MM-DD') ||
                   '|departure=' || to_char(grouped.departure, 'YYYY-MM-DD') ||
                   '|members=' || grouped.initial_count::text || '|' ||
                   grouped.members AS payload
            FROM grouped
        )
        INSERT INTO inventory.manual_block_groups (
            "Id", "ScopeId", "PropertyId", "TargetKind",
            "BuildingLabel", "FloorLabel", "RoomId", "InventoryUnitId",
            "Arrival", "Departure", "Reason", "SelectionDigest",
            "MembershipDigest", "MembershipDigestVersion",
            "InitialBlockCount", "ActiveBlockCount", "State", "Version",
            "ReplacesGroupId", "CreatedAtUtc", "UpdatedAtUtc",
            "ReleasedAtUtc", "CreatedByActorId", "LastModifiedByActorId")
        SELECT canonical."BlockGroupId",
               canonical."ScopeId",
               canonical."PropertyId",
               0,
               NULL, NULL, NULL, NULL,
               canonical.arrival,
               canonical.departure,
               canonical.reason,
               NULL,
               encode(sha256(convert_to(canonical.payload, 'UTF8')), 'hex'),
               1,
               canonical.initial_count,
               canonical.active_count,
               CASE
                   WHEN canonical.active_count = canonical.initial_count THEN 1
                   WHEN canonical.active_count > 0 THEN 2
                   ELSE 3
               END,
               1,
               NULL,
               canonical.created_at,
               CASE WHEN canonical.active_count = canonical.initial_count
                   THEN NULL ELSE canonical.last_released_at END,
               CASE WHEN canonical.active_count = 0
                   THEN canonical.last_released_at ELSE NULL END,
               NULL,
               NULL
        FROM canonical;

        WITH affected AS (
            SELECT property."ScopeId"
            FROM inventory.property_topology property
            UNION
            SELECT block."ScopeId"
            FROM inventory.manual_blocks block
        )
        INSERT INTO inventory.tenant_revisions (
            "ScopeId", "Revision", "LifecycleStatus",
            "DestroyOperationId", "DestroyRequestSha256",
            "DestroyStartedAtUtc", "DestroyCompletedAtUtc")
        SELECT affected."ScopeId", 1, 1, NULL, NULL, NULL, NULL
        FROM affected
        ON CONFLICT ("ScopeId") DO UPDATE
        SET "Revision" = inventory.tenant_revisions."Revision" + 1;
        """;

    internal const string UpProtocolSql =
        """
        CREATE OR REPLACE FUNCTION
            inventory.is_authorized_tenant_destruction(
                target_scope_id text)
        RETURNS boolean
        LANGUAGE sql
        STABLE
        AS $$
            SELECT EXISTS (
                SELECT 1
                FROM inventory.tenant_destroy_operations operation
                INNER JOIN inventory.tenant_revisions revision
                    ON revision."ScopeId" = operation."ScopeId"
                WHERE operation."OperationId"::text = current_setting(
                          'bunkfy.inventory_tenant_destroy_operation_id',
                          true)
                  AND operation."ScopeId" = target_scope_id
                  AND revision."LifecycleStatus" IN (2, 3)
                  AND revision."DestroyOperationId" = operation."OperationId"
                  AND revision."DestroyRequestSha256" =
                      operation."RequestSha256");
        $$;

        CREATE OR REPLACE FUNCTION
            inventory.enforce_management_operation_append_only()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            IF TG_OP = 'DELETE' AND
               inventory.is_authorized_tenant_destruction(OLD."ScopeId")
            THEN
                RETURN OLD;
            END IF;

            RAISE EXCEPTION USING
                ERRCODE = 'P0001',
                MESSAGE = 'Inventory management operation receipts are append-only';
        END;
        $$;

        CREATE OR REPLACE FUNCTION
            inventory.enforce_availability_selection_epoch()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                IF inventory.is_authorized_tenant_destruction(OLD."ScopeId")
                THEN
                    RETURN OLD;
                END IF;

                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory property availability-selection evidence cannot be deleted';
            END IF;

            IF NEW."AvailabilitySelectionVersion" <= 0 THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory property availability-selection version must be positive';
            END IF;

            IF TG_OP = 'INSERT' THEN
                RETURN NEW;
            END IF;

            IF to_jsonb(NEW) = to_jsonb(OLD) THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory property topology no-op updates are forbidden';
            END IF;

            IF NEW."Id" IS DISTINCT FROM OLD."Id" OR
               NEW."ScopeId" IS DISTINCT FROM OLD."ScopeId" OR
               NEW."ProjectionOrdinal" IS DISTINCT FROM
                   OLD."ProjectionOrdinal" OR
               OLD."AvailabilitySelectionVersion" = 9223372036854775807 OR
               NEW."AvailabilitySelectionVersion" <>
                   OLD."AvailabilitySelectionVersion" + 1
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory property availability-selection transition is invalid';
            END IF;

            RETURN NEW;
        END;
        $$;

        CREATE OR REPLACE FUNCTION
            inventory.require_availability_selection_parent_write(
                target_scope_id text,
                target_property_id uuid)
        RETURNS void
        LANGUAGE plpgsql
        AS $$
        DECLARE
            parent_written_in_transaction boolean;
        BEGIN
            SELECT pg_xact_status(property.xmin::text::xid8) = 'in progress'
            INTO parent_written_in_transaction
            FROM inventory.property_topology property
            WHERE property."ScopeId" = target_scope_id
              AND property."Id" = target_property_id;

            IF parent_written_in_transaction IS DISTINCT FROM true THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory selection-affecting write requires an advanced property epoch';
            END IF;
        END;
        $$;

        CREATE OR REPLACE FUNCTION
            inventory.validate_selection_affecting_child_write()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        DECLARE
            old_scope_id text;
            old_property_id uuid;
            new_scope_id text;
            new_property_id uuid;
            requires_epoch boolean := false;
            old_active boolean := false;
            new_active boolean := false;
        BEGIN
            IF TG_OP <> 'INSERT' THEN
                old_scope_id := OLD."ScopeId";
                old_property_id := OLD."PropertyId";
            END IF;
            IF TG_OP <> 'DELETE' THEN
                new_scope_id := NEW."ScopeId";
                new_property_id := NEW."PropertyId";
            END IF;

            IF TG_OP = 'DELETE' AND
               inventory.is_authorized_tenant_destruction(old_scope_id)
            THEN
                RETURN NULL;
            END IF;

            IF TG_TABLE_NAME = 'room_topology' THEN
                requires_epoch := TG_OP <> 'UPDATE' OR
                    to_jsonb(NEW) IS DISTINCT FROM to_jsonb(OLD);
            ELSIF TG_TABLE_NAME = 'inventory_units' THEN
                requires_epoch := TG_OP <> 'UPDATE' OR
                    (to_jsonb(NEW) - 'AvailabilityMutationVersion')
                    IS DISTINCT FROM
                    (to_jsonb(OLD) - 'AvailabilityMutationVersion');
            ELSIF TG_TABLE_NAME = 'room_configurations' THEN
                requires_epoch := TG_OP <> 'UPDATE' OR
                    NEW."SalesMode" IS DISTINCT FROM OLD."SalesMode";
            ELSIF TG_TABLE_NAME IN ('bed_retirements', 'room_retirements')
            THEN
                old_active := TG_OP <> 'INSERT' AND
                    OLD."State" IN (1, 2, 3, 5);
                new_active := TG_OP <> 'DELETE' AND
                    NEW."State" IN (1, 2, 3, 5);
                requires_epoch := old_active IS DISTINCT FROM new_active;
            END IF;

            IF NOT requires_epoch THEN
                RETURN NULL;
            END IF;

            IF TG_OP <> 'INSERT' THEN
                PERFORM inventory.require_availability_selection_parent_write(
                    old_scope_id,
                    old_property_id);
            END IF;
            IF TG_OP <> 'DELETE' AND
               (TG_OP = 'INSERT' OR
                new_scope_id IS DISTINCT FROM old_scope_id OR
                new_property_id IS DISTINCT FROM old_property_id)
            THEN
                PERFORM inventory.require_availability_selection_parent_write(
                    new_scope_id,
                    new_property_id);
            END IF;

            RETURN NULL;
        END;
        $$;

        CREATE OR REPLACE FUNCTION
            inventory.enforce_manual_block_group_transition()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                IF inventory.is_authorized_tenant_destruction(OLD."ScopeId")
                THEN
                    RETURN OLD;
                END IF;

                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory manual block groups cannot be deleted';
            END IF;

            IF TG_OP = 'INSERT' THEN
                IF NEW."TargetKind" NOT BETWEEN 1 AND 5 OR
                   NEW."Version" <> 1 OR
                   NEW."State" <> 1 OR
                   NEW."ActiveBlockCount" <> NEW."InitialBlockCount" OR
                   NEW."UpdatedAtUtc" IS NOT NULL OR
                   NEW."ReleasedAtUtc" IS NOT NULL OR
                   NEW."CreatedByActorId" IS NULL OR
                   NEW."LastModifiedByActorId" IS DISTINCT FROM
                       NEW."CreatedByActorId"
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'Inventory manual block-group initial evidence is invalid';
                END IF;

                RETURN NEW;
            END IF;

            IF (NEW."Id", NEW."ScopeId", NEW."PropertyId",
                NEW."TargetKind", NEW."BuildingLabel", NEW."FloorLabel",
                NEW."RoomId", NEW."InventoryUnitId", NEW."Arrival",
                NEW."Departure", NEW."Reason", NEW."SelectionDigest",
                NEW."MembershipDigest", NEW."MembershipDigestVersion",
                NEW."InitialBlockCount", NEW."ReplacesGroupId",
                NEW."CreatedAtUtc", NEW."CreatedByActorId")
            IS DISTINCT FROM
               (OLD."Id", OLD."ScopeId", OLD."PropertyId",
                OLD."TargetKind", OLD."BuildingLabel", OLD."FloorLabel",
                OLD."RoomId", OLD."InventoryUnitId", OLD."Arrival",
                OLD."Departure", OLD."Reason", OLD."SelectionDigest",
                OLD."MembershipDigest", OLD."MembershipDigestVersion",
                OLD."InitialBlockCount", OLD."ReplacesGroupId",
                OLD."CreatedAtUtc", OLD."CreatedByActorId") OR
               NEW."Version" <> OLD."Version" + 1 OR
               NEW."ActiveBlockCount" >= OLD."ActiveBlockCount" OR
               NEW."UpdatedAtUtc" IS NULL OR
               NEW."UpdatedAtUtc" < COALESCE(
                   OLD."UpdatedAtUtc", OLD."CreatedAtUtc") OR
               NEW."LastModifiedByActorId" IS NULL OR
               ((NEW."State" = 2 AND
                 (NEW."ActiveBlockCount" <= 0 OR
                  NEW."ReleasedAtUtc" IS NOT NULL)) OR
                (NEW."State" IN (3, 4) AND
                 (NEW."ActiveBlockCount" <> 0 OR
                  NEW."ReleasedAtUtc" IS DISTINCT FROM
                      NEW."UpdatedAtUtc")) OR
                NEW."State" NOT IN (2, 3, 4))
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory manual block-group transition is invalid';
            END IF;

            IF OLD."State" NOT IN (1, 2) OR
               (NEW."State" = 2 AND OLD."State" NOT IN (1, 2))
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'terminal Inventory manual block-group evidence is immutable';
            END IF;

            RETURN NEW;
        END;
        $$;

        CREATE OR REPLACE FUNCTION
            inventory.enforce_manual_block_transition()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                IF inventory.is_authorized_tenant_destruction(OLD."ScopeId")
                THEN
                    RETURN OLD;
                END IF;

                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory manual blocks cannot be deleted';
            END IF;

            IF TG_OP = 'INSERT' THEN
                IF NEW."Status" <> 1 OR
                   NEW."Version" <> 1 OR
                   NEW."ReleasedAtUtc" IS NOT NULL
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'Inventory manual block initial evidence is invalid';
                END IF;

                RETURN NEW;
            END IF;

            IF (NEW."Id", NEW."ScopeId", NEW."PropertyId",
                NEW."BlockGroupId", NEW."InventoryUnitId", NEW."Arrival",
                NEW."Departure", NEW."Reason", NEW."CreatedAtUtc")
            IS DISTINCT FROM
               (OLD."Id", OLD."ScopeId", OLD."PropertyId",
                OLD."BlockGroupId", OLD."InventoryUnitId", OLD."Arrival",
                OLD."Departure", OLD."Reason", OLD."CreatedAtUtc") OR
               OLD."Status" <> 1 OR NEW."Status" <> 2 OR
               NEW."Version" <> OLD."Version" + 1 OR
               NEW."ReleasedAtUtc" IS NULL OR
               NEW."ReleasedAtUtc" < NEW."CreatedAtUtc"
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory manual block transition is invalid';
            END IF;

            RETURN NEW;
        END;
        $$;

        CREATE OR REPLACE FUNCTION
            inventory.validate_manual_block_group_graph()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        DECLARE
            target_scope_id text;
            target_property_id uuid;
            target_group_id uuid;
            parent inventory.manual_block_groups%ROWTYPE;
            initial_count integer;
            active_count integer;
            member_arrival date;
            member_departure date;
            member_reason text;
            member_definition_consistent boolean;
            member_creation_consistent boolean;
            unit_target_consistent boolean;
            last_released_at timestamp with time zone;
            members text;
            payload text;
            expected_digest text;
            successor_count integer;
            successor_created_at timestamp with time zone;
            predecessor_state integer;
            changed_row jsonb;
        BEGIN
            changed_row := CASE
                WHEN TG_OP = 'DELETE' THEN to_jsonb(OLD)
                ELSE to_jsonb(NEW)
            END;
            target_scope_id := changed_row ->> 'ScopeId';
            target_property_id := (changed_row ->> 'PropertyId')::uuid;
            target_group_id := (changed_row ->> CASE
                WHEN TG_TABLE_NAME = 'manual_blocks' THEN 'BlockGroupId'
                ELSE 'Id'
            END)::uuid;

            IF TG_OP = 'DELETE' AND
               inventory.is_authorized_tenant_destruction(target_scope_id)
            THEN
                RETURN NULL;
            END IF;

            SELECT group_row.*
            INTO parent
            FROM inventory.manual_block_groups group_row
            WHERE group_row."ScopeId" = target_scope_id
              AND group_row."PropertyId" = target_property_id
              AND group_row."Id" = target_group_id;

            IF NOT FOUND THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory manual block-group parent is missing';
            END IF;

            SELECT count(*)::integer,
                   count(*) FILTER (WHERE block."Status" = 1)::integer,
                   min(block."Arrival"),
                   min(block."Departure"),
                   min(block."Reason"),
                   bool_and(
                       block."Arrival" = parent."Arrival" AND
                       block."Departure" = parent."Departure" AND
                       block."Reason" = parent."Reason"),
                   bool_and(block."CreatedAtUtc" = parent."CreatedAtUtc"),
                   bool_and(parent."TargetKind" <> 5 OR
                            block."InventoryUnitId" =
                                parent."InventoryUnitId"),
                   max(block."ReleasedAtUtc"),
                   string_agg(
                       lower(replace(block."InventoryUnitId"::text, '-', '')),
                       ',' ORDER BY
                           lower(replace(block."InventoryUnitId"::text, '-', ''))
                               COLLATE "C")
            INTO initial_count, active_count, member_arrival,
                 member_departure, member_reason,
                 member_definition_consistent,
                 member_creation_consistent,
                 unit_target_consistent,
                 last_released_at,
                 members
            FROM inventory.manual_blocks block
            WHERE block."ScopeId" = target_scope_id
              AND block."PropertyId" = target_property_id
              AND block."BlockGroupId" = target_group_id;

            IF initial_count = 0 THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory manual block-group has no members';
            END IF;

            payload :=
                'bunkfy-inventory-manual-block-group-membership/v1' ||
                '|kind=' || parent."TargetKind"::text ||
                '|building=' || octet_length(convert_to(
                    COALESCE(parent."BuildingLabel", ''), 'UTF8'))::text || ':' ||
                    COALESCE(parent."BuildingLabel", '') ||
                '|floor=' || octet_length(convert_to(
                    COALESCE(parent."FloorLabel", ''), 'UTF8'))::text || ':' ||
                    COALESCE(parent."FloorLabel", '') ||
                '|room=' || COALESCE(
                    lower(replace(parent."RoomId"::text, '-', '')), '') ||
                '|unit=' || COALESCE(
                    lower(replace(parent."InventoryUnitId"::text, '-', '')), '') ||
                '|arrival=' || to_char(parent."Arrival", 'YYYY-MM-DD') ||
                '|departure=' || to_char(parent."Departure", 'YYYY-MM-DD') ||
                '|members=' || initial_count::text || '|' || members;
            expected_digest := encode(
                sha256(convert_to(payload, 'UTF8')), 'hex');

            SELECT count(*)::integer,
                   min(successor."CreatedAtUtc")
            INTO successor_count, successor_created_at
            FROM inventory.manual_block_groups successor
            WHERE successor."ScopeId" = target_scope_id
              AND successor."PropertyId" = target_property_id
              AND successor."ReplacesGroupId" = target_group_id;

            predecessor_state := NULL;
            IF parent."ReplacesGroupId" IS NOT NULL THEN
                SELECT predecessor."State"
                INTO predecessor_state
                FROM inventory.manual_block_groups predecessor
                WHERE predecessor."ScopeId" = target_scope_id
                  AND predecessor."PropertyId" = target_property_id
                  AND predecessor."Id" = parent."ReplacesGroupId";
            END IF;

            IF parent."InitialBlockCount" <> initial_count OR
               parent."ActiveBlockCount" <> active_count OR
               NOT member_definition_consistent OR
               NOT member_creation_consistent OR
               NOT unit_target_consistent OR
               parent."Arrival" <> member_arrival OR
               parent."Departure" <> member_departure OR
               parent."Reason" <> member_reason OR
               parent."MembershipDigest" <> expected_digest OR
               parent."UpdatedAtUtc" IS DISTINCT FROM last_released_at OR
               (parent."State" = 1 AND
                active_count <> initial_count) OR
               (parent."State" = 2 AND
                (active_count <= 0 OR active_count >= initial_count)) OR
               (parent."State" IN (3, 4) AND active_count <> 0) OR
               (parent."State" = 4 AND
                (successor_count <> 1 OR
                 successor_created_at IS DISTINCT FROM
                     parent."ReleasedAtUtc")) OR
               (parent."State" <> 4 AND successor_count <> 0) OR
               (parent."ReplacesGroupId" IS NOT NULL AND
                predecessor_state IS DISTINCT FROM 4)
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory manual block-group graph is inconsistent';
            END IF;

            RETURN NULL;
        END;
        $$;

        CREATE OR REPLACE FUNCTION
            inventory.validate_manual_block_group_member_parent_write()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        DECLARE
            target_scope_id text;
            target_property_id uuid;
            target_group_id uuid;
            parent_written_in_transaction boolean;
        BEGIN
            target_scope_id := CASE
                WHEN TG_OP = 'DELETE' THEN OLD."ScopeId"
                ELSE NEW."ScopeId"
            END;
            target_property_id := CASE
                WHEN TG_OP = 'DELETE' THEN OLD."PropertyId"
                ELSE NEW."PropertyId"
            END;
            target_group_id := CASE
                WHEN TG_OP = 'DELETE' THEN OLD."BlockGroupId"
                ELSE NEW."BlockGroupId"
            END;

            IF TG_OP = 'DELETE' AND
               inventory.is_authorized_tenant_destruction(target_scope_id)
            THEN
                RETURN NULL;
            END IF;

            SELECT pg_xact_status(group_row.xmin::text::xid8) = 'in progress'
            INTO parent_written_in_transaction
            FROM inventory.manual_block_groups group_row
            WHERE group_row."ScopeId" = target_scope_id
              AND group_row."PropertyId" = target_property_id
              AND group_row."Id" = target_group_id;

            IF parent_written_in_transaction IS DISTINCT FROM true THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory manual block-group graph is inconsistent';
            END IF;

            RETURN NULL;
        END;
        $$;

        CREATE OR REPLACE FUNCTION
            inventory.validate_manual_block_group_operation_receipt()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        DECLARE
            result_group inventory.manual_block_groups%ROWTYPE;
            predecessor_state integer;
            predecessor_version bigint;
            predecessor_initial_count integer;
            predecessor_released_at timestamp with time zone;
        BEGIN
            IF NEW."Kind" NOT IN (12, 13, 14) THEN
                RETURN NULL;
            END IF;

            SELECT group_row.*
            INTO result_group
            FROM inventory.manual_block_groups group_row
            WHERE group_row."ScopeId" = NEW."ScopeId"
              AND group_row."PropertyId" = NEW."PropertyId"
              AND group_row."Id" = NEW."ResultBlockGroupId";

            IF NOT FOUND OR
               result_group."State" <> NEW."ResultBlockGroupStatus" OR
               result_group."Version" <> NEW."ResultVersion" OR
               result_group."InitialBlockCount" <>
                   NEW."ResultTotalBlockCount" OR
               result_group."ActiveBlockCount" <>
                   NEW."ResultActiveBlockCount" OR
               result_group."MembershipDigest" <>
                   NEW."ResultMembershipDigest"
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory block-group operation receipt does not match durable group evidence';
            END IF;

            IF NEW."Kind" = 12 AND
               (result_group."ReplacesGroupId" IS NOT NULL OR
                NEW."CompletedAtUtc" IS DISTINCT FROM
                    result_group."CreatedAtUtc")
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory block-group create receipt has invalid lineage';
            END IF;

            IF NEW."Kind" = 13 AND
               NEW."ResultBlockGroupId" <> NEW."ResourceId"
            THEN
                SELECT predecessor."State",
                       predecessor."Version",
                       predecessor."InitialBlockCount",
                       predecessor."ReleasedAtUtc"
                INTO predecessor_state,
                     predecessor_version,
                     predecessor_initial_count,
                     predecessor_released_at
                FROM inventory.manual_block_groups predecessor
                WHERE predecessor."ScopeId" = NEW."ScopeId"
                  AND predecessor."PropertyId" = NEW."PropertyId"
                  AND predecessor."Id" = NEW."ResourceId";

                IF predecessor_state IS DISTINCT FROM 4 OR
                   predecessor_version IS DISTINCT FROM
                       NEW."ExpectedVersion" + 1 OR
                   predecessor_initial_count IS DISTINCT FROM
                       NEW."ResultReleasedBlockCount" +
                       NEW."ResultAlreadyReleasedBlockCount" OR
                   predecessor_released_at IS DISTINCT FROM
                       result_group."CreatedAtUtc" OR
                   NEW."CompletedAtUtc" IS DISTINCT FROM
                       predecessor_released_at OR
                   result_group."ReplacesGroupId" IS DISTINCT FROM
                       NEW."ResourceId"
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'Inventory block-group replace receipt has invalid lineage';
                END IF;
            END IF;

            IF NEW."Kind" = 13 AND
               NEW."ResultBlockGroupId" = NEW."ResourceId" AND
               NEW."CompletedAtUtc" < COALESCE(
                   result_group."UpdatedAtUtc",
                   result_group."CreatedAtUtc")
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory block-group replace receipt predates durable group evidence';
            END IF;

            IF NEW."Kind" = 14 AND
               ((NEW."ResultVersion" = NEW."ExpectedVersion" + 1 AND
                 NEW."CompletedAtUtc" IS DISTINCT FROM
                     result_group."ReleasedAtUtc") OR
                (NEW."ResultVersion" = NEW."ExpectedVersion" AND
                 NEW."CompletedAtUtc" < COALESCE(
                     result_group."ReleasedAtUtc",
                     result_group."UpdatedAtUtc",
                     result_group."CreatedAtUtc")))
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory block-group release receipt does not match durable completion evidence';
            END IF;

            RETURN NULL;
        END;
        $$;

        CREATE TRIGGER management_operations_append_only
        BEFORE UPDATE OR DELETE
        ON inventory.management_operations
        FOR EACH ROW
        EXECUTE FUNCTION
            inventory.enforce_management_operation_append_only();

        CREATE TRIGGER property_topology_selection_epoch_guard
        BEFORE INSERT OR UPDATE OR DELETE
        ON inventory.property_topology
        FOR EACH ROW
        EXECUTE FUNCTION
            inventory.enforce_availability_selection_epoch();

        CREATE CONSTRAINT TRIGGER room_topology_selection_epoch_guard
        AFTER INSERT OR UPDATE OR DELETE
        ON inventory.room_topology
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW
        EXECUTE FUNCTION
            inventory.validate_selection_affecting_child_write();

        CREATE CONSTRAINT TRIGGER inventory_units_selection_epoch_guard
        AFTER INSERT OR UPDATE OR DELETE
        ON inventory.inventory_units
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW
        EXECUTE FUNCTION
            inventory.validate_selection_affecting_child_write();

        CREATE CONSTRAINT TRIGGER room_configurations_selection_epoch_guard
        AFTER INSERT OR UPDATE OR DELETE
        ON inventory.room_configurations
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW
        EXECUTE FUNCTION
            inventory.validate_selection_affecting_child_write();

        CREATE CONSTRAINT TRIGGER bed_retirements_selection_epoch_guard
        AFTER INSERT OR UPDATE OR DELETE
        ON inventory.bed_retirements
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW
        EXECUTE FUNCTION
            inventory.validate_selection_affecting_child_write();

        CREATE CONSTRAINT TRIGGER room_retirements_selection_epoch_guard
        AFTER INSERT OR UPDATE OR DELETE
        ON inventory.room_retirements
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW
        EXECUTE FUNCTION
            inventory.validate_selection_affecting_child_write();

        CREATE TRIGGER manual_block_groups_transition_guard
        BEFORE INSERT OR UPDATE OR DELETE
        ON inventory.manual_block_groups
        FOR EACH ROW
        EXECUTE FUNCTION
            inventory.enforce_manual_block_group_transition();

        CREATE TRIGGER manual_blocks_transition_guard
        BEFORE INSERT OR UPDATE OR DELETE
        ON inventory.manual_blocks
        FOR EACH ROW
        EXECUTE FUNCTION
            inventory.enforce_manual_block_transition();

        CREATE CONSTRAINT TRIGGER manual_block_groups_graph_guard
        AFTER INSERT OR UPDATE OR DELETE
        ON inventory.manual_block_groups
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW
        EXECUTE FUNCTION inventory.validate_manual_block_group_graph();

        CREATE CONSTRAINT TRIGGER manual_blocks_group_graph_guard
        AFTER INSERT OR UPDATE OR DELETE
        ON inventory.manual_blocks
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW
        EXECUTE FUNCTION
            inventory.validate_manual_block_group_member_parent_write();

        CREATE CONSTRAINT TRIGGER management_operations_group_receipt_guard
        AFTER INSERT
        ON inventory.management_operations
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW
        EXECUTE FUNCTION
            inventory.validate_manual_block_group_operation_receipt();
        """;

    internal const string DownGuardSql =
        """
        LOCK TABLE inventory.bed_retirements
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.inventory_units
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.management_operations
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.manual_block_groups
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.manual_blocks
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.property_topology
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.room_configurations
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.room_retirements
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.room_topology
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.tenant_destroy_operations
            IN ACCESS EXCLUSIVE MODE NOWAIT;
        LOCK TABLE inventory.tenant_revisions
            IN ACCESS EXCLUSIVE MODE NOWAIT;

        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM inventory.tenant_destroy_operations operation
                INNER JOIN inventory.tenant_revisions revision
                    ON revision."ScopeId" = operation."ScopeId"
                WHERE revision."LifecycleStatus" = 2
                  AND revision."DestroyOperationId" = operation."OperationId")
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'active Inventory tenant destruction prevents block-group downgrade';
            END IF;

            IF EXISTS (
                SELECT 1
                FROM inventory.tenant_destroy_operations operation
                WHERE operation."Stage" = 21)
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory tenant destruction stage 21 prevents block-group downgrade';
            END IF;

            IF EXISTS (
                SELECT 1
                FROM inventory.management_operations operation
                WHERE operation."Kind" IN (12, 13, 14)) OR
               EXISTS (
                SELECT 1
                FROM inventory.manual_block_groups group_row
                WHERE group_row."TargetKind" <> 0 OR
                      group_row."SelectionDigest" IS NOT NULL OR
                      group_row."Version" <> 1 OR
                      group_row."ReplacesGroupId" IS NOT NULL OR
                      group_row."CreatedByActorId" IS NOT NULL OR
                      group_row."LastModifiedByActorId" IS NOT NULL)
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Cannot downgrade Inventory while manual block-group convergence history exists';
            END IF;

            IF EXISTS (
                SELECT 1
                FROM inventory.tenant_revisions revision
                WHERE revision."ScopeId" IN (
                    SELECT group_row."ScopeId"
                    FROM inventory.manual_block_groups group_row
                    UNION
                    SELECT property."ScopeId"
                    FROM inventory.property_topology property)
                  AND (revision."LifecycleStatus" <> 1 OR
                       revision."Revision" = 9223372036854775807))
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Inventory tenant revision prevents block-group downgrade';
            END IF;
        END;
        $$;

        DROP TRIGGER IF EXISTS manual_blocks_group_graph_guard
            ON inventory.manual_blocks;
        DROP TRIGGER IF EXISTS room_retirements_selection_epoch_guard
            ON inventory.room_retirements;
        DROP TRIGGER IF EXISTS bed_retirements_selection_epoch_guard
            ON inventory.bed_retirements;
        DROP TRIGGER IF EXISTS room_configurations_selection_epoch_guard
            ON inventory.room_configurations;
        DROP TRIGGER IF EXISTS inventory_units_selection_epoch_guard
            ON inventory.inventory_units;
        DROP TRIGGER IF EXISTS room_topology_selection_epoch_guard
            ON inventory.room_topology;
        DROP TRIGGER IF EXISTS property_topology_selection_epoch_guard
            ON inventory.property_topology;
        DROP TRIGGER IF EXISTS manual_block_groups_graph_guard
            ON inventory.manual_block_groups;
        DROP TRIGGER IF EXISTS manual_blocks_transition_guard
            ON inventory.manual_blocks;
        DROP TRIGGER IF EXISTS manual_block_groups_transition_guard
            ON inventory.manual_block_groups;
        DROP TRIGGER IF EXISTS management_operations_append_only
            ON inventory.management_operations;
        DROP TRIGGER IF EXISTS management_operations_group_receipt_guard
            ON inventory.management_operations;
        DROP FUNCTION IF EXISTS
            inventory.validate_manual_block_group_operation_receipt();
        DROP FUNCTION IF EXISTS
            inventory.validate_selection_affecting_child_write();
        DROP FUNCTION IF EXISTS
            inventory.require_availability_selection_parent_write(text, uuid);
        DROP FUNCTION IF EXISTS
            inventory.enforce_availability_selection_epoch();
        DROP FUNCTION IF EXISTS
            inventory.validate_manual_block_group_member_parent_write();
        DROP FUNCTION IF EXISTS
            inventory.validate_manual_block_group_graph();
        DROP FUNCTION IF EXISTS
            inventory.enforce_manual_block_transition();
        DROP FUNCTION IF EXISTS
            inventory.enforce_manual_block_group_transition();
        DROP FUNCTION IF EXISTS
            inventory.enforce_management_operation_append_only();
        DROP FUNCTION IF EXISTS
            inventory.is_authorized_tenant_destruction(text);

        WITH affected AS (
            SELECT group_row."ScopeId"
            FROM inventory.manual_block_groups group_row
            UNION
            SELECT property."ScopeId"
            FROM inventory.property_topology property
        )
        UPDATE inventory.tenant_revisions revision
        SET "Revision" = revision."Revision" + 1
        FROM affected
        WHERE revision."ScopeId" = affected."ScopeId";
        """;
}
