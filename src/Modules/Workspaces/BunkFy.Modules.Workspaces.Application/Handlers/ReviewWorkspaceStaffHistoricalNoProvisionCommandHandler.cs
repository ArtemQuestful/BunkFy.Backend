namespace BunkFy.Modules.Workspaces.Application.Handlers;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class
    ReviewWorkspaceStaffHistoricalNoProvisionCommandHandler(
        IWorkspaceCrossGraphMutationLock crossGraphLock,
        WorkspaceStaffOnboardingMutationCoordinator mutations,
        IWorkspaceStaffHistoricalNoProvisionReceiptRepository receipts,
        IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader outcomes,
        IStaffIdentityProvisioningAnchorCutover cutover,
        WorkspaceStaffHistoricalNoProvisionAuthorityReader authorities,
        IScopeContext scopeContext,
        ISystemClock clock,
        IIdGenerator ids)
    : ICommandHandler<
        ReviewWorkspaceStaffHistoricalNoProvisionCommand,
        WorkspaceStaffHistoricalNoProvisionDispositionResult>
{
    public async Task<
        Result<WorkspaceStaffHistoricalNoProvisionDispositionResult>>
        HandleAsync(
            ReviewWorkspaceStaffHistoricalNoProvisionCommand command,
            CancellationToken cancellationToken)
    {
        if (!WorkspaceStaffIdentityAnchorTenantScope.TryGetCanonicalTenantId(
                scopeContext,
                out string tenantId))
        {
            return Failure(
                WorkspaceStaffHistoricalNoProvisionApplicationErrors
                    .TenantRequired);
        }

        string externalDigest = NormalizeSha256(
            command.ExternalEvidenceSha256);
        string reviewerId = command.ReviewerId?.Trim() ?? string.Empty;
        if (command.OperationId == Guid.Empty ||
            command.ApplicationId == Guid.Empty ||
            command.ExpectedApplicationVersion is < 1 or long.MaxValue ||
            !Enum.IsDefined(command.ExpectedApplicationStatus) ||
            command.ExpectedApplicationStatus ==
                WorkspaceStaffOnboardingState.Unknown ||
            command.ExpectedOrganizationsScopeRevision < 0 ||
            command.ExpectedOrganizationsSourceVersion < 1 ||
            !Enum.IsDefined(command.ExpectedOrganizationsSourceStatus) ||
            command.ExpectedOrganizationsSourceStatus ==
                WorkspaceStaffHistoricalNoProvisionAuthorityStatus.Unknown ||
            command.ExternalEvidenceManifestId == Guid.Empty ||
            !IsSha256(externalDigest) ||
            reviewerId.Length is 0 or >
                WorkspaceStaffHistoricalNoProvisionReceipt
                    .ReviewerIdMaxLength ||
            reviewerId.Any(char.IsControl))
        {
            return Failure(
                WorkspaceStaffHistoricalNoProvisionApplicationErrors
                    .RequestInvalid);
        }

        await crossGraphLock.AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        WorkspaceStaffOnboardingMutationLease lease =
            await mutations.AcquireExistingAsync(
                    command.ApplicationId,
                    WorkspaceStaffOnboardingSourceLockMode.Write,
                    requireOperational: false,
                    cancellationToken)
                .ConfigureAwait(false);
        WorkspaceStaffOnboarding? application = lease.Application;
        WorkspaceStaffHistoricalNoProvisionReceipt? replay = await receipts
            .FindByOperationIdAsync(
                command.OperationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            return application is not null &&
                replay.MatchesReplay(
                    tenantId,
                    command.ApplicationId,
                    command.ExpectedApplicationVersion,
                    command.ExpectedApplicationStatus,
                    command.ExpectedOrganizationsScopeRevision,
                    command.ExpectedOrganizationsSourceVersion,
                    command.ExpectedOrganizationsSourceStatus,
                    command.ExternalEvidenceManifestId,
                    externalDigest,
                    reviewerId) &&
                replay.MatchesResult(application)
                    ? Result.Success(Map(replay, alreadyReviewed: true))
                    : Failure(
                        WorkspaceStaffHistoricalNoProvisionApplicationErrors
                            .Conflict);
        }

        if (application is null ||
            !string.Equals(
                application.ScopeId,
                tenantId,
                StringComparison.Ordinal) ||
            application.Id != command.ApplicationId ||
            application.Version != command.ExpectedApplicationVersion ||
            application.Status != command.ExpectedApplicationStatus ||
            application.HasIdentityAnchorState)
        {
            return Failure(
                WorkspaceStaffHistoricalNoProvisionApplicationErrors
                    .Conflict);
        }

        WorkspaceStaffHistoricalNoProvisionReceipt? prior = await receipts
            .FindByApplicationIdAsync(
                command.ApplicationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (prior is not null)
        {
            return Failure(
                WorkspaceStaffHistoricalNoProvisionApplicationErrors
                    .Conflict);
        }

        Result<string> staffEvidence = await this.ReadStaffEvidenceAsync(
                application,
                cancellationToken)
            .ConfigureAwait(false);
        if (staffEvidence.IsFailure)
        {
            return Failure(staffEvidence.Error);
        }

        if (!Guid.TryParseExact(tenantId, "D", out Guid organizationId))
        {
            return Failure(
                WorkspaceStaffHistoricalNoProvisionApplicationErrors
                    .TenantRequired);
        }

        Guid receiptId = ids.NewId();
        if (receiptId == Guid.Empty)
        {
            return Failure(
                WorkspaceStaffHistoricalNoProvisionApplicationErrors
                    .RequestInvalid);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        DateTimeOffset reviewedAtUtc = ToPersistencePrecision(
            nowUtc < application.LastChangedAtUtc
                ? application.LastChangedAtUtc
                : nowUtc);
        Result<WorkspaceStaffHistoricalNoProvisionAuthority> authority;
        try
        {
            authority = await authorities.ReadAsync(
                    organizationId,
                    application.SourceKind,
                    application.SourceId,
                    command.ExpectedOrganizationsScopeRevision,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            return Failure(
                WorkspaceStaffHistoricalNoProvisionApplicationErrors
                    .ExternalEvidenceUnavailable);
        }

        if (authority.IsFailure ||
            authority.Value.SourceId != application.SourceId ||
            authority.Value.SourceVersion !=
                command.ExpectedOrganizationsSourceVersion ||
            authority.Value.SourceStatus !=
                command.ExpectedOrganizationsSourceStatus ||
            !WorkspaceStaffHistoricalNoProvisionReceipt.IsTerminalAuthority(
                application.SourceKind,
                authority.Value.SourceStatus))
        {
            return Failure(
                WorkspaceStaffHistoricalNoProvisionApplicationErrors
                    .Conflict);
        }

        Result reviewed = application.ReviewHistoricalNoProvision(
            receiptId,
            reviewedAtUtc);
        if (reviewed.IsFailure)
        {
            return Failure(
                WorkspaceStaffHistoricalNoProvisionApplicationErrors
                    .Conflict);
        }

        Result<WorkspaceStaffHistoricalNoProvisionReceipt> created =
            WorkspaceStaffHistoricalNoProvisionReceipt.Create(
                receiptId,
                tenantId,
                command.OperationId,
                application.Id,
                application.SourceKind,
                application.SourceId,
                command.ExpectedApplicationVersion,
                command.ExpectedApplicationStatus,
                application.Version,
                application.Status,
                command.ExpectedOrganizationsScopeRevision,
                authority.Value.SourceVersion,
                authority.Value.SourceStatus,
                staffEvidence.Value,
                command.ExternalEvidenceManifestId,
                externalDigest,
                reviewerId,
                reviewedAtUtc);
        if (created.IsFailure)
        {
            return Failure(
                WorkspaceStaffHistoricalNoProvisionApplicationErrors
                    .Conflict);
        }

        await receipts.AddAsync(created.Value, cancellationToken)
            .ConfigureAwait(false);
        return Result.Success(Map(created.Value, alreadyReviewed: false));
    }

    private async Task<Result<string>> ReadStaffEvidenceAsync(
        WorkspaceStaffOnboarding application,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcome>
            outcomeBatch;
        StaffIdentityProvisioningAnchorInspection inspection;
        try
        {
            outcomeBatch = await outcomes.ReadAsync(
                    [new StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest(
                        application.Id,
                        application.SubjectId)],
                    cancellationToken)
                .ConfigureAwait(false);
            inspection = await cutover.InspectAsync(
                    [new StaffIdentityProvisioningAnchorCandidate(
                        StaffIdentityProvisioningAnchorSourceKind
                            .WorkspaceOnboarding,
                        application.Id,
                        StaffMemberId: null,
                        application.SubjectId)],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            return Result.Failure<string>(
                WorkspaceStaffHistoricalNoProvisionApplicationErrors
                    .ExternalEvidenceUnavailable);
        }

        StaffWorkspaceOnboardingIdentityAnchorOutcome? outcome =
            outcomeBatch is { Count: 1 }
                ? outcomeBatch[0]
                : null;
        bool exactAbsent = outcome is not null &&
            outcome.ApplicationId == application.Id &&
            outcome.Status ==
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Absent &&
            outcome.StaffMemberId is null &&
            outcome.TargetLifecycle ==
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Unknown &&
            outcome.SubjectMatch ==
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Unknown &&
            outcome.WorkspaceApplicationVersion is null &&
            outcome.ResolutionDisposition is null &&
            outcome.ResolutionEventId is null;
        StaffIdentityProvisioningAnchorCandidateInspection? item =
            inspection?.Items is { Count: 1 }
                ? inspection.Items[0]
                : null;
        bool exactAmbiguous = inspection is not null &&
            inspection.IsSuccess &&
            string.IsNullOrWhiteSpace(inspection.ErrorCode) &&
            item is not null &&
            item.SourceKind ==
                StaffIdentityProvisioningAnchorSourceKind.WorkspaceOnboarding &&
            item.SourceId == application.Id &&
            item.Disposition ==
                StaffIdentityProvisioningAnchorCutoverDisposition.Ambiguous;
        return exactAbsent && exactAmbiguous
            ? Result.Success(ComputeStaffEvidenceSha256(application.Id))
            : Result.Failure<string>(
                WorkspaceStaffHistoricalNoProvisionApplicationErrors
                    .Conflict);
    }

    private static string ComputeStaffEvidenceSha256(Guid applicationId)
    {
        StringBuilder canonical = new();
        Append(
            canonical,
            "workspaces-staff-historical-no-provision-staff-evidence|v1");
        Append(canonical, applicationId);
        Append(
            canonical,
            (int)StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Absent);
        Append(canonical, "null-target");
        Append(
            canonical,
            (int)StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Unknown);
        Append(
            canonical,
            (int)StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Unknown);
        Append(canonical, "null-resolution");
        Append(
            canonical,
            (int)StaffIdentityProvisioningAnchorSourceKind.WorkspaceOnboarding);
        Append(
            canonical,
            (int)StaffIdentityProvisioningAnchorCutoverDisposition.Ambiguous);
        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static WorkspaceStaffHistoricalNoProvisionDispositionResult Map(
        WorkspaceStaffHistoricalNoProvisionReceipt receipt,
        bool alreadyReviewed) =>
        new(
            receipt.Id,
            receipt.OperationId,
            receipt.ApplicationId,
            receipt.ResultApplicationVersion,
            receipt.ResultApplicationStatus,
            receipt.StaffEvidenceSha256,
            receipt.CanonicalSha256,
            receipt.ReviewedAtUtc,
            alreadyReviewed);

    private static Result<
        WorkspaceStaffHistoricalNoProvisionDispositionResult> Failure(
            Error error) =>
        Result.Failure<WorkspaceStaffHistoricalNoProvisionDispositionResult>(
            error);

    private static string NormalizeSha256(string? value) =>
        value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static bool IsSha256(string value) =>
        value.Length == WorkspaceStaffHistoricalNoProvisionReceipt
            .Sha256Length &&
        value.All(character => character is (>= '0' and <= '9') or
            (>= 'a' and <= 'f'));

    private static DateTimeOffset ToPersistencePrecision(
        DateTimeOffset value)
    {
        const long ticksPerMicrosecond =
            TimeSpan.TicksPerMillisecond / 1000;
        DateTimeOffset utc = value.ToUniversalTime();
        return new(
            utc.Ticks - (utc.Ticks % ticksPerMicrosecond),
            TimeSpan.Zero);
    }

    private static void Append(StringBuilder target, object value)
    {
        string text = value switch
        {
            Guid id => id.ToString("N"),
            IFormattable formattable => formattable.ToString(
                null,
                CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
        target.Append(text.Length.ToString(CultureInfo.InvariantCulture));
        target.Append(':');
        target.Append(text);
    }
}
