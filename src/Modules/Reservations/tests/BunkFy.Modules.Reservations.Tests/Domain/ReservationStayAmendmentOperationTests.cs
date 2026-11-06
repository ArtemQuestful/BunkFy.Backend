namespace BunkFy.Modules.Reservations.Tests;

using BunkFy.Modules.Reservations.Domain.Errors;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationStayAmendmentOperationTests
{
    [Fact]
    public void Pending_operation_canonicalizes_target_and_accepts_one_rate_bounded_reconcile()
    {
        Guid firstUnitId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid secondUnitId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        ReservationStayAmendmentOperation operation = CreatePending([secondUnitId, firstUnitId]);

        Assert.Equal(ReservationStayAmendmentOperationOutcome.Pending, operation.Outcome);
        Assert.Equal(1, operation.OperationVersion);
        Assert.Equal([firstUnitId, secondUnitId], operation.GetTargetInventoryUnitIds());
        Assert.False(operation.IsReconciliationEligible(Now.AddMinutes(4)));
        Assert.Equal(
            ReservationsDomainErrors.StayAmendmentReconcileTooSoon,
            operation.Reconcile(1, Actor, Now.AddMinutes(4)).Error);

        Result reconciled = operation.Reconcile(1, Actor, Now.AddMinutes(5));

        Assert.True(reconciled.IsSuccess, reconciled.Error.Code);
        Assert.Equal(2, operation.OperationVersion);
        Assert.Equal(1, operation.ReconciliationCount);
        Assert.Equal(Now.AddMinutes(5), operation.LastReconciledAtUtc);
        Assert.Equal(Actor, operation.LastReconciledBy);
        Assert.Equal(Now.AddMinutes(10), operation.NextReconciliationEligibleAtUtc);
    }

    [Fact]
    public void Reconcile_rejects_stale_version_and_terminal_outcomes()
    {
        ReservationStayAmendmentOperation operation = CreatePending([Guid.NewGuid()]);

        Assert.Equal(
            ReservationsDomainErrors.StayAmendmentOperationVersionConflict,
            operation.Reconcile(2, Actor, Now.AddMinutes(5)).Error);
        Assert.True(operation.MarkRejected(
            rejectionCode: 2,
            resultingDetailsRevision: 1,
            resultingReservationVersion: 4,
            Now.AddMinutes(1)).IsSuccess);

        Assert.Equal(
            ReservationsDomainErrors.StayAmendmentOperationTransitionInvalid,
            operation.Reconcile(2, Actor, Now.AddMinutes(6)).Error);
    }

    [Fact]
    public void Confirmation_must_match_the_durable_target_and_is_exactly_idempotent()
    {
        Guid unitId = Guid.NewGuid();
        ReservationStayAmendmentOperation operation = CreatePending([unitId]);

        Assert.Equal(
            ReservationsDomainErrors.StayAmendmentOutcomeMismatch,
            operation.MarkApplied(
                Arrival,
                Departure.AddDays(1),
                new TimeOnly(15, 0),
                new TimeOnly(10, 0),
                [unitId],
                resultingAllocationVersion: 2,
                resultingDetailsRevision: 2,
                resultingReservationVersion: 4,
                Now.AddMinutes(1)).Error);

        Result<bool> applied = operation.MarkApplied(
            Arrival,
            Departure,
            new TimeOnly(15, 0),
            new TimeOnly(10, 0),
            [unitId],
            resultingAllocationVersion: 2,
            resultingDetailsRevision: 2,
            resultingReservationVersion: 4,
            Now.AddMinutes(1));
        Result<bool> replay = operation.MarkApplied(
            Arrival,
            Departure,
            new TimeOnly(15, 0),
            new TimeOnly(10, 0),
            [unitId],
            resultingAllocationVersion: 2,
            resultingDetailsRevision: 2,
            resultingReservationVersion: 4,
            Now.AddMinutes(2));

        Assert.True(applied.Value);
        Assert.False(replay.Value);
        Assert.Equal(ReservationStayAmendmentOperationOutcome.Applied, operation.Outcome);
        Assert.Equal(2, operation.OperationVersion);
        Assert.Equal(Now.AddMinutes(1), operation.CompletedAtUtc);
    }

    [Fact]
    public void Rejection_records_resulting_reservation_truth_without_changing_target()
    {
        Guid unitId = Guid.NewGuid();
        ReservationStayAmendmentOperation operation = CreatePending([unitId]);

        Result<bool> rejected = operation.MarkRejected(
            rejectionCode: 3,
            resultingDetailsRevision: 1,
            resultingReservationVersion: 4,
            Now.AddMinutes(1));

        Assert.True(rejected.Value);
        Assert.Equal(ReservationStayAmendmentOperationOutcome.Rejected, operation.Outcome);
        Assert.Equal(3, operation.RejectionCode);
        Assert.Equal(1, operation.ResultingDetailsRevision);
        Assert.Equal(4, operation.ResultingReservationVersion);
        Assert.Equal([unitId], operation.GetTargetInventoryUnitIds());
    }

    [Fact]
    public void No_op_is_created_applied_and_never_becomes_pending()
    {
        Result<ReservationStayAmendmentOperation> created =
            ReservationStayAmendmentOperation.CreateAppliedNoOp(
                OperationId,
                TenantId,
                PropertyId,
                ReservationId,
                ReservationStayAmendmentOperation.CurrentRequestSchemaVersion,
                Fingerprint,
                Arrival,
                Departure,
                new TimeOnly(15, 0),
                new TimeOnly(10, 0),
                [Guid.NewGuid()],
                expectedDetailsRevision: 3,
                Actor,
                resultingDetailsRevision: 3,
                resultingReservationVersion: 8,
                resultingAllocationVersion: 5,
                Now);

        Assert.True(created.IsSuccess, created.Error.Code);
        Assert.Equal(ReservationStayAmendmentOperationOutcome.Applied, created.Value.Outcome);
        Assert.Equal(Now, created.Value.CompletedAtUtc);
        Assert.False(created.Value.IsReconciliationEligible(Now.AddDays(1)));
        Assert.Equal(
            ReservationsDomainErrors.StayAmendmentOperationTransitionInvalid,
            created.Value.Reconcile(1, Actor, Now.AddDays(1)).Error);
    }

    [Fact]
    public void Historical_unknown_preserves_absent_target_and_actor()
    {
        Result<ReservationStayAmendmentOperation> created =
            ReservationStayAmendmentOperation.CreateOutcomeUnknown(
                OperationId,
                TenantId,
                PropertyId,
                ReservationId,
                ReservationStayAmendmentOperation.LegacyRequestSchemaVersion,
                Fingerprint,
                expectedDetailsRevision: 1,
                Now);

        Assert.True(created.IsSuccess, created.Error.Code);
        Assert.Equal(ReservationStayAmendmentOperationOutcome.OutcomeUnknown, created.Value.Outcome);
        Assert.Null(created.Value.TargetArrival);
        Assert.Null(created.Value.TargetDeparture);
        Assert.Null(created.Value.TargetInventoryUnitIds);
        Assert.Null(created.Value.RequestedBy);
        Assert.Empty(created.Value.GetTargetInventoryUnitIds());
    }

    [Fact]
    public void Historical_unknown_rejects_the_current_request_schema()
    {
        Result<ReservationStayAmendmentOperation> created =
            ReservationStayAmendmentOperation.CreateOutcomeUnknown(
                OperationId,
                TenantId,
                PropertyId,
                ReservationId,
                ReservationStayAmendmentOperation.CurrentRequestSchemaVersion,
                Fingerprint,
                expectedDetailsRevision: 1,
                Now);

        Assert.Equal(
            ReservationsDomainErrors.StayAmendmentOperationRequestInvalid,
            created.Error);
    }

    [Fact]
    public void Applied_no_op_rejects_the_legacy_request_schema()
    {
        Result<ReservationStayAmendmentOperation> created =
            ReservationStayAmendmentOperation.CreateAppliedNoOp(
                OperationId,
                TenantId,
                PropertyId,
                ReservationId,
                ReservationStayAmendmentOperation.LegacyRequestSchemaVersion,
                Fingerprint,
                Arrival,
                Departure,
                new TimeOnly(15, 0),
                new TimeOnly(10, 0),
                [Guid.NewGuid()],
                expectedDetailsRevision: 3,
                Actor,
                resultingDetailsRevision: 3,
                resultingReservationVersion: 8,
                resultingAllocationVersion: 5,
                Now);

        Assert.Equal(
            ReservationsDomainErrors.StayAmendmentOperationRequestInvalid,
            created.Error);
    }

    private static ReservationStayAmendmentOperation CreatePending(
        IReadOnlyCollection<Guid> unitIds) =>
        ReservationStayAmendmentOperation.CreatePending(
            OperationId,
            TenantId,
            PropertyId,
            ReservationId,
            Guid.NewGuid(),
            ReservationStayAmendmentOperation.CurrentRequestSchemaVersion,
            Fingerprint,
            Arrival,
            Departure,
            new TimeOnly(15, 0),
            new TimeOnly(10, 0),
            unitIds,
            expectedDetailsRevision: 1,
            actorId: Actor,
            requestedAtUtc: Now).Value;

    private const string TenantId = "tenant-a";
    private const string Actor = "user:operator-a";
    private const string Fingerprint =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly Guid OperationId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid PropertyId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid ReservationId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly DateOnly Arrival = new(2026, 8, 20);
    private static readonly DateOnly Departure = new(2026, 8, 23);
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
}
