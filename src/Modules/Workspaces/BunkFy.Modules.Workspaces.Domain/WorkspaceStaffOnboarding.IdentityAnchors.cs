namespace BunkFy.Modules.Workspaces.Domain;

using BunkFy.Modules.Workspaces.Domain.Events;
using Gma.Framework.Results;

public sealed partial class WorkspaceStaffOnboarding
{
    public Result ConvergeCommittedStaffAnchor(
        Guid staffMemberId,
        Guid expectedResolutionEventId,
        Guid continuationEventId,
        DateTimeOffset nowUtc)
    {
        if (!AreAnchorCoordinatesValid(
                this.Id,
                staffMemberId,
                expectedResolutionEventId,
                continuationEventId) ||
            (this.StaffMemberId.HasValue &&
                this.StaffMemberId.Value != staffMemberId) ||
            (this.IdentityAnchorExpectedResolutionEventId.HasValue &&
                this.IdentityAnchorExpectedResolutionEventId.Value !=
                    expectedResolutionEventId) ||
            (this.IdentityAnchorContinuationEventId.HasValue &&
                this.IdentityAnchorContinuationEventId.Value !=
                    continuationEventId) ||
            this.IdentityAnchorResolutionEventId.HasValue ||
            this.IsTerminal())
        {
            return Result.Failure(WorkspaceStaffOnboardingErrors.StateConflict);
        }

        bool alreadyConverged =
            this.StaffMemberId == staffMemberId &&
            this.IdentityAnchorExpectedResolutionEventId ==
                expectedResolutionEventId &&
            this.IdentityAnchorContinuationEventId == continuationEventId &&
            this.Status == WorkspaceStaffOnboardingState.StaffReady &&
            this.IsApplicantDataRedacted();
        if (alreadyConverged)
        {
            return Result.Success();
        }

        this.StaffMemberId = staffMemberId;
        this.IdentityAnchorExpectedResolutionEventId =
            expectedResolutionEventId;
        this.IdentityAnchorContinuationEventId = continuationEventId;
        this.Status = WorkspaceStaffOnboardingState.StaffReady;
        this.FailureCode = null;
        this.RedactApplicantData();
        this.Advance(nowUtc);
        this.RaiseContinuationRequested(staffMemberId, nowUtc);
        return Result.Success();
    }

    public Result ConvergeTerminalStaffAnchor(
        Guid staffMemberId,
        Guid expectedResolutionEventId,
        DateTimeOffset nowUtc)
    {
        if (staffMemberId == Guid.Empty ||
            expectedResolutionEventId == Guid.Empty ||
            expectedResolutionEventId == this.Id ||
            (this.StaffMemberId.HasValue &&
                this.StaffMemberId.Value != staffMemberId) ||
            (this.IdentityAnchorExpectedResolutionEventId.HasValue &&
                this.IdentityAnchorExpectedResolutionEventId.Value !=
                    expectedResolutionEventId) ||
            this.IdentityAnchorContinuationEventId ==
                expectedResolutionEventId)
        {
            return Result.Failure(WorkspaceStaffOnboardingErrors.StateConflict);
        }

        WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition disposition =
            this.Status switch
            {
                WorkspaceStaffOnboardingState.Rejected =>
                    WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                        .RejectedRedacted,
                WorkspaceStaffOnboardingState.Expired =>
                    WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                        .ExpiredRedacted,
                WorkspaceStaffOnboardingState.Withdrawn =>
                    WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                        .WithdrawnRedacted,
                WorkspaceStaffOnboardingState.Completed =>
                    WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                        .CompletedRedacted,
                _ => WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                    .SupersededRedacted
            };

        if (!this.IsTerminal())
        {
            this.Status = WorkspaceStaffOnboardingState.Superseded;
        }

        this.StaffMemberId = staffMemberId;
        this.IdentityAnchorExpectedResolutionEventId =
            expectedResolutionEventId;
        this.FailureCode = null;
        this.RedactApplicantData();
        return this.RecordResolutionIntent(
            staffMemberId,
            disposition,
            nowUtc);
    }

