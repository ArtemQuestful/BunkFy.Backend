namespace BunkFy.Modules.Reservations.Domain.StayAmendments;

using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.Errors;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed partial class ReservationStayAmendmentOperation : ScopedAggregateRoot<Guid>
{
    public const int LegacyRequestSchemaVersion = 1;
    public const int CurrentRequestSchemaVersion = 2;
    public const int RequestFingerprintLength = 64;
    public const int TargetInventoryUnitIdsMaxLength = Reservation.MaximumRequestedUnits * 33;
    public static readonly TimeSpan MinimumReconciliationInterval = TimeSpan.FromMinutes(5);

    private ReservationStayAmendmentOperation() { }

    private ReservationStayAmendmentOperation(Guid id, string scopeId)
        : base(id, scopeId)
    {
    }

    public Guid PropertyId { get; private set; }
    public Guid ReservationId { get; private set; }
    public Guid? InventoryRequestId { get; private set; }
    public int RequestSchemaVersion { get; private set; }
    public string RequestFingerprint { get; private set; } = string.Empty;
    public DateOnly? TargetArrival { get; private set; }
    public DateOnly? TargetDeparture { get; private set; }
    public TimeOnly? TargetExpectedArrivalTime { get; private set; }
    public TimeOnly? TargetExpectedDepartureTime { get; private set; }
    public string? TargetInventoryUnitIds { get; private set; }
    public long ExpectedDetailsRevision { get; private set; }
    public string? RequestedBy { get; private set; }
    public ReservationStayAmendmentOperationOutcome Outcome { get; private set; }
    public long OperationVersion { get; private set; }
    public DateTimeOffset RequestedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public long? ResultingDetailsRevision { get; private set; }
    public long? ResultingReservationVersion { get; private set; }
    public long? ResultingAllocationVersion { get; private set; }
    public int? RejectionCode { get; private set; }
    public int ReconciliationCount { get; private set; }
    public DateTimeOffset? LastReconciledAtUtc { get; private set; }
    public string? LastReconciledBy { get; private set; }

    public static Result<ReservationStayAmendmentOperation> CreatePending(
        Guid operationId,
        string tenantId,
        Guid propertyId,
        Guid reservationId,
        Guid inventoryRequestId,
        int requestSchemaVersion,
        string requestFingerprint,
        DateOnly targetArrival,
        DateOnly targetDeparture,
        TimeOnly? targetExpectedArrivalTime,
        TimeOnly? targetExpectedDepartureTime,
        IReadOnlyCollection<Guid> targetInventoryUnitIds,
        long expectedDetailsRevision,
        string actorId,
        DateTimeOffset requestedAtUtc) => Create(
            operationId,
            tenantId,
            propertyId,
            reservationId,
            inventoryRequestId,
            requestSchemaVersion,
            requestFingerprint,
            targetArrival,
            targetDeparture,
            targetExpectedArrivalTime,
            targetExpectedDepartureTime,
            targetInventoryUnitIds,
            expectedDetailsRevision,
            actorId,
            ReservationStayAmendmentOperationOutcome.Pending,
            resultingDetailsRevision: null,
            resultingReservationVersion: null,
            resultingAllocationVersion: null,
            requestedAtUtc);

    public static Result<ReservationStayAmendmentOperation> CreateAppliedNoOp(
        Guid operationId,
        string tenantId,
        Guid propertyId,
        Guid reservationId,
        int requestSchemaVersion,
        string requestFingerprint,
        DateOnly targetArrival,
        DateOnly targetDeparture,
        TimeOnly? targetExpectedArrivalTime,
        TimeOnly? targetExpectedDepartureTime,
        IReadOnlyCollection<Guid> targetInventoryUnitIds,
        long expectedDetailsRevision,
        string actorId,
        long resultingDetailsRevision,
        long resultingReservationVersion,
        long resultingAllocationVersion,
        DateTimeOffset requestedAtUtc) => Create(
            operationId,
            tenantId,
            propertyId,
            reservationId,
            inventoryRequestId: null,
            requestSchemaVersion,
            requestFingerprint,
            targetArrival,
            targetDeparture,
            targetExpectedArrivalTime,
            targetExpectedDepartureTime,
            targetInventoryUnitIds,
            expectedDetailsRevision,
            actorId,
            ReservationStayAmendmentOperationOutcome.Applied,
            resultingDetailsRevision,
            resultingReservationVersion,
            resultingAllocationVersion,
            requestedAtUtc);

    public static Result<ReservationStayAmendmentOperation> CreateOutcomeUnknown(
        Guid operationId,
        string tenantId,
        Guid propertyId,
        Guid reservationId,
        int requestSchemaVersion,
        string requestFingerprint,
        long expectedDetailsRevision,
        DateTimeOffset requestedAtUtc) => Create(
            operationId,
            tenantId,
            propertyId,
            reservationId,
            inventoryRequestId: null,
            requestSchemaVersion,
            requestFingerprint,
            targetArrival: null,
            targetDeparture: null,
            targetExpectedArrivalTime: null,
            targetExpectedDepartureTime: null,
            targetInventoryUnitIds: null,
            expectedDetailsRevision,
            actorId: null,
            ReservationStayAmendmentOperationOutcome.OutcomeUnknown,
            resultingDetailsRevision: null,
            resultingReservationVersion: null,
            resultingAllocationVersion: null,
            requestedAtUtc);

    public bool MatchesRequest(int requestSchemaVersion, string requestFingerprint) =>
        this.RequestSchemaVersion == requestSchemaVersion &&
        string.Equals(
            this.RequestFingerprint,
            NormalizeFingerprint(requestFingerprint),
            StringComparison.Ordinal);

    public IReadOnlyCollection<Guid> GetTargetInventoryUnitIds() =>
        (this.TargetInventoryUnitIds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => Guid.ParseExact(value, "N"))
            .ToArray();

    public DateTimeOffset? NextReconciliationEligibleAtUtc =>
        this.Outcome == ReservationStayAmendmentOperationOutcome.Pending
            ? this.UpdatedAtUtc + MinimumReconciliationInterval
            : null;

    public bool IsReconciliationEligible(DateTimeOffset nowUtc) =>
        this.NextReconciliationEligibleAtUtc is DateTimeOffset eligibleAtUtc &&
        nowUtc >= eligibleAtUtc;

    public Result<bool> MarkApplied(
        DateOnly arrival,
        DateOnly departure,
        TimeOnly? expectedArrivalTime,
        TimeOnly? expectedDepartureTime,
        IReadOnlyCollection<Guid> inventoryUnitIds,
        long resultingAllocationVersion,
        long resultingDetailsRevision,
        long resultingReservationVersion,
        DateTimeOffset nowUtc)
    {
        if (!this.MatchesTarget(
                arrival,
                departure,
                expectedArrivalTime,
                expectedDepartureTime,
                inventoryUnitIds) ||
            resultingAllocationVersion <= 0 || resultingDetailsRevision <= 0 ||
            resultingReservationVersion <= 0)
        {
            return Result.Failure<bool>(ReservationsDomainErrors.StayAmendmentOutcomeMismatch);
        }

        if (this.Outcome == ReservationStayAmendmentOperationOutcome.Applied)
        {
            return this.ResultingAllocationVersion == resultingAllocationVersion &&
                this.ResultingDetailsRevision == resultingDetailsRevision &&
                this.ResultingReservationVersion == resultingReservationVersion
                    ? Result.Success(false)
                    : Result.Failure<bool>(ReservationsDomainErrors.StayAmendmentOutcomeMismatch);
        }

        if (this.Outcome != ReservationStayAmendmentOperationOutcome.Pending || !this.IsValidAdvance(nowUtc))
        {
            return Result.Failure<bool>(ReservationsDomainErrors.StayAmendmentOperationTransitionInvalid);
        }

        this.Outcome = ReservationStayAmendmentOperationOutcome.Applied;
        this.ResultingAllocationVersion = resultingAllocationVersion;
        this.ResultingDetailsRevision = resultingDetailsRevision;
        this.ResultingReservationVersion = resultingReservationVersion;
        this.CompletedAtUtc = nowUtc;
        this.Advance(nowUtc);
        return Result.Success(true);
    }

    public Result<bool> MarkRejected(
        int rejectionCode,
        long resultingDetailsRevision,
        long resultingReservationVersion,
        DateTimeOffset nowUtc)
    {
        if (rejectionCode <= 0 || resultingDetailsRevision <= 0 || resultingReservationVersion <= 0)
        {
            return Result.Failure<bool>(ReservationsDomainErrors.StayAmendmentOutcomeMismatch);
        }

        if (this.Outcome == ReservationStayAmendmentOperationOutcome.Rejected)
        {
            return this.RejectionCode == rejectionCode &&
                this.ResultingDetailsRevision == resultingDetailsRevision &&
                this.ResultingReservationVersion == resultingReservationVersion
                    ? Result.Success(false)
                    : Result.Failure<bool>(ReservationsDomainErrors.StayAmendmentOutcomeMismatch);
        }

        if (this.Outcome != ReservationStayAmendmentOperationOutcome.Pending || !this.IsValidAdvance(nowUtc))
        {
            return Result.Failure<bool>(ReservationsDomainErrors.StayAmendmentOperationTransitionInvalid);
        }

        this.Outcome = ReservationStayAmendmentOperationOutcome.Rejected;
        this.RejectionCode = rejectionCode;
        this.ResultingDetailsRevision = resultingDetailsRevision;
        this.ResultingReservationVersion = resultingReservationVersion;
        this.CompletedAtUtc = nowUtc;
        this.Advance(nowUtc);
        return Result.Success(true);
    }

    private static Result<ReservationStayAmendmentOperation> Create(
        Guid operationId,
        string tenantId,
        Guid propertyId,
        Guid reservationId,
        Guid? inventoryRequestId,
        int requestSchemaVersion,
        string requestFingerprint,
        DateOnly? targetArrival,
        DateOnly? targetDeparture,
        TimeOnly? targetExpectedArrivalTime,
        TimeOnly? targetExpectedDepartureTime,
        IReadOnlyCollection<Guid>? targetInventoryUnitIds,
        long expectedDetailsRevision,
        string? actorId,
        ReservationStayAmendmentOperationOutcome outcome,
        long? resultingDetailsRevision,
        long? resultingReservationVersion,
        long? resultingAllocationVersion,
        DateTimeOffset requestedAtUtc)
    {
        if (operationId == Guid.Empty || propertyId == Guid.Empty || reservationId == Guid.Empty ||
            !TenantIds.TryNormalize(tenantId, out string? scopeId))
        {
            return Result.Failure<ReservationStayAmendmentOperation>(
                ReservationsDomainErrors.StayAmendmentOperationIdentityInvalid);
        }

        string fingerprint = NormalizeFingerprint(requestFingerprint);
        string normalizedActor = actorId?.Trim() ?? string.Empty;
        Guid[] units = targetInventoryUnitIds?.ToArray() ?? [];
        bool targetUnknown = outcome == ReservationStayAmendmentOperationOutcome.OutcomeUnknown;
        if (requestSchemaVersion is not (LegacyRequestSchemaVersion or CurrentRequestSchemaVersion) ||
            (targetUnknown && requestSchemaVersion != LegacyRequestSchemaVersion) ||
            (outcome == ReservationStayAmendmentOperationOutcome.Applied &&
                requestSchemaVersion != CurrentRequestSchemaVersion) ||
            outcome == ReservationStayAmendmentOperationOutcome.Pending !=
                (inventoryRequestId.HasValue && inventoryRequestId != Guid.Empty) ||
            fingerprint.Length != RequestFingerprintLength || !fingerprint.All(Uri.IsHexDigit) ||
            (!targetUnknown && (!targetArrival.HasValue || !targetDeparture.HasValue ||
                targetArrival.Value >= targetDeparture.Value ||
                !HasMinutePrecision(targetExpectedArrivalTime) || !HasMinutePrecision(targetExpectedDepartureTime) ||
                units.Length is 0 or > Reservation.MaximumRequestedUnits || units.Any(id => id == Guid.Empty) ||
                units.Distinct().Count() != units.Length)) ||
            (targetUnknown && (targetArrival.HasValue || targetDeparture.HasValue ||
                targetExpectedArrivalTime.HasValue || targetExpectedDepartureTime.HasValue || units.Length != 0)) ||
            expectedDetailsRevision <= 0 ||
            (!targetUnknown && (normalizedActor.Length is 0 or > Reservation.ActorIdMaxLength ||
                normalizedActor.Any(char.IsControl))) ||
            (targetUnknown && normalizedActor.Length != 0) || requestedAtUtc == default ||
            outcome is ReservationStayAmendmentOperationOutcome.Unknown or ReservationStayAmendmentOperationOutcome.Rejected ||
            outcome == ReservationStayAmendmentOperationOutcome.Applied != resultingDetailsRevision.HasValue ||
            outcome == ReservationStayAmendmentOperationOutcome.Applied != resultingReservationVersion.HasValue ||
            outcome == ReservationStayAmendmentOperationOutcome.Applied != resultingAllocationVersion.HasValue ||
            resultingDetailsRevision <= 0 || resultingReservationVersion <= 0 ||
            resultingAllocationVersion <= 0)
        {
            return Result.Failure<ReservationStayAmendmentOperation>(
                ReservationsDomainErrors.StayAmendmentOperationRequestInvalid);
        }

        return Result.Success(new ReservationStayAmendmentOperation(operationId, scopeId)
        {
            PropertyId = propertyId,
            ReservationId = reservationId,
            InventoryRequestId = inventoryRequestId,
            RequestSchemaVersion = requestSchemaVersion,
            RequestFingerprint = fingerprint,
            TargetArrival = targetArrival,
            TargetDeparture = targetDeparture,
            TargetExpectedArrivalTime = targetExpectedArrivalTime,
            TargetExpectedDepartureTime = targetExpectedDepartureTime,
            TargetInventoryUnitIds = targetUnknown
                ? null
                : string.Join(',', units.Order().Select(id => id.ToString("N"))),
            ExpectedDetailsRevision = expectedDetailsRevision,
            RequestedBy = targetUnknown ? null : normalizedActor,
            Outcome = outcome,
            OperationVersion = 1,
            RequestedAtUtc = requestedAtUtc,
            UpdatedAtUtc = requestedAtUtc,
            CompletedAtUtc = outcome == ReservationStayAmendmentOperationOutcome.Applied ? requestedAtUtc : null,
            ResultingDetailsRevision = resultingDetailsRevision,
            ResultingReservationVersion = resultingReservationVersion,
            ResultingAllocationVersion = resultingAllocationVersion
        });
    }

    private bool MatchesTarget(
        DateOnly arrival,
        DateOnly departure,
        TimeOnly? expectedArrivalTime,
        TimeOnly? expectedDepartureTime,
        IReadOnlyCollection<Guid> inventoryUnitIds) =>
        this.TargetArrival == arrival &&
        this.TargetDeparture == departure &&
        this.TargetExpectedArrivalTime == expectedArrivalTime &&
        this.TargetExpectedDepartureTime == expectedDepartureTime &&
        this.GetTargetInventoryUnitIds().Order().SequenceEqual(inventoryUnitIds.Order());

    private bool IsValidAdvance(DateTimeOffset nowUtc) =>
        nowUtc != default && nowUtc >= this.UpdatedAtUtc;

    private void Advance(DateTimeOffset nowUtc)
    {
        this.OperationVersion++;
        this.UpdatedAtUtc = nowUtc;
    }

    private static bool HasMinutePrecision(TimeOnly? time) =>
        !time.HasValue || time.Value.Ticks % TimeSpan.TicksPerMinute == 0;

    private static string NormalizeFingerprint(string? fingerprint) =>
        fingerprint?.Trim().ToLowerInvariant() ?? string.Empty;
}
