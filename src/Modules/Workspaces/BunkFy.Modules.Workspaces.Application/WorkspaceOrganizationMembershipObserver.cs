namespace BunkFy.Modules.Workspaces.Application;

using Gma.Modules.Organizations.Contracts;
using Gma.Framework.Runtime.Time;

internal sealed class WorkspaceOrganizationMembershipObserver(
    IEnumerable<IOrganizationScopeLifecycle> lifecycles,
    IEnumerable<IOrganizationMembershipReader> readers,
    ISystemClock clock)
{
    private readonly IOrganizationScopeLifecycle? lifecycle =
        lifecycles.SingleOrDefault();
    private readonly IOrganizationMembershipReader? reader =
        readers.SingleOrDefault();

    public async Task<WorkspaceOrganizationMembershipObservation> ObserveAsync(
        string workspaceId,
        string subjectId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset observedAtUtc = clock.UtcNow;
        if (this.lifecycle is null || this.reader is null ||
            !Guid.TryParse(workspaceId, out Guid organizationId) ||
            organizationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(subjectId))
        {
            return WorkspaceOrganizationMembershipObservation.ReviewRequired(
                WorkspaceOrganizationMembershipReason.InvalidOrUnsupported,
                observedAtUtc);
        }

        try
        {
            OrganizationScopeSnapshot before = await this.lifecycle
                .GetSnapshotAsync(organizationId, cancellationToken)
                .ConfigureAwait(false);
            if (before.Status != OrganizationScopeStatus.Open)
            {
                return before.Status is OrganizationScopeStatus.Missing or
                    OrganizationScopeStatus.Invalid or
                    OrganizationScopeStatus.Closed
                    ? WorkspaceOrganizationMembershipObservation
                        .ReviewRequired(
                            WorkspaceOrganizationMembershipReason.ScopeClosed,
                            observedAtUtc,
                            before.Revision)
                    : WorkspaceOrganizationMembershipObservation.RetryLater(
                        WorkspaceOrganizationMembershipReason.ScopeUnavailable,
                        observedAtUtc,
                        before.Revision);
            }

            OrganizationMembershipSnapshotDto? snapshot = await this.reader
                .FindAsync(organizationId, subjectId, cancellationToken)
                .ConfigureAwait(false);
            OrganizationScopeSnapshot after = await this.lifecycle
                .GetSnapshotAsync(organizationId, cancellationToken)
                .ConfigureAwait(false);
            if (after.Status != OrganizationScopeStatus.Open ||
                after.Revision != before.Revision)
            {
                return WorkspaceOrganizationMembershipObservation.RetryLater(
                    WorkspaceOrganizationMembershipReason.RevisionChanged,
                    observedAtUtc,
                    before.Revision);
            }

            OrganizationMembershipDto? membership = snapshot?.Membership;
            if (snapshot?.OrganizationId != organizationId ||
                snapshot.OrganizationStatus != OrganizationStatus.Active ||
                membership?.OrganizationId != organizationId ||
                membership.MembershipId == Guid.Empty ||
                membership.Version < 1 ||
                membership.Role != OrganizationMembershipRole.Member ||
                membership.Status != OrganizationMembershipStatus.Active ||
                !string.Equals(
                    membership.SubjectId,
                    subjectId,
                    StringComparison.Ordinal))
            {
                return WorkspaceOrganizationMembershipObservation
                    .ReviewRequired(
                        WorkspaceOrganizationMembershipReason.NotActiveMember,
                        observedAtUtc,
                        after.Revision);
            }

            return WorkspaceOrganizationMembershipObservation.Equivalent(
                after.Revision,
                membership.MembershipId,
                membership.Version,
                membership.Status,
                observedAtUtc);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return WorkspaceOrganizationMembershipObservation.RetryLater(
                WorkspaceOrganizationMembershipReason.PersistenceUnavailable,
                observedAtUtc);
        }
    }
}

internal sealed record WorkspaceOrganizationMembershipObservation(
    WorkspaceRecoveryVerificationStatus Status,
    WorkspaceOrganizationMembershipReason Reason,
    long? OrganizationRevision,
    Guid? MembershipId,
    long? MembershipVersion,
    OrganizationMembershipStatus? MembershipStatus,
    DateTimeOffset ObservedAtUtc)
{
    public static WorkspaceOrganizationMembershipObservation Equivalent(
        long revision,
        Guid membershipId,
        long membershipVersion,
        OrganizationMembershipStatus status,
        DateTimeOffset observedAtUtc) => new(
        WorkspaceRecoveryVerificationStatus.Equivalent,
        WorkspaceOrganizationMembershipReason.Equivalent,
        revision,
        membershipId,
        membershipVersion,
        status,
        observedAtUtc);

    public static WorkspaceOrganizationMembershipObservation ReviewRequired(
        WorkspaceOrganizationMembershipReason reason,
        DateTimeOffset observedAtUtc,
        long? revision = null) => new(
        WorkspaceRecoveryVerificationStatus.ReviewRequired,
        reason,
        revision,
        null,
        null,
        null,
        observedAtUtc);

    public static WorkspaceOrganizationMembershipObservation RetryLater(
        WorkspaceOrganizationMembershipReason reason,
        DateTimeOffset observedAtUtc,
        long? revision = null) => new(
        WorkspaceRecoveryVerificationStatus.RetryLater,
        reason,
        revision,
        null,
        null,
        null,
        observedAtUtc);
}

internal enum WorkspaceRecoveryVerificationStatus
{
    Unknown = 0,
    Equivalent = 1,
    ReviewRequired = 2,
    RetryLater = 3
}

internal enum WorkspaceOrganizationMembershipReason
{
    Unknown = 0,
    Equivalent = 1,
    InvalidOrUnsupported = 2,
    ScopeClosed = 3,
    ScopeUnavailable = 4,
    RevisionChanged = 5,
    NotActiveMember = 6,
    PersistenceUnavailable = 7,
    BindingMissing = 8,
    BindingChanged = 9
}