    public Result ConvergeMismatchedStaffAnchor(
        Guid staffMemberId,
        Guid expectedResolutionEventId,
        DateTimeOffset nowUtc)
    {
        if (staffMemberId == Guid.Empty ||
            expectedResolutionEventId == Guid.Empty ||
            expectedResolutionEventId == this.Id ||
            (this.StaffMemberId.HasValue &&
                this.StaffMemberId.Value != staffMemberId) ||
            (this.IdentityAnchorExpectedResolutionEventId.HasValue &&
                this.IdentityAnchorExpectedResolutionEventId.Value !=
                    expectedResolutionEventId) ||
            this.IdentityAnchorContinuationEventId ==
                expectedResolutionEventId)
        {
            return Result.Failure(
                WorkspaceStaffOnboardingErrors.StateConflict);
        }

        WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition disposition =
            this.Status switch
            {
                WorkspaceStaffOnboardingState.Rejected =>
                    WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                        .RejectedRedacted,
                WorkspaceStaffOnboardingState.Expired =>
                    WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                        .ExpiredRedacted,
                WorkspaceStaffOnboardingState.Withdrawn =>
                    WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                        .WithdrawnRedacted,
                WorkspaceStaffOnboardingState.Superseded =>
                    WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                        .SupersededRedacted,
                _ => WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                    .SupersededRedacted
            };
        if (this.Status is not (
            WorkspaceStaffOnboardingState.Rejected or
            WorkspaceStaffOnboardingState.Expired or
            WorkspaceStaffOnboardingState.Withdrawn or
            WorkspaceStaffOnboardingState.Superseded))
        {
            this.Status = WorkspaceStaffOnboardingState.Superseded;
        }

        this.StaffMemberId = staffMemberId;
        this.IdentityAnchorExpectedResolutionEventId =
            expectedResolutionEventId;
        this.FailureCode = null;
        this.RedactApplicantData();
        return this.RecordResolutionIntent(
            staffMemberId,
            disposition,
            nowUtc);
    }

    public Result Complete(DateTimeOffset nowUtc)
    {
        if (this.Status == WorkspaceStaffOnboardingState.Completed)
        {
            return Result.Success();
        }

        if (this.Status != WorkspaceStaffOnboardingState.StaffReady || !this.StaffMemberId.HasValue)
        {
            return Result.Failure(WorkspaceStaffOnboardingErrors.StateConflict);
        }

        this.Status = WorkspaceStaffOnboardingState.Completed;
        this.FailureCode = null;
        this.RedactApplicantData();
        return this.RecordResolutionIntent(
            this.StaffMemberId.Value,
            WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                .CompletedRedacted,
            nowUtc);
    }

    public Result RecordResolutionIntent(
        Guid staffMemberId,
        WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
            disposition,
        DateTimeOffset intentAtUtc)
    {
        if (staffMemberId == Guid.Empty ||
            !IsDefinedResolutionDisposition(disposition) ||
            this.Id == Guid.Empty ||
            this.StaffMemberId != staffMemberId ||
            !this.IdentityAnchorExpectedResolutionEventId.HasValue ||
            this.IdentityAnchorExpectedResolutionEventId.Value == Guid.Empty ||
            this.IdentityAnchorExpectedResolutionEventId.Value == this.Id ||
            this.IdentityAnchorContinuationEventId ==
                this.IdentityAnchorExpectedResolutionEventId ||
            !this.IsTerminal() ||
            !this.IsApplicantDataRedacted())
        {
            return Result.Failure(WorkspaceStaffOnboardingErrors.StateConflict);
        }

        if (this.IdentityAnchorResolutionEventId.HasValue)
        {
            return this.IdentityAnchorResolutionEventId ==
                    this.IdentityAnchorExpectedResolutionEventId &&
                this.IdentityAnchorResolutionStaffMemberId == staffMemberId &&
                this.IdentityAnchorResolutionApplicationVersion.HasValue &&
                this.IdentityAnchorResolutionApplicationVersion.Value <=
                    this.Version &&
                this.IdentityAnchorResolutionDisposition == disposition
                    ? Result.Success()
                    : Result.Failure(WorkspaceStaffOnboardingErrors.StateConflict);
        }

        DateTimeOffset normalizedIntentAtUtc = intentAtUtc.ToUniversalTime();
        this.Advance(normalizedIntentAtUtc);
        this.IdentityAnchorResolutionEventId =
            this.IdentityAnchorExpectedResolutionEventId.Value;
        this.IdentityAnchorResolutionStaffMemberId = staffMemberId;
        this.IdentityAnchorResolutionApplicationVersion = this.Version;
        this.IdentityAnchorResolutionDisposition = disposition;
        this.IdentityAnchorResolutionIntentAtUtc = normalizedIntentAtUtc;
        this.RaiseDomainEvent(
            new WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent(
                this.IdentityAnchorResolutionEventId.Value,
                normalizedIntentAtUtc,
                this.ScopeId,
                this.Id,
                staffMemberId,
                this.Version,
                disposition));
        return Result.Success();
    }

