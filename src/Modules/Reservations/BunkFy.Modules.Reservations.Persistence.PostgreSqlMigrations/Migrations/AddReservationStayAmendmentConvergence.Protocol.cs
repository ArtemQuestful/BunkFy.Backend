namespace BunkFy.Modules.Reservations.Persistence.PostgreSqlMigrations.Migrations;

public partial class AddReservationStayAmendmentConvergence
{
    internal const string UpLockSql =
        """
        LOCK TABLE reservations.tenant_revisions
            IN SHARE MODE NOWAIT;
        LOCK TABLE reservations.management_operations
            IN SHARE MODE NOWAIT;
        LOCK TABLE reservations.reservations
            IN SHARE MODE NOWAIT;
        """;

    internal const string UpBackfillSql =
        """
        DO $$
        BEGIN
            IF EXISTS (
                SELECT reservation."PendingAllocationAmendmentId"
                FROM reservations.reservations reservation
                WHERE reservation."PendingAllocationAmendmentId" IS NOT NULL
                GROUP BY reservation."PendingAllocationAmendmentId"
                HAVING count(*) > 1)
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'duplicate active Inventory amendment coordinate prevents upgrade';
            END IF;
        END;
        $$;

        UPDATE reservations.reservations
        SET "PendingInventoryAmendmentRequestId" =
            "PendingAllocationAmendmentId"
        WHERE "PendingAllocationAmendmentId" IS NOT NULL;
        """;

