namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Workspaces.Domain;
using Gma.Modules.Organizations.Contracts;

internal static class WorkspaceStaffOnboardingProfileMutationAuthority
{
    public static async Task<bool> IsFencedAsync(
        IOrganizationEnrollmentClaimInspector claims,
        WorkspaceStaffOnboardingSource sourceKind,
        Guid organizationId,
        Guid sourceId,
        string subjectId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if (sourceKind == WorkspaceStaffOnboardingSource.Invitation)
        {
            return false;
        }

        if (sourceKind != WorkspaceStaffOnboardingSource.EnrollmentLink ||
            organizationId == Guid.Empty ||
            sourceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(subjectId))
        {
            return true;
        }

        OrganizationEnrollmentClaimDto? claim = await claims.FindAsync(
                organizationId,
                sourceId,
                subjectId,
                cancellationToken)
            .ConfigureAwait(false);
        if (claim is null)
        {
            return false;
        }

        DateTimeOffset persistenceNowUtc = ToPersistencePrecision(nowUtc);
        DateTimeOffset? decisionExpiresAtUtc = claim.DecisionExpiresAtUtc
            .HasValue
                ? ToPersistencePrecision(claim.DecisionExpiresAtUtc.Value)
                : null;

        return claim.OrganizationId != organizationId ||
            claim.EnrollmentLinkId != sourceId ||
            !string.Equals(
                claim.SubjectId,
                subjectId,
                StringComparison.Ordinal) ||
            claim.Status != OrganizationEnrollmentClaimStatus.Pending ||
            !decisionExpiresAtUtc.HasValue ||
            decisionExpiresAtUtc.Value <= persistenceNowUtc;
    }

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
}
