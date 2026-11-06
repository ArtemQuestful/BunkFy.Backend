namespace BunkFy.Modules.Workspaces.Domain;

using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class WorkspaceStaffDeferredClaimWithdrawal
    : ScopedEntity<Guid>
{
    private WorkspaceStaffDeferredClaimWithdrawal() { }

    private WorkspaceStaffDeferredClaimWithdrawal(
        Guid claimId,
        string scopeId)
        : base(claimId, scopeId)
    {
    }

    public Guid OrganizationId { get; private set; }
    public Guid EnrollmentLinkId { get; private set; }
    public long ClaimVersion { get; private set; }
    public Guid EventId { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }

    public static Result<WorkspaceStaffDeferredClaimWithdrawal> Create(
        string scopeId,
        Guid organizationId,
        Guid enrollmentLinkId,
        Guid claimId,
        long claimVersion,
        Guid eventId,
        DateTimeOffset occurredAtUtc)
    {
        if (!TenantIds.TryNormalize(scopeId, out string? normalizedScopeId) ||
            !Guid.TryParse(normalizedScopeId, out Guid scopedOrganizationId) ||
            scopedOrganizationId != organizationId ||
            organizationId == Guid.Empty ||
            enrollmentLinkId == Guid.Empty ||
            claimId == Guid.Empty ||
            claimVersion <= 0 ||
            eventId == Guid.Empty ||
            occurredAtUtc == default)
        {
            return Result.Failure<WorkspaceStaffDeferredClaimWithdrawal>(
                WorkspaceStaffOnboardingErrors.Invalid);
        }

        return Result.Success(
            new WorkspaceStaffDeferredClaimWithdrawal(
                claimId,
                organizationId.ToString("D"))
            {
                OrganizationId = organizationId,
                EnrollmentLinkId = enrollmentLinkId,
                ClaimVersion = claimVersion,
                EventId = eventId,
                OccurredAtUtc = NormalizeTimestamp(occurredAtUtc)
            });
    }

    public bool Matches(
        string scopeId,
        Guid organizationId,
        Guid enrollmentLinkId,
        Guid claimId,
        long claimVersion,
        Guid eventId,
        DateTimeOffset occurredAtUtc) =>
        TenantIds.TryNormalize(scopeId, out string? normalizedScopeId) &&
        Guid.TryParse(normalizedScopeId, out Guid scopedOrganizationId) &&
        scopedOrganizationId == organizationId &&
        string.Equals(
            this.ScopeId,
            organizationId.ToString("D"),
            StringComparison.Ordinal) &&
        this.OrganizationId == organizationId &&
        this.EnrollmentLinkId == enrollmentLinkId &&
        this.Id == claimId &&
        this.ClaimVersion == claimVersion &&
        this.EventId == eventId &&
        this.OccurredAtUtc == NormalizeTimestamp(occurredAtUtc);

    private static DateTimeOffset NormalizeTimestamp(
        DateTimeOffset timestamp)
    {
        DateTimeOffset utc = timestamp.ToUniversalTime();
        return new DateTimeOffset(
            utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMicrosecond),
            TimeSpan.Zero);
    }
}