    internal const string UpProtocolSql =
        """
        CREATE OR REPLACE FUNCTION
            reservations.is_authorized_tenant_destruction(
                target_scope_id text)
        RETURNS boolean
        LANGUAGE sql
        STABLE
        AS $$
            SELECT EXISTS (
                SELECT 1
                FROM reservations.tenant_destroy_operations operation
                INNER JOIN reservations.tenant_revisions state
                    ON state."ScopeId" = operation."ScopeId"
                WHERE operation."OperationId"::text = current_setting(
                          'bunkfy.reservations_tenant_destroy_operation_id',
                          true)
                  AND operation."ScopeId" = target_scope_id
                  AND state."LifecycleStatus" = 2
                  AND state."DestroyOperationId" = operation."OperationId"
                  AND state."DestroyRequestSha256" = operation."RequestSha256");
        $$;

        CREATE OR REPLACE FUNCTION
            reservations.enforce_stay_amendment_transition()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                IF reservations.is_authorized_tenant_destruction(
                       OLD."ScopeId")
                THEN
                    RETURN OLD;
                END IF;

                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'stay-amendment evidence cannot be deleted';
            END IF;

            IF TG_OP = 'INSERT' THEN
                IF NEW."OperationVersion" <> 1 OR
                   NEW."ReconciliationCount" <> 0 OR
                   NEW."UpdatedAtUtc" <> NEW."RequestedAtUtc" OR
                   NEW."LastReconciledAtUtc" IS NOT NULL OR
                   NEW."LastReconciledBy" IS NOT NULL
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'stay-amendment initial evidence is invalid';
                END IF;

                IF NEW."Outcome" = 1 AND
                   NEW."CompletedAtUtc" IS NULL AND
                   NEW."ResultingDetailsRevision" IS NULL AND
                   NEW."ResultingReservationVersion" IS NULL AND
                   NEW."ResultingAllocationVersion" IS NULL AND
                   NEW."RejectionCode" IS NULL
                THEN
                    RETURN NEW;
                END IF;

                IF NEW."Outcome" = 2 AND
                   NEW."RequestSchemaVersion" = 2 AND
                   NEW."CompletedAtUtc" = NEW."RequestedAtUtc" AND
                   NEW."ResultingDetailsRevision" IS NOT NULL AND
                   NEW."ResultingReservationVersion" IS NOT NULL AND
                   NEW."ResultingAllocationVersion" IS NOT NULL AND
                   NEW."RejectionCode" IS NULL
                THEN
                    RETURN NEW;
                END IF;

                IF NEW."Outcome" = 4 AND
                   NEW."RequestSchemaVersion" = 1 AND
                   NEW."CompletedAtUtc" IS NULL AND
                   NEW."ResultingDetailsRevision" IS NULL AND
                   NEW."ResultingReservationVersion" IS NULL AND
                   NEW."ResultingAllocationVersion" IS NULL AND
                   NEW."RejectionCode" IS NULL
                THEN
                    RETURN NEW;
                END IF;

                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'stay-amendment initial outcome is invalid';
            END IF;

            IF (NEW."ScopeId", NEW."ReservationId", NEW."Id",
                NEW."PropertyId", NEW."InventoryRequestId",
                NEW."RequestSchemaVersion",
                NEW."RequestFingerprint", NEW."TargetArrival",
                NEW."TargetDeparture", NEW."TargetExpectedArrivalTime",
                NEW."TargetExpectedDepartureTime",
                NEW."TargetInventoryUnitIds",
                NEW."ExpectedDetailsRevision", NEW."RequestedBy",
                NEW."RequestedAtUtc")
            IS DISTINCT FROM
               (OLD."ScopeId", OLD."ReservationId", OLD."Id",
                OLD."PropertyId", OLD."InventoryRequestId",
                OLD."RequestSchemaVersion",
                OLD."RequestFingerprint", OLD."TargetArrival",
                OLD."TargetDeparture", OLD."TargetExpectedArrivalTime",
                OLD."TargetExpectedDepartureTime",
                OLD."TargetInventoryUnitIds",
                OLD."ExpectedDetailsRevision", OLD."RequestedBy",
                OLD."RequestedAtUtc")
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'stay-amendment request evidence is immutable';
            END IF;

            IF OLD."Outcome" <> 1 THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'terminal stay-amendment evidence is immutable';
            END IF;

            IF NEW."Outcome" = 1 THEN
                IF NEW."OperationVersion" <> OLD."OperationVersion" + 1 OR
                   NEW."ReconciliationCount" <>
                       OLD."ReconciliationCount" + 1 OR
                   NEW."UpdatedAtUtc" <
                       OLD."UpdatedAtUtc" + interval '5 minutes' OR
                   NEW."LastReconciledAtUtc" <> NEW."UpdatedAtUtc" OR
                   NEW."LastReconciledBy" IS NULL OR
                   (NEW."CompletedAtUtc", NEW."ResultingDetailsRevision",
                    NEW."ResultingReservationVersion",
                    NEW."ResultingAllocationVersion", NEW."RejectionCode")
                   IS DISTINCT FROM
                   (OLD."CompletedAtUtc", OLD."ResultingDetailsRevision",
                    OLD."ResultingReservationVersion",
                    OLD."ResultingAllocationVersion", OLD."RejectionCode")
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'stay-amendment reconciliation advance is invalid';
                END IF;

                RETURN NEW;
            END IF;

            IF NEW."Outcome" IN (2, 3) THEN
                IF NEW."OperationVersion" <> OLD."OperationVersion" + 1 OR
                   NEW."ReconciliationCount" <>
                       OLD."ReconciliationCount" OR
                   NEW."LastReconciledAtUtc" IS DISTINCT FROM
                       OLD."LastReconciledAtUtc" OR
                   NEW."LastReconciledBy" IS DISTINCT FROM
                       OLD."LastReconciledBy" OR
                   NEW."UpdatedAtUtc" < OLD."UpdatedAtUtc" OR
                   NEW."CompletedAtUtc" <> NEW."UpdatedAtUtc"
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'stay-amendment terminal advance is invalid';
                END IF;

                RETURN NEW;
            END IF;

            RAISE EXCEPTION USING
                ERRCODE = 'P0001',
                MESSAGE = 'stay-amendment outcome transition is invalid';
        END;
        $$;

        CREATE OR REPLACE FUNCTION
            reservations.enforce_reservation_version_advance()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            IF NEW."Version" <> OLD."Version" + 1 THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'reservation updates require a version advance';
            END IF;

            RETURN NEW;
        END;
        $$;

        CREATE OR REPLACE FUNCTION
            reservations.validate_stay_amendment_coordinates()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        DECLARE
            parent_kind integer;
            parent_property_id uuid;
            parent_expected_revision bigint;
            parent_fingerprint text;
            reservation_property_id uuid;
            reservation_status integer;
            reservation_details_revision bigint;
            reservation_version bigint;
            reservation_allocation_version bigint;
            reservation_arrival date;
            reservation_departure date;
            reservation_expected_arrival time without time zone;
            reservation_expected_departure time without time zone;
            reservation_pending_id uuid;
            reservation_pending_inventory_id uuid;
            reservation_pending_fingerprint text;
            reservation_pending_arrival date;
            reservation_pending_departure date;
            reservation_pending_expected_arrival time without time zone;
            reservation_pending_expected_departure time without time zone;
            reservation_pending_units text;
            reservation_pending_origin integer;
            reservation_pending_actor text;
            reservation_last_rejection integer;
            reservation_xmin xid;
            reservation_xmin_status text;
            target_unit_count integer;
            current_unit_count integer;
            canonical_units text;
            expected_fingerprint text;
        BEGIN
            SELECT parent."Kind", parent."PropertyId",
                   parent."ExpectedDetailsRevision",
                   parent."RequestFingerprint"
            INTO parent_kind, parent_property_id,
                 parent_expected_revision, parent_fingerprint
            FROM reservations.management_operations parent
            WHERE parent."ScopeId" = NEW."ScopeId"
              AND parent."ReservationId" = NEW."ReservationId"
              AND parent."Id" = NEW."Id"
            FOR UPDATE;

            IF NOT FOUND OR
               parent_kind <> (CASE
                   WHEN NEW."RequestSchemaVersion" = 1 THEN 6
                   WHEN NEW."RequestSchemaVersion" = 2 THEN 7
                   ELSE 0
               END) OR
               parent_property_id <> NEW."PropertyId" OR
               parent_expected_revision <>
                   NEW."ExpectedDetailsRevision" OR
               parent_fingerprint <> NEW."RequestFingerprint"
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'stay-amendment parent coordinates do not match';
            END IF;

            SELECT reservation."PropertyId", reservation."Status",
                   reservation."DetailsRevision", reservation."Version",
                   reservation."AllocationVersion",
                   reservation."Arrival", reservation."Departure",
                   reservation."ExpectedArrivalTime",
                   reservation."ExpectedDepartureTime",
                   reservation."PendingAllocationAmendmentId",
                   reservation."PendingInventoryAmendmentRequestId",
                   reservation."PendingAllocationAmendmentRequestFingerprint",
                   reservation."PendingArrival",
                   reservation."PendingDeparture",
                   reservation."PendingExpectedArrivalTime",
                   reservation."PendingExpectedDepartureTime",
                   reservation."PendingInventoryUnitIds",
                   reservation."PendingDetailsChangeOrigin",
                   reservation."PendingDetailsActorId",
                   reservation."LastAllocationAmendmentRejectionCode",
                   reservation.xmin
            INTO reservation_property_id, reservation_status,
                 reservation_details_revision, reservation_version,
                 reservation_allocation_version,
                 reservation_arrival, reservation_departure,
                 reservation_expected_arrival,
                 reservation_expected_departure,
                 reservation_pending_id,
                 reservation_pending_inventory_id,
                 reservation_pending_fingerprint,
                 reservation_pending_arrival,
                 reservation_pending_departure,
                 reservation_pending_expected_arrival,
                 reservation_pending_expected_departure,
                 reservation_pending_units,
                 reservation_pending_origin,
                 reservation_pending_actor,
                 reservation_last_rejection,
                 reservation_xmin
            FROM reservations.reservations reservation
            WHERE reservation."ScopeId" = NEW."ScopeId"
              AND reservation."Id" = NEW."ReservationId"
            FOR UPDATE;

            IF NOT FOUND OR
               reservation_property_id <> NEW."PropertyId"
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'stay-amendment reservation coordinates do not match';
            END IF;

            reservation_xmin_status := pg_xact_status(
                reservation_xmin::text::xid8);

            IF NEW."Outcome" = 4 THEN
                RETURN NEW;
            END IF;

            target_unit_count := cardinality(
                string_to_array(NEW."TargetInventoryUnitIds", ','));
            SELECT string_agg(target.value, ',' ORDER BY target.value)
            INTO canonical_units
            FROM unnest(string_to_array(
                     NEW."TargetInventoryUnitIds", ',')) target(value);

            IF target_unit_count <> (
                   SELECT count(DISTINCT target.value)::integer
                   FROM unnest(string_to_array(
                            NEW."TargetInventoryUnitIds", ',')) target(value)) OR
               NEW."TargetInventoryUnitIds" <> canonical_units
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'stay-amendment target units must be canonical';
            END IF;

            expected_fingerprint := CASE NEW."RequestSchemaVersion"
                WHEN 1 THEN encode(sha256(convert_to(
                    replace(NEW."ReservationId"::text, '-', '') || '|' ||
                    replace(NEW."Id"::text, '-', '') || '|' ||
                    NEW."ExpectedDetailsRevision"::text || '|' ||
                    canonical_units,
                    'UTF8')), 'hex')
                WHEN 2 THEN encode(sha256(convert_to(
                    'v2|reservation=' ||
                    replace(NEW."ReservationId"::text, '-', '') ||
                    '|operation=' || replace(NEW."Id"::text, '-', '') ||
                    '|expected-details-revision=' ||
                    NEW."ExpectedDetailsRevision"::text ||
                    '|arrival=' || to_char(NEW."TargetArrival", 'YYYY-MM-DD') ||
                    '|departure=' || to_char(NEW."TargetDeparture", 'YYYY-MM-DD') ||
                    '|expected-arrival-time=' || coalesce(
                        to_char(NEW."TargetExpectedArrivalTime", 'HH24:MI'), '-') ||
                    '|expected-departure-time=' || coalesce(
                        to_char(NEW."TargetExpectedDepartureTime", 'HH24:MI'), '-') ||
                    '|units=' || canonical_units,
                    'UTF8')), 'hex')
                ELSE NULL
            END;

            IF NEW."RequestFingerprint" IS DISTINCT FROM expected_fingerprint
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'stay-amendment request fingerprint does not match';
            END IF;

            IF reservation_status <> 2 THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'stay-amendment requires a confirmed reservation';
            END IF;

            IF NEW."Outcome" = 1 THEN
                IF reservation_pending_id IS DISTINCT FROM NEW."Id" OR
                   reservation_pending_inventory_id IS DISTINCT FROM
                       NEW."InventoryRequestId" OR
                   reservation_pending_fingerprint IS DISTINCT FROM
                       NEW."RequestFingerprint" OR
                   reservation_pending_arrival IS DISTINCT FROM
                       NEW."TargetArrival" OR
                   reservation_pending_departure IS DISTINCT FROM
                       NEW."TargetDeparture" OR
                   reservation_pending_expected_arrival IS DISTINCT FROM
                       NEW."TargetExpectedArrivalTime" OR
                   reservation_pending_expected_departure IS DISTINCT FROM
                       NEW."TargetExpectedDepartureTime" OR
                   reservation_pending_units IS DISTINCT FROM
                       NEW."TargetInventoryUnitIds" OR
                   reservation_pending_origin <> 1 OR
                   reservation_pending_actor IS DISTINCT FROM
                       NEW."RequestedBy"
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'pending stay-amendment candidate does not match';
                END IF;

                IF reservation_xmin_status <> 'in progress'
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'stay-amendment graph change requires a reservation version advance';
                END IF;

                RETURN NEW;
            END IF;

            IF reservation_pending_id IS NOT NULL OR
               reservation_pending_inventory_id IS NOT NULL OR
               reservation_details_revision IS DISTINCT FROM
                   NEW."ResultingDetailsRevision" OR
               reservation_version IS DISTINCT FROM
                   NEW."ResultingReservationVersion"
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'terminal stay-amendment result does not match';
            END IF;

            IF NEW."Outcome" = 3 THEN
                IF reservation_last_rejection IS DISTINCT FROM
                       NEW."RejectionCode"
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'rejected stay-amendment result does not match';
                END IF;

                IF reservation_xmin_status <> 'in progress'
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'stay-amendment graph change requires a reservation version advance';
                END IF;

                RETURN NEW;
            END IF;

            IF reservation_allocation_version IS DISTINCT FROM
                   NEW."ResultingAllocationVersion"
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'applied stay-amendment allocation version does not match';
            END IF;

            IF reservation_arrival IS DISTINCT FROM
                   NEW."TargetArrival" OR
               reservation_departure IS DISTINCT FROM
                   NEW."TargetDeparture" OR
               reservation_expected_arrival IS DISTINCT FROM
                   NEW."TargetExpectedArrivalTime" OR
               reservation_expected_departure IS DISTINCT FROM
                   NEW."TargetExpectedDepartureTime"
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'applied stay-amendment target does not match';
            END IF;

            SELECT count(*)::integer
            INTO current_unit_count
            FROM reservations.requested_inventory_units unit
            WHERE unit."ScopeId" = NEW."ScopeId"
              AND unit."ReservationId" = NEW."ReservationId";

            IF current_unit_count <> target_unit_count OR EXISTS (
                SELECT 1
                FROM unnest(string_to_array(
                         NEW."TargetInventoryUnitIds", ',')) target(value)
                WHERE NOT EXISTS (
                    SELECT 1
                    FROM reservations.requested_inventory_units unit
                    WHERE unit."ScopeId" = NEW."ScopeId"
                      AND unit."ReservationId" = NEW."ReservationId"
                      AND replace(unit."Id"::text, '-', '') =
                          target.value))
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'applied stay-amendment units do not match';
            END IF;

            IF reservation_xmin_status <> 'in progress'
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'stay-amendment graph change requires a reservation version advance';
            END IF;

            RETURN NEW;
        END;
        $$;

        CREATE OR REPLACE FUNCTION
            reservations.protect_stay_amendment_parent()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            IF TG_OP = 'UPDATE' AND
               (OLD."Kind" IN (6, 7) OR NEW."Kind" IN (6, 7)) THEN
                IF (NEW."ScopeId", NEW."ReservationId", NEW."Id",
                    NEW."PropertyId", NEW."Kind",
                    NEW."ExpectedVersion",
                    NEW."ExpectedDetailsRevision", NEW."BusinessDate",
                    NEW."CreatedAtUtc", NEW."RequestFingerprint")
                   IS DISTINCT FROM
                   (OLD."ScopeId", OLD."ReservationId", OLD."Id",
                    OLD."PropertyId", OLD."Kind",
                    OLD."ExpectedVersion",
                    OLD."ExpectedDetailsRevision", OLD."BusinessDate",
                    OLD."CreatedAtUtc", OLD."RequestFingerprint")
                THEN
                    RAISE EXCEPTION USING
                        ERRCODE = 'P0001',
                        MESSAGE = 'amendment parent coordinates are immutable';
                END IF;
            END IF;

            IF EXISTS (
                SELECT 1
                FROM reservations.stay_amendment_operations child
                WHERE child."ScopeId" = OLD."ScopeId"
                  AND child."ReservationId" = OLD."ReservationId"
                  AND child."Id" = OLD."Id")
            THEN
                IF TG_OP = 'DELETE' AND
                   reservations.is_authorized_tenant_destruction(
                       OLD."ScopeId")
                THEN
                    RETURN OLD;
                END IF;

                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'stay-amendment parent evidence is immutable';
            END IF;

            IF TG_OP = 'DELETE' THEN
                RETURN OLD;
            END IF;

            RETURN NEW;
        END;
        $$;

        CREATE OR REPLACE FUNCTION
            reservations.require_stay_amendment_child()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            IF NEW."Kind" IN (6, 7) AND NOT EXISTS (
                SELECT 1
                FROM reservations.stay_amendment_operations child
                WHERE child."ScopeId" = NEW."ScopeId"
                  AND child."ReservationId" = NEW."ReservationId"
                  AND child."Id" = NEW."Id")
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'stay-amendment parent requires exact child evidence';
            END IF;

            RETURN NEW;
        END;
        $$;

        CREATE OR REPLACE FUNCTION
            reservations.validate_reservation_pending_stay_amendment()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        DECLARE
            live_reservation reservations.reservations%ROWTYPE;
        BEGIN
            IF OLD."PendingAllocationAmendmentId" IS NOT NULL AND
               NEW."PendingAllocationAmendmentId" IS NOT NULL AND
               (NEW."PendingAllocationAmendmentId" IS DISTINCT FROM
                    OLD."PendingAllocationAmendmentId" OR
                (NEW."PendingInventoryAmendmentRequestId" IS DISTINCT FROM
                     OLD."PendingInventoryAmendmentRequestId" AND NOT (
                     OLD."PendingInventoryAmendmentRequestId" IS NULL AND
                     NEW."PendingInventoryAmendmentRequestId" =
                         NEW."PendingAllocationAmendmentId")))
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'active reservation amendment coordinates are immutable';
            END IF;

            SELECT reservation.*
            INTO live_reservation
            FROM reservations.reservations reservation
            WHERE reservation."ScopeId" = NEW."ScopeId"
              AND reservation."Id" = NEW."Id"
            FOR UPDATE;

            IF NOT FOUND THEN
                RETURN NEW;
            END IF;

            IF EXISTS (
                SELECT 1
                FROM reservations.stay_amendment_operations child
                WHERE child."ScopeId" = live_reservation."ScopeId"
                  AND child."ReservationId" = live_reservation."Id"
                  AND child."Outcome" = 1
                  AND (live_reservation."PropertyId" <> child."PropertyId" OR
                       live_reservation."Status" <> 2 OR
                       live_reservation."PendingAllocationAmendmentId" IS DISTINCT FROM
                           child."Id" OR
                       live_reservation."PendingInventoryAmendmentRequestId" IS DISTINCT FROM
                           child."InventoryRequestId" OR
                       live_reservation."PendingAllocationAmendmentRequestFingerprint"
                           IS DISTINCT FROM child."RequestFingerprint" OR
                       live_reservation."PendingArrival" IS DISTINCT FROM
                           child."TargetArrival" OR
                       live_reservation."PendingDeparture" IS DISTINCT FROM
                           child."TargetDeparture" OR
                       live_reservation."PendingExpectedArrivalTime" IS DISTINCT FROM
                           child."TargetExpectedArrivalTime" OR
                       live_reservation."PendingExpectedDepartureTime" IS DISTINCT FROM
                           child."TargetExpectedDepartureTime" OR
                       live_reservation."PendingInventoryUnitIds" IS DISTINCT FROM
                           child."TargetInventoryUnitIds" OR
                       live_reservation."PendingDetailsChangeOrigin" <> 1 OR
                       live_reservation."PendingDetailsActorId" IS DISTINCT FROM
                           child."RequestedBy"))
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'reservation pending stay-amendment evidence does not match';
            END IF;

            IF live_reservation."PendingAllocationAmendmentId" IS NOT NULL AND
               live_reservation."PendingDetailsChangeOrigin" = 1 AND
               NOT EXISTS (
                   SELECT 1
                   FROM reservations.stay_amendment_operations child
                   WHERE child."ScopeId" = live_reservation."ScopeId"
                     AND child."ReservationId" = live_reservation."Id"
                     AND child."Id" =
                         live_reservation."PendingAllocationAmendmentId"
                     AND child."InventoryRequestId" =
                         live_reservation."PendingInventoryAmendmentRequestId"
                     AND child."Outcome" = 1)
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'Staff stay-amendment pending state requires durable evidence';
            END IF;

            RETURN NEW;
        END;
        $$;

        CREATE TRIGGER stay_amendment_operations_transition
        BEFORE INSERT OR UPDATE OR DELETE
        ON reservations.stay_amendment_operations
        FOR EACH ROW
        EXECUTE FUNCTION
            reservations.enforce_stay_amendment_transition();

        CREATE TRIGGER reservation_version_monotonic
        BEFORE UPDATE
        ON reservations.reservations
        FOR EACH ROW
        EXECUTE FUNCTION
            reservations.enforce_reservation_version_advance();

        CREATE CONSTRAINT TRIGGER
            stay_amendment_operations_coordinates
        AFTER INSERT OR UPDATE
        ON reservations.stay_amendment_operations
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW
        EXECUTE FUNCTION
            reservations.validate_stay_amendment_coordinates();

        CREATE TRIGGER stay_amendment_parent_immutable
        BEFORE UPDATE OR DELETE
        ON reservations.management_operations
        FOR EACH ROW
        EXECUTE FUNCTION
            reservations.protect_stay_amendment_parent();

        CREATE CONSTRAINT TRIGGER stay_amendment_parent_complete
        AFTER INSERT OR UPDATE
        ON reservations.management_operations
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW
        EXECUTE FUNCTION
            reservations.require_stay_amendment_child();

        CREATE CONSTRAINT TRIGGER reservation_pending_stay_amendment
        AFTER INSERT OR UPDATE
        ON reservations.reservations
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW
        EXECUTE FUNCTION
            reservations.validate_reservation_pending_stay_amendment();

        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM reservations.reservations reservation
                WHERE (reservation."PendingAllocationAmendmentId" IS NOT NULL OR
                       EXISTS (
                      SELECT 1
                      FROM reservations.management_operations parent
                      WHERE parent."ScopeId" = reservation."ScopeId"
                        AND parent."ReservationId" = reservation."Id"
                        AND parent."Kind" = 6))
                  AND reservation."Version" = 9223372036854775807)
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'reservation version overflow prevents stay-amendment backfill';
            END IF;
        END;
        $$;

        WITH affected_reservations AS (
            SELECT reservation."ScopeId", reservation."Id"
            FROM reservations.reservations reservation
            WHERE reservation."PendingAllocationAmendmentId" IS NOT NULL OR
                  EXISTS (
                      SELECT 1
                      FROM reservations.management_operations parent
                      WHERE parent."ScopeId" = reservation."ScopeId"
                        AND parent."ReservationId" = reservation."Id"
                        AND parent."Kind" = 6))
        UPDATE reservations.reservations target
        SET "Version" = target."Version" + 1
        FROM affected_reservations affected
        WHERE target."ScopeId" = affected."ScopeId"
          AND target."Id" = affected."Id";

        INSERT INTO reservations.stay_amendment_operations
            ("Id", "ScopeId", "ReservationId", "PropertyId",
             "InventoryRequestId",
             "RequestSchemaVersion", "RequestFingerprint",
             "TargetArrival", "TargetDeparture",
             "TargetExpectedArrivalTime",
             "TargetExpectedDepartureTime",
             "TargetInventoryUnitIds", "ExpectedDetailsRevision",
             "RequestedBy", "Outcome", "OperationVersion",
             "RequestedAtUtc", "UpdatedAtUtc", "CompletedAtUtc",
             "ResultingDetailsRevision",
             "ResultingReservationVersion", "ResultingAllocationVersion",
             "RejectionCode",
             "ReconciliationCount", "LastReconciledAtUtc",
             "LastReconciledBy")
        SELECT parent."Id", parent."ScopeId", parent."ReservationId",
               parent."PropertyId",
               CASE WHEN pending.is_exact
                    THEN reservation."PendingInventoryAmendmentRequestId"
                    ELSE NULL END,
               1, parent."RequestFingerprint",
               CASE WHEN pending.is_exact THEN reservation."PendingArrival"
                    ELSE NULL END,
               CASE WHEN pending.is_exact THEN reservation."PendingDeparture"
                    ELSE NULL END,
               CASE WHEN pending.is_exact
                    THEN reservation."PendingExpectedArrivalTime"
                    ELSE NULL END,
               CASE WHEN pending.is_exact
                    THEN reservation."PendingExpectedDepartureTime"
                    ELSE NULL END,
               CASE WHEN pending.is_exact
                    THEN reservation."PendingInventoryUnitIds"
                    ELSE NULL END,
               parent."ExpectedDetailsRevision",
               CASE WHEN pending.is_exact
                    THEN reservation."PendingDetailsActorId"
                    ELSE NULL END,
               CASE WHEN pending.is_exact THEN 1 ELSE 4 END,
               1,
               parent."CreatedAtUtc", parent."CreatedAtUtc", NULL,
               NULL, NULL, NULL, NULL, 0, NULL, NULL
        FROM reservations.management_operations parent
        INNER JOIN reservations.reservations reservation
            ON reservation."ScopeId" = parent."ScopeId"
           AND reservation."Id" = parent."ReservationId"
        CROSS JOIN LATERAL (
            SELECT
                parent."PropertyId" = reservation."PropertyId" AND
                reservation."Status" = 2 AND
                reservation."PendingAllocationAmendmentId" = parent."Id" AND
                reservation."PendingInventoryAmendmentRequestId" IS NOT NULL AND
                reservation."PendingAllocationAmendmentRequestFingerprint" =
                    parent."RequestFingerprint" AND
                reservation."PendingArrival" IS NOT NULL AND
                reservation."PendingDeparture" IS NOT NULL AND
                reservation."PendingArrival" <
                    reservation."PendingDeparture" AND
                reservation."PendingInventoryUnitIds" ~
                    '^[0-9a-f]{32}(,[0-9a-f]{32}){0,99}$' AND
                reservation."PendingDetailsChangeOrigin" = 1 AND
                reservation."PendingDetailsActorId" IS NOT NULL AND
                reservation."PendingDetailsActorId" =
                    trim(reservation."PendingDetailsActorId") AND
                char_length(reservation."PendingDetailsActorId") > 0 AND
                reservation."PendingDetailsActorId" !~ '[[:cntrl:]]' AND
                parent."ExpectedDetailsRevision" =
                    reservation."DetailsRevision"
                    AS is_exact) pending
        WHERE parent."Kind" = 6;

        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM reservations.reservations reservation
                WHERE reservation."PendingAllocationAmendmentId" IS NOT NULL
                  AND reservation."PendingDetailsChangeOrigin" = 1
                  AND NOT EXISTS (
                      SELECT 1
                      FROM reservations.stay_amendment_operations child
                      WHERE child."ScopeId" = reservation."ScopeId"
                        AND child."ReservationId" = reservation."Id"
                        AND child."Id" =
                            reservation."PendingAllocationAmendmentId"
                        AND child."InventoryRequestId" =
                            reservation."PendingInventoryAmendmentRequestId"
                        AND child."Outcome" = 1
                        AND child."PropertyId" = reservation."PropertyId"
                        AND child."RequestFingerprint" =
                            reservation."PendingAllocationAmendmentRequestFingerprint"
                        AND child."TargetArrival" = reservation."PendingArrival"
                        AND child."TargetDeparture" = reservation."PendingDeparture"
                        AND child."TargetExpectedArrivalTime" IS NOT DISTINCT FROM
                            reservation."PendingExpectedArrivalTime"
                        AND child."TargetExpectedDepartureTime" IS NOT DISTINCT FROM
                            reservation."PendingExpectedDepartureTime"
                        AND child."TargetInventoryUnitIds" =
                            reservation."PendingInventoryUnitIds"
                        AND child."RequestedBy" =
                            reservation."PendingDetailsActorId"))
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'nonexact active Staff stay amendment prevents upgrade';
            END IF;
        END;
        $$;
        """;

