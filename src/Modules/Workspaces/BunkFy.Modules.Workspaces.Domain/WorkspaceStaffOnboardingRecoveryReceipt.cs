namespace BunkFy.Modules.Workspaces.Domain;

using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class WorkspaceStaffOnboardingRecoveryReceipt
    : ScopedAggregateRoot<Guid>
{
    public const int CurrentContractVersion = 1;
    public const int DigestLength = 64;
    public const int ActorIdMaxLength = 256;
    private const int ActiveOrganizationMembershipStatus = 1;

    private WorkspaceStaffOnboardingRecoveryReceipt()
    {
    }

    private WorkspaceStaffOnboardingRecoveryReceipt(
        Guid id,
        string scopeId)
        : base(id, scopeId)
    {
    }

    public int ContractVersion { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid ApplicationId { get; private set; }
    public Guid SourceId { get; private set; }
    public Guid StaffMemberId { get; private set; }
    public WorkspaceStaffOnboardingRecoveryTriggerKind TriggerKind { get; private set; }
    public Guid TriggerId { get; private set; }
    public string ApplicantSubjectId { get; private set; } = string.Empty;
    public string PlanCreatorActorId { get; private set; } = string.Empty;
    public string RecoveryActorId { get; private set; } = string.Empty;
    public string EffectiveSystemActorId { get; private set; } = string.Empty;
    public string RequestSha256 { get; private set; } = string.Empty;
    public long ExpectedApplicationVersion { get; private set; }
    public long SelectedApplicationVersion { get; private set; }
    public WorkspaceStaffOnboardingState SelectedApplicationStatus { get; private set; }
    public long ResultingApplicationVersion { get; private set; }
    public WorkspaceStaffOnboardingState ResultingApplicationStatus { get; private set; }
    public Guid CurrentProfileId { get; private set; }
    public long CurrentProfileVersion { get; private set; }
    public int PropertyCount { get; private set; }
    public string TargetSetSha256 { get; private set; } = string.Empty;
    public long? OrganizationRevision { get; private set; }
    public Guid? MembershipId { get; private set; }
    public long? MembershipVersion { get; private set; }
    public int? MembershipStatus { get; private set; }
    public long? StaffTenantRevision { get; private set; }
    public long? StaffMemberVersion { get; private set; }
    public string? StaffStateProofSha256 { get; private set; }
    public string? StaffPropertySetSha256 { get; private set; }
    public long? AccessManagementRevision { get; private set; }
    public string? AccessFactsSha256 { get; private set; }
    public WorkspaceRecoveryVerification OrganizationVerification { get; private set; }
    public WorkspaceRecoveryVerification StaffVerification { get; private set; }
    public WorkspaceRecoveryVerification AccessVerification { get; private set; }
    public WorkspaceStaffOnboardingRecoveryOutcome Outcome { get; private set; }
    public WorkspaceStaffOnboardingRecoveryReason Reason { get; private set; }
    public bool AccessMutationPerformed { get; private set; }
    public DateTimeOffset? OrganizationObservedAtUtc { get; private set; }
    public DateTimeOffset? StaffObservedAtUtc { get; private set; }
    public DateTimeOffset? AccessObservedAtUtc { get; private set; }
    public DateTimeOffset RecordedAtUtc { get; private set; }

    public static Result<WorkspaceStaffOnboardingRecoveryReceipt> Create(
        WorkspaceStaffOnboardingRecoveryReceiptValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.ReceiptId == Guid.Empty ||
            values.OperationId == Guid.Empty ||
            values.ApplicationId == Guid.Empty ||
            values.SourceId == Guid.Empty ||
            values.StaffMemberId == Guid.Empty ||
            values.TriggerId == Guid.Empty ||
            values.CurrentProfileId == Guid.Empty ||
            values.ExpectedApplicationVersion < 1 ||
            values.SelectedApplicationVersion < 1 ||
            values.CurrentProfileVersion < 1 ||
            values.PropertyCount is < 0 or > WorkspaceStaffAccessPlan.PropertyCountMax ||
            values.RecordedAtUtc == default ||
            !TenantIds.TryNormalize(values.TenantId, out string? scopeId) ||
            !ValidActor(values.ApplicantSubjectId) ||
            !ValidActor(values.PlanCreatorActorId) ||
            !ValidActor(values.RecoveryActorId) ||
            !ValidActor(values.EffectiveSystemActorId) ||
            !ValidDigest(values.RequestSha256) ||
            !ValidDigest(values.TargetSetSha256) ||
            !ValidOptionalDigest(values.StaffStateProofSha256) ||
            !ValidOptionalDigest(values.StaffPropertySetSha256) ||
            !ValidOptionalDigest(values.AccessFactsSha256) ||
            values.TriggerKind !=
                WorkspaceStaffOnboardingRecoveryTriggerKind.ManualOperator ||
            !Enum.IsDefined(values.SelectedApplicationStatus) ||
            !Enum.IsDefined(values.ResultingApplicationStatus) ||
            !Enum.IsDefined(values.OrganizationVerification) ||
            !Enum.IsDefined(values.StaffVerification) ||
            !Enum.IsDefined(values.AccessVerification) ||
            values.Outcome == WorkspaceStaffOnboardingRecoveryOutcome.Unknown ||
            !Enum.IsDefined(values.Outcome) ||
            values.Reason == WorkspaceStaffOnboardingRecoveryReason.Unknown ||
            !Enum.IsDefined(values.Reason) ||
            values.AccessMutationPerformed)
        {
            return Result.Failure<WorkspaceStaffOnboardingRecoveryReceipt>(
                WorkspaceStaffOnboardingErrors.Invalid);
        }

        if (!HasTruthfulState(values))
        {
            return Result.Failure<WorkspaceStaffOnboardingRecoveryReceipt>(
                WorkspaceStaffOnboardingErrors.StateConflict);
        }

        return Result.Success(new WorkspaceStaffOnboardingRecoveryReceipt(
            values.ReceiptId,
            scopeId)
        {
            ContractVersion = CurrentContractVersion,
            OperationId = values.OperationId,
            ApplicationId = values.ApplicationId,
            SourceId = values.SourceId,
            StaffMemberId = values.StaffMemberId,
            TriggerKind = values.TriggerKind,
            TriggerId = values.TriggerId,
            ApplicantSubjectId = values.ApplicantSubjectId.Trim(),
            PlanCreatorActorId = values.PlanCreatorActorId.Trim(),
            RecoveryActorId = values.RecoveryActorId.Trim(),
            EffectiveSystemActorId = values.EffectiveSystemActorId.Trim(),
            RequestSha256 = NormalizeDigest(values.RequestSha256),
            ExpectedApplicationVersion = values.ExpectedApplicationVersion,
            SelectedApplicationVersion = values.SelectedApplicationVersion,
            SelectedApplicationStatus = values.SelectedApplicationStatus,
            ResultingApplicationVersion = values.ResultingApplicationVersion,
            ResultingApplicationStatus = values.ResultingApplicationStatus,
            CurrentProfileId = values.CurrentProfileId,
            CurrentProfileVersion = values.CurrentProfileVersion,
            PropertyCount = values.PropertyCount,
            TargetSetSha256 = NormalizeDigest(values.TargetSetSha256),
            OrganizationRevision = values.OrganizationRevision,
            MembershipId = values.MembershipId,
            MembershipVersion = values.MembershipVersion,
            MembershipStatus = values.MembershipStatus,
            StaffTenantRevision = values.StaffTenantRevision,
            StaffMemberVersion = values.StaffMemberVersion,
            StaffStateProofSha256 = NormalizeOptionalDigest(
                values.StaffStateProofSha256),
            StaffPropertySetSha256 = NormalizeOptionalDigest(
                values.StaffPropertySetSha256),
            AccessManagementRevision = values.AccessManagementRevision,
            AccessFactsSha256 = NormalizeOptionalDigest(values.AccessFactsSha256),
            OrganizationVerification = values.OrganizationVerification,
            StaffVerification = values.StaffVerification,
            AccessVerification = values.AccessVerification,
            Outcome = values.Outcome,
            Reason = values.Reason,
            AccessMutationPerformed = false,
            OrganizationObservedAtUtc = values.OrganizationObservedAtUtc,
            StaffObservedAtUtc = values.StaffObservedAtUtc,
            AccessObservedAtUtc = values.AccessObservedAtUtc,
            RecordedAtUtc = values.RecordedAtUtc
        });
    }

    public bool MatchesReplay(string requestSha256) =>
        string.Equals(
            this.RequestSha256,
            NormalizeDigest(requestSha256),
            StringComparison.Ordinal);

    private static bool ValidActor(string? value) =>
        value?.Trim().Length is > 0 and <= ActorIdMaxLength;

    private static bool HasTruthfulState(
        WorkspaceStaffOnboardingRecoveryReceiptValues values)
    {
        if (values.ExpectedApplicationVersion !=
                values.SelectedApplicationVersion ||
            values.SelectedApplicationStatus !=
                WorkspaceStaffOnboardingState.Failed ||
            !StagePresenceIsValid(values) ||
            ObservationAfterDecision(
                values.OrganizationObservedAtUtc,
                values.RecordedAtUtc) ||
            ObservationAfterDecision(
                values.StaffObservedAtUtc,
                values.RecordedAtUtc) ||
            ObservationAfterDecision(
                values.AccessObservedAtUtc,
                values.RecordedAtUtc))
        {
            return false;
        }

        bool unchanged =
            values.ResultingApplicationStatus ==
                WorkspaceStaffOnboardingState.Failed &&
            values.ResultingApplicationVersion ==
                values.SelectedApplicationVersion;
        return values.Reason switch
        {
            WorkspaceStaffOnboardingRecoveryReason.ExactStateRecovered =>
                values.Outcome ==
                    WorkspaceStaffOnboardingRecoveryOutcome.Recovered &&
                values.OrganizationVerification ==
                    WorkspaceRecoveryVerification.Equivalent &&
                values.StaffVerification ==
                    WorkspaceRecoveryVerification.Equivalent &&
                values.AccessVerification ==
                    WorkspaceRecoveryVerification.Equivalent &&
                values.ResultingApplicationStatus ==
                    WorkspaceStaffOnboardingState.Completed &&
                values.ResultingApplicationVersion ==
                    values.SelectedApplicationVersion + 1,
            WorkspaceStaffOnboardingRecoveryReason
                .LegacyMembershipBindingMissing or
            WorkspaceStaffOnboardingRecoveryReason.ProcessingRestricted =>
                values.Outcome ==
                    WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired &&
                unchanged &&
                values.OrganizationVerification ==
                    WorkspaceRecoveryVerification.Unknown &&
                values.StaffVerification ==
                    WorkspaceRecoveryVerification.Unknown &&
                values.AccessVerification ==
                    WorkspaceRecoveryVerification.Unknown,
            WorkspaceStaffOnboardingRecoveryReason.MembershipConflict =>
                values.Outcome ==
                    WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired &&
                unchanged &&
                values.OrganizationVerification ==
                    WorkspaceRecoveryVerification.ReviewRequired &&
                IsUnknownOrEquivalent(values.StaffVerification) &&
                IsUnknownOrEquivalent(values.AccessVerification),
            WorkspaceStaffOnboardingRecoveryReason.MembershipUnavailable =>
                values.Outcome ==
                    WorkspaceStaffOnboardingRecoveryOutcome.RetryLater &&
                unchanged &&
                values.OrganizationVerification ==
                    WorkspaceRecoveryVerification.RetryLater &&
                IsUnknownOrEquivalent(values.StaffVerification) &&
                IsUnknownOrEquivalent(values.AccessVerification),
            WorkspaceStaffOnboardingRecoveryReason.StaffConflict =>
                values.Outcome ==
                    WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired &&
                unchanged &&
                values.OrganizationVerification ==
                    WorkspaceRecoveryVerification.Equivalent &&
                values.StaffVerification ==
                    WorkspaceRecoveryVerification.ReviewRequired &&
                IsUnknownOrEquivalent(values.AccessVerification),
            WorkspaceStaffOnboardingRecoveryReason.StaffUnavailable =>
                values.Outcome ==
                    WorkspaceStaffOnboardingRecoveryOutcome.RetryLater &&
                unchanged &&
                values.OrganizationVerification ==
                    WorkspaceRecoveryVerification.Equivalent &&
                values.StaffVerification ==
                    WorkspaceRecoveryVerification.RetryLater &&
                IsUnknownOrEquivalent(values.AccessVerification),
            WorkspaceStaffOnboardingRecoveryReason.AccessConflict =>
                values.Outcome ==
                    WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired &&
                unchanged &&
                values.OrganizationVerification ==
                    WorkspaceRecoveryVerification.Equivalent &&
                values.StaffVerification ==
                    WorkspaceRecoveryVerification.Equivalent &&
                values.AccessVerification ==
                    WorkspaceRecoveryVerification.ReviewRequired,
            WorkspaceStaffOnboardingRecoveryReason.AccessUnavailable =>
                values.Outcome ==
                    WorkspaceStaffOnboardingRecoveryOutcome.RetryLater &&
                unchanged &&
                values.OrganizationVerification ==
                    WorkspaceRecoveryVerification.Equivalent &&
                values.StaffVerification ==
                    WorkspaceRecoveryVerification.Equivalent &&
                values.AccessVerification ==
                    WorkspaceRecoveryVerification.RetryLater,
            _ => false
        };
    }

    private static bool StagePresenceIsValid(
        WorkspaceStaffOnboardingRecoveryReceiptValues values)
    {
        bool organizationPresent = values.OrganizationVerification !=
            WorkspaceRecoveryVerification.Unknown;
        bool staffPresent = values.StaffVerification !=
            WorkspaceRecoveryVerification.Unknown;
        bool accessPresent = values.AccessVerification !=
            WorkspaceRecoveryVerification.Unknown;
        bool organizationFactsPresent =
            values.OrganizationRevision.HasValue ||
            values.MembershipId.HasValue ||
            values.MembershipVersion.HasValue ||
            values.MembershipStatus.HasValue;
        bool staffFactsPresent = values.StaffTenantRevision.HasValue ||
            values.StaffMemberVersion.HasValue ||
            values.StaffStateProofSha256 is not null ||
            values.StaffPropertySetSha256 is not null;
        bool accessFactsPresent = values.AccessManagementRevision.HasValue ||
            values.AccessFactsSha256 is not null;
        bool membershipAny = values.MembershipId.HasValue ||
            values.MembershipVersion.HasValue ||
            values.MembershipStatus.HasValue;
        bool membershipAll = values.MembershipId.HasValue &&
            values.MembershipVersion is > 0 &&
            values.MembershipStatus.HasValue;

        if (organizationPresent != values.OrganizationObservedAtUtc.HasValue ||
            staffPresent != values.StaffObservedAtUtc.HasValue ||
            accessPresent != values.AccessObservedAtUtc.HasValue ||
            (!organizationPresent && organizationFactsPresent) ||
            (!staffPresent && staffFactsPresent) ||
            (!accessPresent && accessFactsPresent) ||
            (membershipAny && !membershipAll) ||
            (staffPresent && !organizationPresent) ||
            (accessPresent && !staffPresent) ||
            values.OrganizationRevision is < 0 ||
            values.StaffTenantRevision is < 0 ||
            values.StaffMemberVersion is < 1 ||
            values.AccessManagementRevision is < 0)
        {
            return false;
        }

        if (values.OrganizationVerification ==
                WorkspaceRecoveryVerification.Equivalent &&
            (!values.OrganizationRevision.HasValue ||
             !membershipAll ||
             values.MembershipStatus != ActiveOrganizationMembershipStatus))
        {
            return false;
        }

        if (values.StaffVerification ==
                WorkspaceRecoveryVerification.Equivalent &&
            (!values.StaffTenantRevision.HasValue ||
             !values.StaffMemberVersion.HasValue ||
             !ValidDigest(values.StaffStateProofSha256) ||
             !ValidDigest(values.StaffPropertySetSha256)))
        {
            return false;
        }

        return values.AccessVerification !=
                WorkspaceRecoveryVerification.Equivalent ||
            (values.AccessManagementRevision.HasValue &&
             ValidDigest(values.AccessFactsSha256));
    }

    private static bool IsUnknownOrEquivalent(
        WorkspaceRecoveryVerification verification) =>
        verification is WorkspaceRecoveryVerification.Unknown or
            WorkspaceRecoveryVerification.Equivalent;

    private static bool ObservationAfterDecision(
        DateTimeOffset? observedAtUtc,
        DateTimeOffset recordedAtUtc) =>
        observedAtUtc.HasValue && observedAtUtc.Value > recordedAtUtc;

    private static bool ValidDigest(string? value) =>
        NormalizeDigest(value).Length == DigestLength;

    private static bool ValidOptionalDigest(string? value) =>
        value is null || ValidDigest(value);

    private static string NormalizeDigest(string? value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized.Length == DigestLength &&
            normalized.All(Uri.IsHexDigit)
                ? normalized
                : string.Empty;
    }

    private static string? NormalizeOptionalDigest(string? value) =>
        value is null ? null : NormalizeDigest(value);
}