    public Result ObserveResolution(
        Guid resolutionEventId,
        Guid staffMemberId,
        long workspaceApplicationVersion,
        WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
            disposition,
        DateTimeOffset observedAtUtc)
    {
        if (resolutionEventId == Guid.Empty ||
            staffMemberId == Guid.Empty ||
            workspaceApplicationVersion <= 0 ||
            !IsDefinedResolutionDisposition(disposition) ||
            this.IdentityAnchorResolutionEventId != resolutionEventId ||
            this.IdentityAnchorExpectedResolutionEventId != resolutionEventId ||
            this.IdentityAnchorResolutionStaffMemberId != staffMemberId ||
            this.IdentityAnchorResolutionApplicationVersion !=
                workspaceApplicationVersion ||
            this.IdentityAnchorResolutionDisposition != disposition ||
            this.StaffMemberId != staffMemberId ||
            this.Version < workspaceApplicationVersion ||
            !this.IsTerminal() ||
            !this.IsApplicantDataRedacted())
        {
            return Result.Failure(WorkspaceStaffOnboardingErrors.StateConflict);
        }

        if (this.IdentityAnchorResolutionObservedAtUtc.HasValue)
        {
            return Result.Success();
        }

        DateTimeOffset effectiveObservedAtUtc = observedAtUtc.ToUniversalTime();
        if (this.IdentityAnchorResolutionIntentAtUtc!.Value >
            effectiveObservedAtUtc)
        {
            effectiveObservedAtUtc =
                this.IdentityAnchorResolutionIntentAtUtc.Value;
        }

        if (this.LastChangedAtUtc > effectiveObservedAtUtc)
        {
            effectiveObservedAtUtc = this.LastChangedAtUtc;
        }

        this.IdentityAnchorResolutionObservedAtUtc = effectiveObservedAtUtc;
        this.Advance(effectiveObservedAtUtc);
        return Result.Success();
    }

    public Result Fail(string failureCode, DateTimeOffset nowUtc)
    {
        if (!TryNormalizeRequired(
                failureCode,
                WorkspaceStaffOnboardingRules.FailureCodeMaxLength,
                out string? normalized))
        {
            return Result.Failure(WorkspaceStaffOnboardingErrors.Invalid);
        }

        if (this.Status is WorkspaceStaffOnboardingState.Completed or
            WorkspaceStaffOnboardingState.Rejected or
            WorkspaceStaffOnboardingState.Superseded or
            WorkspaceStaffOnboardingState.Expired or
            WorkspaceStaffOnboardingState.Withdrawn)
        {
            return Result.Failure(WorkspaceStaffOnboardingErrors.StateConflict);
        }

        this.Status = WorkspaceStaffOnboardingState.Failed;
        this.FailureCode = normalized;
        this.Advance(nowUtc);
        return Result.Success();
    }

    public Result Supersede(DateTimeOffset nowUtc)
    {
        if (this.Status is WorkspaceStaffOnboardingState.Completed or
            WorkspaceStaffOnboardingState.Rejected or
            WorkspaceStaffOnboardingState.Superseded or
            WorkspaceStaffOnboardingState.Expired or
            WorkspaceStaffOnboardingState.Withdrawn)
        {
            return Result.Success();
        }

        this.Status = WorkspaceStaffOnboardingState.Superseded;
        this.FailureCode = null;
        this.RedactApplicantData();
        this.Advance(nowUtc);
        return Result.Success();
    }

    public Result ReviewHistoricalNoProvision(
        Guid receiptId,
        DateTimeOffset reviewedAtUtc)
    {
        string pseudonym = WorkspaceStaffHistoricalNoProvisionReceipt
            .CreateSubjectPseudonym(receiptId);
        if (pseudonym.Length == 0 ||
            reviewedAtUtc == default ||
            this.HasIdentityAnchorState ||
            !Enum.IsDefined(this.Status) ||
            this.Status == WorkspaceStaffOnboardingState.Unknown)
        {
            return Result.Failure(
                WorkspaceStaffHistoricalNoProvisionErrors.ReceiptInvalid);
        }

        bool terminal = this.IsTerminal();
        bool changed = !terminal ||
            !string.Equals(
                this.SubjectId,
                pseudonym,
                StringComparison.Ordinal) ||
            !this.IsApplicantDataRedacted() ||
            this.FailureCode is not null;
        if (!changed)
        {
            return Result.Success();
        }

        if (!terminal)
        {
            this.Status = WorkspaceStaffOnboardingState.Superseded;
        }

        this.SubjectId = pseudonym;
        this.FailureCode = null;
        this.RedactApplicantData();
        this.Advance(reviewedAtUtc.ToUniversalTime());
        return Result.Success();
    }
}
