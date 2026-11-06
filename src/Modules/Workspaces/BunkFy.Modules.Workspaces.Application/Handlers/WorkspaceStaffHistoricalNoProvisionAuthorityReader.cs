namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Results;
using Gma.Modules.Organizations.Contracts;

internal sealed class WorkspaceStaffHistoricalNoProvisionAuthorityReader(
    IOrganizationScopeLifecycle organizations)
{
    public async Task<Result<WorkspaceStaffHistoricalNoProvisionAuthority>>
        ReadAsync(
            Guid organizationId,
            WorkspaceStaffOnboardingSource sourceKind,
            Guid sourceId,
            long expectedScopeRevision,
            CancellationToken cancellationToken)
    {
        if (organizationId == Guid.Empty ||
            sourceId == Guid.Empty ||
            expectedScopeRevision < 0 ||
            sourceKind is not (WorkspaceStaffOnboardingSource.Invitation or
                WorkspaceStaffOnboardingSource.EnrollmentLink))
        {
            return Conflict();
        }

        OrganizationScopeSnapshot snapshot = await organizations
            .GetSnapshotAsync(organizationId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot.Status != OrganizationScopeStatus.Open ||
            snapshot.Revision != expectedScopeRevision)
        {
            return Conflict();
        }

        OrganizationScopeExportStore store = sourceKind ==
            WorkspaceStaffOnboardingSource.Invitation
                ? OrganizationScopeExportStore.Invitations
                : OrganizationScopeExportStore.EnrollmentLinks;
        string? afterCursor = null;
        Guid? previousId = null;
        WorkspaceStaffHistoricalNoProvisionAuthority? found = null;
        int matchCount = 0;
        while (true)
        {
            OrganizationScopeExportPage? page = await organizations.ExportAsync(
                    new OrganizationScopeExportRequest(
                        organizationId,
                        expectedScopeRevision,
                        store,
                        afterCursor,
                        OrganizationScopeLifecycleLimits.MaximumPageSize),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!IsValidPage(
                    page,
                    store,
                    expectedScopeRevision))
            {
                return Conflict();
            }

            foreach (OrganizationScopeExportRecord record in page.Records)
            {
                Result<WorkspaceStaffHistoricalNoProvisionAuthority> mapped =
                    Map(record, sourceKind, organizationId);
                if (mapped.IsFailure ||
                    (previousId.HasValue &&
                     mapped.Value.SourceId.CompareTo(previousId.Value) <= 0))
                {
                    return Conflict();
                }

                previousId = mapped.Value.SourceId;
                if (mapped.Value.SourceId == sourceId)
                {
                    matchCount++;
                    found = mapped.Value;
                }
            }

            string? expectedCursor = previousId.HasValue
                ? "id:" + previousId.Value.ToString("D")
                : afterCursor;
            if (!string.Equals(
                    page.NextCursor,
                    expectedCursor,
                    StringComparison.Ordinal) ||
                (page.HasMore &&
                 (page.Records.Count !=
                      OrganizationScopeLifecycleLimits.MaximumPageSize ||
                  string.IsNullOrWhiteSpace(page.NextCursor) ||
                  string.Equals(
                      page.NextCursor,
                      afterCursor,
                      StringComparison.Ordinal))))
            {
                return Conflict();
            }

            if (!page.HasMore)
            {
                break;
            }

            afterCursor = page.NextCursor;
        }

        OrganizationScopeSnapshot finalSnapshot = await organizations
            .GetSnapshotAsync(organizationId, cancellationToken)
            .ConfigureAwait(false);
        if (finalSnapshot.Status != OrganizationScopeStatus.Open ||
            finalSnapshot.Revision != expectedScopeRevision ||
            matchCount != 1 ||
            found is null)
        {
            return Conflict();
        }

        return Result.Success(found);
    }

    private static bool IsValidPage(
        OrganizationScopeExportPage? page,
        OrganizationScopeExportStore store,
        long expectedScopeRevision) =>
        page is not null &&
        page.Status == OrganizationScopeExportStatus.Completed &&
        page.ScopeRevision == expectedScopeRevision &&
        page.Store == store &&
        page.Records is not null &&
        page.Records.Count <=
            OrganizationScopeLifecycleLimits.MaximumPageSize;

    private static Result<WorkspaceStaffHistoricalNoProvisionAuthority> Map(
        OrganizationScopeExportRecord record,
        WorkspaceStaffOnboardingSource sourceKind,
        Guid organizationId)
    {
        if (sourceKind == WorkspaceStaffOnboardingSource.Invitation &&
            record is OrganizationScopeInvitationExportRecord invitation &&
            invitation.InvitationId != Guid.Empty &&
            invitation.OrganizationId == organizationId &&
            invitation.Version >= 1 &&
            Enum.IsDefined(invitation.Status) &&
            invitation.Status != OrganizationInvitationStatus.Unknown)
        {
            return Result.Success(
                new WorkspaceStaffHistoricalNoProvisionAuthority(
                    invitation.InvitationId,
                    invitation.Version,
                    invitation.Status switch
                    {
                        OrganizationInvitationStatus.Pending =>
                            WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                                .InvitationPending,
                        OrganizationInvitationStatus.Accepted =>
                            WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                                .InvitationAccepted,
                        OrganizationInvitationStatus.Revoked =>
                            WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                                .InvitationRevoked,
                        OrganizationInvitationStatus.Superseded =>
                            WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                                .InvitationSuperseded,
                        OrganizationInvitationStatus.Expired =>
                            WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                                .InvitationExpired,
                        _ => WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                            .Unknown
                    }));
        }

        if (sourceKind == WorkspaceStaffOnboardingSource.EnrollmentLink &&
            record is OrganizationScopeEnrollmentLinkExportRecord link &&
            link.EnrollmentLinkId != Guid.Empty &&
            link.OrganizationId == organizationId &&
            link.Version >= 1 &&
            Enum.IsDefined(link.Status) &&
            link.Status != OrganizationEnrollmentLinkStatus.Unknown)
        {
            return Result.Success(
                new WorkspaceStaffHistoricalNoProvisionAuthority(
                    link.EnrollmentLinkId,
                    link.Version,
                    link.Status switch
                    {
                        OrganizationEnrollmentLinkStatus.Active =>
                            WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                                .EnrollmentLinkActive,
                        OrganizationEnrollmentLinkStatus.Disabled =>
                            WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                                .EnrollmentLinkDisabled,
                        OrganizationEnrollmentLinkStatus.Rotated =>
                            WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                                .EnrollmentLinkRotated,
                        OrganizationEnrollmentLinkStatus.Expired =>
                            WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                                .EnrollmentLinkExpired,
                        OrganizationEnrollmentLinkStatus.CapacityReached =>
                            WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                                .EnrollmentLinkCapacityReached,
                        _ => WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                            .Unknown
                    }));
        }

        return Conflict();
    }

    private static Result<WorkspaceStaffHistoricalNoProvisionAuthority>
        Conflict() =>
        Result.Failure<WorkspaceStaffHistoricalNoProvisionAuthority>(
            WorkspaceStaffHistoricalNoProvisionApplicationErrors.Conflict);
}

internal sealed record WorkspaceStaffHistoricalNoProvisionAuthority(
    Guid SourceId,
    long SourceVersion,
    WorkspaceStaffHistoricalNoProvisionAuthorityStatus SourceStatus);
