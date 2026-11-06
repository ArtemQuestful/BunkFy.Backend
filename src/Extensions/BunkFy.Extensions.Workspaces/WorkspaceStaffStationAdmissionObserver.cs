namespace BunkFy.Extensions.Workspaces;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.TimeZones;
using Gma.Framework.AccessControl;
using Gma.Framework.Permissions;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.Options;
using NodaTime;
using Reason = BunkFy.Modules.Workspaces.Contracts.WorkspaceStaffStationObservationReason;
using Status = BunkFy.Modules.Workspaces.Contracts.WorkspaceStaffStationObservationStatus;

internal sealed class WorkspaceStaffStationAdmissionObserver(
    IScopeContext scope,
    IStaffStationEligibilitySource staff,
    IPropertyStationEligibilitySource properties,
    IAuthMemberAdmissionReader accounts,
    IOrganizationMembershipReader memberships,
    IOrganizationScopeLifecycle organizations,
    IAccessAuthorizationService authorization,
    IAccessControlSubjectAuthoritySnapshotReader authority,
    IWorkspaceOperationalAdmissionPolicy workspace,
    IWorkspaceTerminationFenceReader fences,
    IOptions<BunkFyWorkspacesOptions> options,
    ISystemClock clock) : IWorkspaceStaffStationAdmissionObserver
{
    public async Task<WorkspaceStaffStationObservation> ObserveAsync(string scopeId, Guid propertyId,
        Guid staffMemberId, WorkspaceStaffStationAction action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset observedAt = clock.UtcNow;
        DateOnly? localDate = null;
        string? zoneId = null;
        WorkspaceStaffStationObservation Result(Status status, Reason reason) => new(status, reason,
            observedAt, localDate, zoneId, TimeZoneCatalog.Default.CatalogVersion, TimeZoneCatalog.Default.TzdbVersion);
        if (!scope.IsEnabled || action is not (WorkspaceStaffStationAction.ReservationCheckIn or WorkspaceStaffStationAction.ReservationCheckOut or WorkspaceStaffStationAction.StationIdentity) ||
            propertyId == Guid.Empty || staffMemberId == Guid.Empty ||
            !Guid.TryParseExact(scopeId, "D", out Guid tenantId) || tenantId == Guid.Empty ||
            tenantId.ToString("D") != scopeId || scope.ScopeId != scopeId || observedAt == default)
        {
            return Result(Status.PrerequisitesNotObserved, Reason.InvalidRequest);
        }

        try
        {
            Facts before = await this.ReadFacts(scopeId, propertyId, staffMemberId, cancellationToken).ConfigureAwait(false);
            if (before.Property is not null && !TryLocalDate(before.Property.TimeZoneId, observedAt, out localDate, out zoneId))
            {
                return Result(Status.Unavailable, Reason.OwnerEvidenceUnavailable);
            }
            (Status Status, Reason Reason)? initial = Classify(before, scopeId, propertyId, staffMemberId, localDate);
            if (initial is not null)
            {
                Facts afterNegative = await this.ReadFacts(scopeId, propertyId, staffMemberId, cancellationToken).ConfigureAwait(false);
                return before != afterNegative || !StillSameDate(before, clock.UtcNow)
                    ? Result(Status.ObservationStale, Reason.ChangedDuringObservation)
                    : Result(initial.Value.Status, initial.Value.Reason);
            }

            string subjectId = before.Staff!.AuthSubjectId!;
            if (!Guid.TryParseExact(subjectId, "D", out Guid accountId) || accountId == Guid.Empty ||
                subjectId != accountId.ToString("D"))
            {
                return Result(Status.Unavailable, Reason.OwnerEvidenceUnavailable);
            }
            string authScope = options.Value.GlobalAuthScopeId;
            if (string.IsNullOrWhiteSpace(authScope) || authScope.Trim() != authScope)
            {
                return Result(Status.Unavailable, Reason.OwnerEvidenceUnavailable);
            }
            AccessScope propertyScope = WorkspaceAccessScopes.CreateProperty(scopeId, propertyId);
            AccessRequirement? requirement = action == WorkspaceStaffStationAction.StationIdentity ? null : new(AccessSubject.User(subjectId),
                PermissionCode.Create(action == WorkspaceStaffStationAction.ReservationCheckIn
                    ? ReservationsAdminPermissionCodes.CheckIn : ReservationsAdminPermissionCodes.CheckOut), propertyScope);

            // Two account-existence reads bracket the other owners, but are not a session check or revision fence.
            bool accountBefore = await accounts.FindActiveAsync(authScope, accountId, cancellationToken).ConfigureAwait(false) is not null;
            External beforeExternal = await this.ReadExternal(scopeId, tenantId, subjectId, propertyScope, false, requirement is not null, cancellationToken).ConfigureAwait(false);
            AccessDecision? decisionBefore = requirement is null ? null : await authorization.AuthorizeAsync(requirement, cancellationToken).ConfigureAwait(false);
            Facts after = await this.ReadFacts(scopeId, propertyId, staffMemberId, cancellationToken).ConfigureAwait(false);
            AccessDecision? decisionAfter = requirement is null ? null : await authorization.AuthorizeAsync(requirement, cancellationToken).ConfigureAwait(false);
            External afterExternal = await this.ReadExternal(scopeId, tenantId, subjectId, propertyScope, true, requirement is not null, cancellationToken).ConfigureAwait(false);
            bool accountAfter = await accounts.FindActiveAsync(authScope, accountId, cancellationToken).ConfigureAwait(false) is not null;
            DateTimeOffset completedAt = clock.UtcNow;

            if (before != after || accountBefore != accountAfter || decisionBefore != decisionAfter ||
                !Equivalent(beforeExternal, afterExternal) || !StillSameDate(after, completedAt) ||
                beforeExternal.Authority?.Status == AccessControlAuthoritySnapshotStatus.Stale ||
                afterExternal.Authority?.Status == AccessControlAuthoritySnapshotStatus.Stale ||
                (beforeExternal.Authority?.EarliestExpiryAtUtc is { } expiry && expiry > observedAt && expiry <= completedAt))
            {
                return Result(Status.ObservationStale, Reason.ChangedDuringObservation);
            }
            if (!ValidExternal(beforeExternal, tenantId, subjectId, propertyScope, observedAt, completedAt, requirement is not null) ||
                !ValidExternal(afterExternal, tenantId, subjectId, propertyScope, observedAt, completedAt, requirement is not null))
            {
                return Result(Status.Unavailable, Reason.OwnerEvidenceUnavailable);
            }
            if (!accountAfter)
            {
                return Result(Status.PrerequisitesNotObserved, Reason.AuthAccountNotActive);
            }
            if (afterExternal.Workspace.Outcome == WorkspaceOperationalAdmissionOutcome.Restricted ||
                afterExternal.Fence is not null || afterExternal.OrganizationScope.Status != OrganizationScopeStatus.Open ||
                afterExternal.Authority?.Status == AccessControlAuthoritySnapshotStatus.Closed)
            {
                return Result(Status.PrerequisitesNotObserved, Reason.WorkspaceClosed);
            }
            if (afterExternal.Membership is not
                {
                    OrganizationStatus: OrganizationStatus.Active,
                    Membership:
                    {
                        Status: OrganizationMembershipStatus.Active,
                        Role: OrganizationMembershipRole.Owner or OrganizationMembershipRole.Member
                    }
                })
            {
                return Result(Status.PrerequisitesNotObserved, Reason.MembershipNotActive);
            }
            if (requirement is not null && (decisionAfter?.IsAllowed != true || afterExternal.Authority?.EarliestExpiryAtUtc <= completedAt))
            {
                return Result(Status.PrerequisitesNotObserved, Reason.PermissionNotObserved);
            }
            return Result(Status.LinkedAccountPrerequisitesObserved, Reason.LinkedAccountPrerequisites);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // No contact data or raw provider exception is returned or logged.
            return Result(Status.Unavailable, Reason.OwnerEvidenceUnavailable);
        }

        bool StillSameDate(Facts facts, DateTimeOffset finishedAt) => scope.IsEnabled && scope.ScopeId == scopeId &&
            finishedAt >= observedAt && (facts.Property is null ||
                (TryLocalDate(facts.Property.TimeZoneId, finishedAt, out DateOnly? finalDate, out string? finalZone) &&
                finalDate == localDate && finalZone == zoneId));
    }

    private async Task<Facts> ReadFacts(string tenant, Guid property, Guid member, CancellationToken ct) => new(
        await staff.FindAsync(tenant, property, member, ct).ConfigureAwait(false),
        await properties.FindAsync(tenant, property, ct).ConfigureAwait(false));

    private async Task<External> ReadExternal(string tenant, Guid organization, string subject,
        AccessScope propertyScope, bool closing, bool includeAuthority, CancellationToken ct)
    {
        OrganizationScopeSnapshot? organizationScope = closing ? null :
            await organizations.GetSnapshotAsync(organization, ct).ConfigureAwait(false);
        WorkspaceOperationalAdmissionDecision operational = await workspace.EvaluateAsync(tenant, ct).ConfigureAwait(false);
        WorkspaceTerminationFenceSnapshot? fence = await fences.GetCurrentAsync(ct).ConfigureAwait(false);
        OrganizationMembershipSnapshotDto? membership = await memberships.FindAsync(organization, subject, ct).ConfigureAwait(false);
        AccessControlSubjectAuthoritySnapshot? access = includeAuthority ? await authority.ReadAsync(
            new(AccessSubjectKind.User, subject, propertyScope, [propertyScope]), ct).ConfigureAwait(false) : null;
        organizationScope ??= await organizations.GetSnapshotAsync(organization, ct).ConfigureAwait(false);
        return new(operational, fence, organizationScope, membership, access);
    }

    private static (Status, Reason)? Classify(Facts facts, string tenant, Guid property, Guid member, DateOnly? date)
    {
        if (facts.Staff is null || facts.Property is null)
        {
            return (Status.PrerequisitesNotObserved, Reason.OwnerRecordMissing);
        }
        StaffStationEligibilitySnapshot s = facts.Staff;
        PropertyStationEligibilitySnapshot p = facts.Property;
        if (s.ScopeId != tenant || p.ScopeId != tenant || s.PropertyId != property || p.PropertyId != property ||
            s.StaffMemberId != member || s.Version < 1 || p.Version < 1 ||
            s.Status is not (StaffStatus.Active or StaffStatus.Suspended or StaffStatus.Departed) ||
            p.Status is not (PropertyStatus.Active or PropertyStatus.Retired) ||
            p.ConfiguredProcessingStatus is not (PropertyProcessingStatus.Unconfigured or PropertyProcessingStatus.Enabled or PropertyProcessingStatus.Suspended) ||
            s.AuthLinkState is not (StaffStationAuthLinkState.Linked or StaffStationAuthLinkState.Unlinked) ||
            s.AssignmentState is not (StaffStationAssignmentState.Open or StaffStationAssignmentState.None) ||
            s.ProcessingRestriction.ObservedContractVersion != StaffProcessingRestrictionContract.CurrentVersion ||
            s.ProcessingRestriction.ProjectionRevision is null or < 0 ||
            s.ProcessingRestriction.Decision is not (StaffProcessingRestrictionDecision.Allowed or StaffProcessingRestrictionDecision.Restricted))
        {
            return (Status.Unavailable, Reason.OwnerEvidenceUnavailable);
        }
        if (s.Status != StaffStatus.Active || p.Status != PropertyStatus.Active)
        {
            return (Status.PrerequisitesNotObserved, Reason.StaffOrPropertyInactive);
        }
        if (s.ProcessingRestriction.Decision == StaffProcessingRestrictionDecision.Restricted)
        {
            return (Status.PrerequisitesNotObserved, Reason.StaffProcessingRestricted);
        }
        if (s.AssignmentState == StaffStationAssignmentState.None)
        {
            return s.OpenAssignment is null ? (Status.PrerequisitesNotObserved, Reason.AssignmentNotEffective) :
                (Status.Unavailable, Reason.OwnerEvidenceUnavailable);
        }
        if (s.OpenAssignment is not { } assignment || assignment.AssignmentId == Guid.Empty ||
            assignment.PropertyId != property || assignment.AssignedAtVersion < 1 ||
            assignment.AssignedAtVersion > s.Version || assignment.EffectiveFrom == default || date is null)
        {
            return (Status.Unavailable, Reason.OwnerEvidenceUnavailable);
        }
        if (assignment.EffectiveFrom > date)
        {
            return (Status.PrerequisitesNotObserved, Reason.AssignmentNotEffective);
        }
        if (s.AuthLinkState == StaffStationAuthLinkState.Unlinked)
        {
            return s.AuthSubjectId is null ? (Status.StationOnlyAccessUnsupported, Reason.StationOnlyAccessNotImplemented) :
                (Status.Unavailable, Reason.OwnerEvidenceUnavailable);
        }
        return null;
    }

    private static bool TryLocalDate(string timeZone, DateTimeOffset instant, out DateOnly? date, out string? canonical)
    {
        date = null;
        canonical = null;
        if (!TimeZoneCatalog.Default.TryResolve(timeZone, out TimeZoneCatalogResolution? resolution) ||
            resolution.Kind != TimeZoneCatalogResolutionKind.Canonical || timeZone != resolution.CanonicalTimeZoneId)
        {
            return false;
        }
        LocalDate local = Instant.FromDateTimeOffset(instant).InZone(DateTimeZoneProviders.Tzdb[resolution.CanonicalTimeZoneId]).Date;
        date = new(local.Year, local.Month, local.Day);
        canonical = resolution.CanonicalTimeZoneId;
        return true;
    }

    private static bool ValidExternal(External e, Guid organization, string subject, AccessScope target,
        DateTimeOffset startedAt, DateTimeOffset completedAt, bool requireAuthority)
    {
        AccessControlSubjectAuthoritySnapshot? a = e.Authority;
        OrganizationMembershipDto? m = e.Membership?.Membership;
        return e.Workspace.Outcome is WorkspaceOperationalAdmissionOutcome.Allowed or WorkspaceOperationalAdmissionOutcome.Restricted &&
            (e.Fence is null || (e.Fence.ProcessId != Guid.Empty && e.Fence.TerminationEpoch != Guid.Empty && e.Fence.Version > 0 &&
                e.Fence.State is WorkspaceTerminationFenceState.Frozen or WorkspaceTerminationFenceState.DestructionStarted or WorkspaceTerminationFenceState.Closed)) &&
            e.OrganizationScope.Revision >= 0 && e.OrganizationScope.Status is OrganizationScopeStatus.Open or OrganizationScopeStatus.Missing or OrganizationScopeStatus.Closed &&
            (e.Membership is null || (e.Membership.OrganizationId == organization &&
                e.Membership.OrganizationStatus is OrganizationStatus.Active or OrganizationStatus.Suspended or OrganizationStatus.Archived &&
                (m is null || (m.OrganizationId == organization && m.SubjectId == subject && m.MembershipId != Guid.Empty && m.Version > 0 &&
                    m.Role is OrganizationMembershipRole.Owner or OrganizationMembershipRole.Member &&
                    m.Status is OrganizationMembershipStatus.Active or OrganizationMembershipStatus.Suspended or OrganizationMembershipStatus.Removed)))) &&
            (!requireAuthority ? a is null : a is not null && a.ContractVersion == 2 && a.Status is AccessControlAuthoritySnapshotStatus.Stable or AccessControlAuthoritySnapshotStatus.Closed &&
            a.SubjectKind == AccessSubjectKind.User && a.SubjectId == subject && target.Equals(a.RootScope) &&
            a.ExpectedTargetScopes.Count == 1 && target.Equals(a.ExpectedTargetScopes[0]) && a.ManagementRevision is >= 0 &&
            a.ObservedAtUtc >= startedAt && a.ObservedAtUtc <= completedAt &&
            a.FactsSha256 is { Length: 64 } digest && digest.All(Uri.IsHexDigit));
    }

    private static bool Equivalent(External x, External y) => x.Workspace == y.Workspace && x.Fence == y.Fence &&
        x.OrganizationScope == y.OrganizationScope && x.Membership == y.Membership && AccessEquivalent(x.Authority, y.Authority);

    private static bool AccessEquivalent(AccessControlSubjectAuthoritySnapshot? x, AccessControlSubjectAuthoritySnapshot? y) =>
        x is null || y is null ? x == y : x.Status == y.Status && x.ContractVersion == y.ContractVersion && x.SubjectKind == y.SubjectKind && x.SubjectId == y.SubjectId &&
        Equals(x.RootScope, y.RootScope) && x.ExpectedTargetScopes.SequenceEqual(y.ExpectedTargetScopes) &&
        x.ManagementRevision == y.ManagementRevision && x.FactsSha256 == y.FactsSha256 && x.EarliestExpiryAtUtc == y.EarliestExpiryAtUtc &&
        x.RoleGrants.Count == y.RoleGrants.Count && x.RoleGrants.Zip(y.RoleGrants).All(pair =>
            pair.First.AssignmentId == pair.Second.AssignmentId && pair.First.RoleId == pair.Second.RoleId &&
            pair.First.RoleName == pair.Second.RoleName && pair.First.Scope.Equals(pair.Second.Scope) &&
            pair.First.ExpiresAtUtc == pair.Second.ExpiresAtUtc && pair.First.Permissions.SequenceEqual(pair.Second.Permissions)) &&
        x.ProfileGrants.Count == y.ProfileGrants.Count && x.ProfileGrants.Zip(y.ProfileGrants).All(pair =>
            pair.First.AssignmentId == pair.Second.AssignmentId && pair.First.AssignmentScope.Equals(pair.Second.AssignmentScope) &&
            pair.First.ProfileId == pair.Second.ProfileId && pair.First.OwnerScope.Equals(pair.Second.OwnerScope) &&
            pair.First.ProfileKey == pair.Second.ProfileKey && pair.First.ProfileStatus == pair.Second.ProfileStatus &&
            pair.First.ProfileVersion == pair.Second.ProfileVersion && pair.First.Permissions.SequenceEqual(pair.Second.Permissions));

    private sealed record Facts(StaffStationEligibilitySnapshot? Staff, PropertyStationEligibilitySnapshot? Property);
    private sealed record External(WorkspaceOperationalAdmissionDecision Workspace, WorkspaceTerminationFenceSnapshot? Fence,
        OrganizationScopeSnapshot OrganizationScope, OrganizationMembershipSnapshotDto? Membership,
        AccessControlSubjectAuthoritySnapshot? Authority);
}
