namespace BunkFy.Modules.DataRights.Domain.Aggregates;

using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Errors;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed partial class DataRightsCase : ScopedAggregateRoot<Guid>
{
    public const int ActorIdMaxLength = 200;
    public const int MaxSelectedSubjects = 100;

    private readonly List<DataRightsSubjectCoordinate> selectedSubjects = [];

    private DataRightsCase() { }

    private DataRightsCase(Guid id, string scopeId) : base(id, scopeId) { }

    public Guid? PropertyId { get; private set; }
    public DataRightsCaseKind Kind { get; private set; }
    public DataRightsCaseOperation RequestedOperations { get; private set; }
    public DataRightsRestrictionAction RestrictionAction { get; private set; }
    public DataRightsRequesterRelation RequesterRelationship { get; private set; }
    public DataRightsVerificationState VerificationStatus { get; private set; }
    public DataRightsRoutingState RoutingStatus { get; private set; }
    public DataRightsCaseState Status { get; private set; } = DataRightsCaseState.Draft;
    public DataRightsCaseDecision Decision { get; private set; }
    public DataRightsCaseDecisionReason DecisionReason { get; private set; }
    public long? DecisionRevision { get; private set; }
    public string? DecidedBy { get; private set; }
    public DateTimeOffset? DecidedAtUtc { get; private set; }
    public DataRightsApprovalPolicyEvidence? ApprovalPolicyEvidence { get; private set; }
    public bool? TenantTerminationExportRequested { get; private set; }
    public string? TenantTerminationPolicyEvidenceSha256 { get; private set; }
    public DataRightsRestrictionExecutionProof? RestrictionExecutionProof { get; private set; }
    public int? RestrictionTargetingContractVersion { get; private set; }
    public DataRightsRestrictionReleaseTarget? RestrictionReleaseTarget { get; private set; }
    public long? ExecutionRevision { get; private set; }
    public string? ExecutionStartedBy { get; private set; }
    public DateTimeOffset? ExecutionStartedAtUtc { get; private set; }
    public DateTimeOffset? DueAtUtc { get; private set; }
    public DataRightsResponseDeadlinePolicyEvidence? ResponseDeadlinePolicyEvidence { get; private set; }
    public long Version { get; private set; } = 1;
    public string CreatedBy { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string LastChangedBy { get; private set; } = string.Empty;
    public DateTimeOffset LastChangedAtUtc { get; private set; }
    public IReadOnlyCollection<DataRightsSubjectCoordinate> SelectedSubjects =>
        this.selectedSubjects.AsReadOnly();

    public static Result<DataRightsCase> Create(
        Guid id,
        string tenantId,
        DataRightsCaseRequest request,
        string actorId,
        DateTimeOffset nowUtc,
        DataRightsResponseDeadlinePolicyEvidence? responseDeadlinePolicyEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (id == Guid.Empty)
        {
            return Result.Failure<DataRightsCase>(DataRightsDomainErrors.CaseIdRequired);
        }

        if (!TenantIds.TryNormalize(tenantId, out string? scopeId))
        {
            return Result.Failure<DataRightsCase>(DataRightsDomainErrors.TenantInvalid);
        }

        Result<string> actor = NormalizeActor(actorId);
        if (actor.IsFailure)
        {
            return Result.Failure<DataRightsCase>(actor.Error);
        }

        if (nowUtc == default)
        {
            return Result.Failure<DataRightsCase>(DataRightsDomainErrors.TimestampInvalid);
        }

        Result deadline = ValidateResponseDeadlinePolicyEvidence(
            request,
            nowUtc,
            responseDeadlinePolicyEvidence);
        if (deadline.IsFailure)
        {
            return Result.Failure<DataRightsCase>(deadline.Error);
        }

        bool requesterNeedsVerification = request.RequesterRelationship is
            DataRightsRequesterRelation.DataSubject or
            DataRightsRequesterRelation.AuthorizedRepresentative;
        DataRightsCase dataRightsCase = new(id, scopeId)
        {
            PropertyId = request.PropertyId,
            Kind = request.Kind,
            RequestedOperations = request.RequestedOperations,
            RestrictionAction = request.RestrictionAction,
            RestrictionTargetingContractVersion =
                request.RequestedOperations == DataRightsCaseOperation.Restriction &&
                request.RestrictionAction == DataRightsRestrictionAction.Release
                    ? DataRightsRestrictionReleaseTarget.CurrentBindingVersion
                    : null,
            RequesterRelationship = request.RequesterRelationship,
            VerificationStatus = requesterNeedsVerification
                ? DataRightsVerificationState.Pending
                : DataRightsVerificationState.NotRequired,
            RoutingStatus = requesterNeedsVerification
                ? DataRightsRoutingState.Pending
                : DataRightsRoutingState.NotRequired,
            DueAtUtc = responseDeadlinePolicyEvidence?.DueAtUtc,
            ResponseDeadlinePolicyEvidence = responseDeadlinePolicyEvidence,
            CreatedBy = actor.Value,
            CreatedAtUtc = nowUtc,
            LastChangedBy = actor.Value,
            LastChangedAtUtc = nowUtc
        };
        return Result.Success(dataRightsCase);
    }

    public Result RecordRequesterVerification(
        bool verified,
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        Result ready = this.EnsureTransition(
            expectedVersion,
            actorId,
            nowUtc,
            DataRightsCaseState.Draft);
        if (ready.IsFailure)
        {
            return ready;
        }

        if (this.VerificationStatus != DataRightsVerificationState.Pending)
        {
            return Result.Failure(DataRightsDomainErrors.TransitionInvalid);
        }

        this.VerificationStatus = verified
            ? DataRightsVerificationState.Verified
            : DataRightsVerificationState.Failed;
        this.CompleteChange(actorId, nowUtc);
        return Result.Success();
    }

    public Result RecordControllerRouting(
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc,
        DataRightsResponseDeadlinePolicyEvidence? responseDeadlinePolicyEvidence = null)
    {
        Result ready = this.EnsureTransition(
            expectedVersion,
            actorId,
            nowUtc,
            DataRightsCaseState.Draft);
        if (ready.IsFailure)
        {
            return ready;
        }

        bool needsDeadline = this.RequiresResponseDeadline();
        bool legacyRoutedWithoutDeadline =
            needsDeadline &&
            this.RoutingStatus == DataRightsRoutingState.Routed &&
            this.ResponseDeadlinePolicyEvidence is null;
        if (this.RoutingStatus != DataRightsRoutingState.Pending &&
            !legacyRoutedWithoutDeadline)
        {
            return Result.Failure(DataRightsDomainErrors.TransitionInvalid);
        }

        if (needsDeadline)
        {
            Result assigned = this.AssignResponseDeadlinePolicyEvidence(
                responseDeadlinePolicyEvidence);
            if (assigned.IsFailure)
            {
                return assigned;
            }
        }
        else if (responseDeadlinePolicyEvidence is not null)
        {
            return Result.Failure(
                DataRightsDomainErrors.ResponseDeadlinePolicyEvidenceInvalid);
        }

        this.RoutingStatus = DataRightsRoutingState.Routed;
        this.CompleteChange(actorId, nowUtc);
        return Result.Success();
    }

    public Result BeginDiscovery(long expectedVersion, string actorId, DateTimeOffset nowUtc)
    {
        Result ready = this.EnsureTransition(
            expectedVersion,
            actorId,
            nowUtc,
            DataRightsCaseState.Draft);
        if (ready.IsFailure)
        {
            return ready;
        }

        if (this.VerificationStatus is not DataRightsVerificationState.Verified
            and not DataRightsVerificationState.NotRequired)
        {
            return Result.Failure(DataRightsDomainErrors.VerificationRequired);
        }

        if (this.RoutingStatus is not DataRightsRoutingState.Routed
            and not DataRightsRoutingState.NotRequired)
        {
            return Result.Failure(DataRightsDomainErrors.ControllerRoutingRequired);
        }

        if (this.RequiresResponseDeadline() &&
            (this.ResponseDeadlinePolicyEvidence is null || this.DueAtUtc is null))
        {
            return Result.Failure(DataRightsDomainErrors.ResponseDeadlinePolicyRequired);
        }

        this.Status = DataRightsCaseState.Discovery;
        this.CompleteChange(actorId, nowUtc);
        return Result.Success();
    }

    private static Result ValidateResponseDeadlinePolicyEvidence(
        DataRightsCaseRequest request,
        DateTimeOffset createdAtUtc,
        DataRightsResponseDeadlinePolicyEvidence? evidence)
    {
        if (evidence is null)
        {
            return Result.Success();
        }

        bool requiresDeadline = request.Kind == DataRightsCaseKind.GuestRights &&
            request.RequesterRelationship is
                DataRightsRequesterRelation.DataSubject or
                DataRightsRequesterRelation.AuthorizedRepresentative;
        return requiresDeadline &&
            evidence.HasValidShape() &&
            request.PropertyId == evidence.PropertyId &&
            evidence.ReceivedAtUtc == createdAtUtc
                ? Result.Success()
                : Result.Failure(
                    DataRightsDomainErrors.ResponseDeadlinePolicyEvidenceInvalid);
    }

    private Result AssignResponseDeadlinePolicyEvidence(
        DataRightsResponseDeadlinePolicyEvidence? evidence)
    {
        if (this.ResponseDeadlinePolicyEvidence is not null)
        {
            if (!this.ResponseDeadlinePolicyEvidence.HasValidShape() ||
                this.DueAtUtc != this.ResponseDeadlinePolicyEvidence.DueAtUtc)
            {
                return Result.Failure(
                    DataRightsDomainErrors.ResponseDeadlinePolicyEvidenceInvalid);
            }

            return evidence is null ||
                this.ResponseDeadlinePolicyEvidence.HasSameCoordinates(evidence)
                    ? Result.Success()
                    : Result.Failure(
                        DataRightsDomainErrors.ResponseDeadlinePolicyEvidenceInvalid);
        }

        if (evidence is null)
        {
            return Result.Failure(DataRightsDomainErrors.ResponseDeadlinePolicyRequired);
        }

        if (!evidence.HasValidShape() ||
            this.PropertyId != evidence.PropertyId ||
            this.CreatedAtUtc != evidence.ReceivedAtUtc)
        {
            return Result.Failure(
                DataRightsDomainErrors.ResponseDeadlinePolicyEvidenceInvalid);
        }

        this.ResponseDeadlinePolicyEvidence = evidence;
        this.DueAtUtc = evidence.DueAtUtc;
        return this.DueAtUtc == evidence.DueAtUtc
            ? Result.Success()
            : Result.Failure(
                DataRightsDomainErrors.ResponseDeadlinePolicyEvidenceInvalid);
    }

    private bool RequiresResponseDeadline() =>
        this.Kind == DataRightsCaseKind.GuestRights &&
        this.RequesterRelationship is
            DataRightsRequesterRelation.DataSubject or
            DataRightsRequesterRelation.AuthorizedRepresentative;

    public Result Cancel(long expectedVersion, string actorId, DateTimeOffset nowUtc)
    {
        Result ready = this.EnsureVersionActorAndTime(expectedVersion, actorId, nowUtc);
        if (ready.IsFailure)
        {
            return ready;
        }

        if (this.Status is not DataRightsCaseState.Draft
            and not DataRightsCaseState.Discovery
            and not DataRightsCaseState.ReviewRequired
            and not DataRightsCaseState.DecisionPending
            and not DataRightsCaseState.Blocked)
        {
            return Result.Failure(DataRightsDomainErrors.TransitionInvalid);
        }

        this.Status = DataRightsCaseState.Canceled;
        this.CompleteChange(actorId, nowUtc);
        return Result.Success();
    }

    private Result EnsureTransition(
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc,
        DataRightsCaseState expectedState)
    {
        Result ready = this.EnsureVersionActorAndTime(expectedVersion, actorId, nowUtc);
        if (ready.IsFailure)
        {
            return ready;
        }

        return this.Status == expectedState
            ? Result.Success()
            : Result.Failure(DataRightsDomainErrors.TransitionInvalid);
    }

    private Result EnsureVersionActorAndTime(
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        if (expectedVersion != this.Version)
        {
            return Result.Failure(DataRightsDomainErrors.VersionConflict);
        }

        Result<string> actor = NormalizeActor(actorId);
        if (actor.IsFailure)
        {
            return Result.Failure(actor.Error);
        }

        return nowUtc == default || nowUtc < this.LastChangedAtUtc
            ? Result.Failure(DataRightsDomainErrors.TimestampInvalid)
            : Result.Success();
    }

    private void CompleteChange(string actorId, DateTimeOffset nowUtc)
    {
        this.LastChangedBy = actorId.Trim();
        this.LastChangedAtUtc = nowUtc;
        this.Version++;
    }

    private static Result<string> NormalizeActor(string actorId)
    {
        string normalized = actorId?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= ActorIdMaxLength
            ? Result.Success(normalized)
            : Result.Failure<string>(DataRightsDomainErrors.ActorInvalid);
    }

}