    internal const string DownGuardSql =
        """
        LOCK TABLE reservations.tenant_revisions
            IN SHARE MODE NOWAIT;
        LOCK TABLE reservations.management_operations
            IN SHARE MODE NOWAIT;
        LOCK TABLE reservations.reservations
            IN SHARE MODE NOWAIT;
        LOCK TABLE reservations.stay_amendment_operations
            IN SHARE MODE NOWAIT;

        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM reservations.management_operations parent
                WHERE parent."Kind" = 7) OR EXISTS (
                SELECT 1
                FROM reservations.stay_amendment_operations child
                WHERE child."RequestSchemaVersion" <> 1
                   OR child."Outcome" <> 4
                   OR child."OperationVersion" <> 1
                   OR child."ReconciliationCount" <> 0) OR EXISTS (
                SELECT 1
                FROM reservations.reservations reservation
                WHERE reservation."PendingInventoryAmendmentRequestId" IS NOT NULL
                  AND reservation."PendingInventoryAmendmentRequestId"
                      IS DISTINCT FROM
                          reservation."PendingAllocationAmendmentId")
            THEN
                RAISE EXCEPTION USING
                    ERRCODE = 'P0001',
                    MESSAGE = 'stay-amendment convergence evidence prevents downgrade';
            END IF;
        END;
        $$;

        DROP TRIGGER IF EXISTS stay_amendment_parent_immutable
            ON reservations.management_operations;
        DROP TRIGGER IF EXISTS stay_amendment_parent_complete
            ON reservations.management_operations;
        DROP TRIGGER IF EXISTS reservation_pending_stay_amendment
            ON reservations.reservations;
        DROP TRIGGER IF EXISTS reservation_version_monotonic
            ON reservations.reservations;
        DROP FUNCTION IF EXISTS
            reservations.protect_stay_amendment_parent();
        DROP FUNCTION IF EXISTS
            reservations.require_stay_amendment_child();
        DROP FUNCTION IF EXISTS
            reservations.validate_reservation_pending_stay_amendment();
        DROP FUNCTION IF EXISTS
            reservations.enforce_reservation_version_advance();
        """;

    internal const string DownCleanupSql =
        """
        DROP FUNCTION IF EXISTS
            reservations.validate_stay_amendment_coordinates();
        DROP FUNCTION IF EXISTS
            reservations.enforce_stay_amendment_transition();
        DROP FUNCTION IF EXISTS
            reservations.is_authorized_tenant_destruction(text);
        """;
}
