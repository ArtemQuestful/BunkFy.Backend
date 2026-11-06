namespace BunkFy.Modules.Stations.Application;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Stations.Domain;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.TimeZones;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Modules.Organizations.Contracts;
using NodaTime;

/// <summary>One current station/check-in prerequisite boundary, not a reusable permission or atomic cross-owner grant.</summary>
public sealed class StationAdmissionCoordinator(IScopeContext scope, IStaffStationEligibilitySource staff,
    IPropertyStationEligibilitySource properties, IWorkspaceStaffStationAdmissionObserver linked,
    IWorkspaceOperationalAdmissionPolicy workspace, IWorkspaceTerminationFenceReader fences,
    IOrganizationScopeLifecycle organizations, ISystemClock clock)
{
    /// <summary>Property registration facts only; no reservation/check-in permission or grant is observed.</summary>
    public async Task<(StationAdmission Admission, StationEnrollmentBinding? Enrollment)> ObserveRegistrationEnrollmentAsync(
        Guid propertyId, Guid staffId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!scope.IsEnabled || scope.ScopeId is not { } tenantId)
            { return (new(StationAdmissionState.Denied), null); }
            var before = await staff.FindAsync(tenantId, propertyId, staffId, cancellationToken).ConfigureAwait(false);
            StationEnrollmentBinding? binding = before?.AuthLinkState switch
            {
                StaffStationAuthLinkState.Linked when before.AuthSubjectId is not null => new(StationActorKind.LinkedStation, before.AuthSubjectId),
                StaffStationAuthLinkState.Unlinked when before.AuthSubjectId is null => new(StationActorKind.StationOnly, null),
                _ => null
            };
            if (binding is null)
            { return (new(StationAdmissionState.Denied), null); }
            StationAdmission result = await this.ObserveCoreAsync(propertyId, staffId, binding, null, cancellationToken).ConfigureAwait(false);
            if (scope.ScopeId != tenantId || before != await staff.FindAsync(tenantId, propertyId, staffId, cancellationToken).ConfigureAwait(false))
            { return (new(StationAdmissionState.StateChanged), null); }
            return (result, binding);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return (new(StationAdmissionState.Unavailable), null); }
    }

    public async Task<StationAdmission> ObservePropertyAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        if (!scope.IsEnabled || !Guid.TryParseExact(scope.ScopeId, "D", out Guid tenant) || tenant == Guid.Empty ||
            tenant.ToString("D") != scope.ScopeId || propertyId == Guid.Empty)
        { return new(StationAdmissionState.Denied); }
        string tenantId = scope.ScopeId;
        DateTimeOffset started = clock.UtcNow;
        try
        {
            var before = await properties.FindAsync(tenantId, propertyId, cancellationToken).ConfigureAwait(false);
            Common first = await Read().ConfigureAwait(false);
            Common last = await Read().ConfigureAwait(false);
            var after = await properties.FindAsync(tenantId, propertyId, cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = clock.UtcNow;
            if (now < started || !scope.IsEnabled || scope.ScopeId != tenantId || first != last || before != after ||
                LocalDate(before?.TimeZoneId, started) != LocalDate(after?.TimeZoneId, now))
            { return new(StationAdmissionState.StateChanged); }
            if (!ValidCommon(last))
            { return new(StationAdmissionState.Unavailable); }
            if (after is null || last.Organization.Status != OrganizationScopeStatus.Open || last.Fence is not null ||
                last.Workspace.Outcome != WorkspaceOperationalAdmissionOutcome.Allowed || after.Status != PropertyStatus.Active)
            { return new(StationAdmissionState.Denied); }
            DateOnly? date = LocalDate(after.TimeZoneId, now);
            if (after.ScopeId != tenantId || after.PropertyId != propertyId || after.Version < 1 || date is null ||
                after.ConfiguredProcessingStatus is not (PropertyProcessingStatus.Enabled or PropertyProcessingStatus.Unconfigured or PropertyProcessingStatus.Suspended))
            { return new(StationAdmissionState.Unavailable); }
            return new(StationAdmissionState.Current, date);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(StationAdmissionState.Unavailable); }
        async Task<Common> Read() => new(await organizations.GetSnapshotAsync(tenant, cancellationToken).ConfigureAwait(false),
            await workspace.EvaluateAsync(tenantId, cancellationToken).ConfigureAwait(false),
            await fences.GetCurrentAsync(cancellationToken).ConfigureAwait(false));
    }

    public Task<StationAdmission> ObserveAsync(Guid propertyId, Guid staffId, StationEnrollmentBinding binding,
        CancellationToken cancellationToken = default) => this.ObserveCoreAsync(propertyId, staffId, binding, WorkspaceStaffStationAction.ReservationCheckIn, cancellationToken);

    public Task<StationAdmission> ObserveIdentityAsync(Guid propertyId, Guid staffId, StationEnrollmentBinding binding,
        CancellationToken cancellationToken = default) => this.ObserveCoreAsync(propertyId, staffId, binding,
            WorkspaceStaffStationAction.StationIdentity, cancellationToken);

    public Task<StationAdmission> ObserveJobAsync(Guid propertyId, Guid staffId, StationEnrollmentBinding binding,
        WorkspaceStaffStationAction action, CancellationToken cancellationToken = default) =>
        action is WorkspaceStaffStationAction.ReservationCheckIn or WorkspaceStaffStationAction.ReservationCheckOut
            ? this.ObserveCoreAsync(propertyId, staffId, binding, action, cancellationToken)
            : Task.FromResult(new StationAdmission(StationAdmissionState.Denied));

    /// <summary>Own PIN enrollment facts only. Does not observe or grant reservation/check-in authority.</summary>
    public Task<StationAdmission> ObserveOwnPinEnrollmentAsync(Guid propertyId, Guid staffId, string ownSubject,
        CancellationToken cancellationToken = default) => this.ObserveCoreAsync(propertyId, staffId,
            new(StationActorKind.LinkedStation, ownSubject), null, cancellationToken);

    private async Task<StationAdmission> ObserveCoreAsync(Guid propertyId, Guid staffId, StationEnrollmentBinding binding,
        WorkspaceStaffStationAction? action, CancellationToken cancellationToken)
    {
        if (!scope.IsEnabled || !Guid.TryParseExact(scope.ScopeId, "D", out Guid tenant) || tenant == Guid.Empty ||
            tenant.ToString("D") != scope.ScopeId || propertyId == Guid.Empty || staffId == Guid.Empty || !binding.IsBound)
        {
            return new(StationAdmissionState.StateChanged);
        }
        string tenantId = scope.ScopeId;
        DateTimeOffset started = clock.UtcNow;
        try
        {
            Facts before = await ReadFacts().ConfigureAwait(false);
            Common beforeCommon = await ReadCommon().ConfigureAwait(false);
            DateOnly? date = LocalDate(before.Property?.TimeZoneId, started);
            StationAdmissionState state = Classify(before, tenantId, propertyId, staffId, binding, date);
            WorkspaceStaffStationObservation? observation = null;
            if (action is not null && state == StationAdmissionState.Current && binding.Kind == StationActorKind.LinkedStation)
            {
                observation = await linked.ObserveAsync(tenantId, propertyId, staffId,
                    action.Value, cancellationToken).ConfigureAwait(false);
                state = observation.Status switch
                {
                    WorkspaceStaffStationObservationStatus.LinkedAccountPrerequisitesObserved => StationAdmissionState.Current,
                    WorkspaceStaffStationObservationStatus.ObservationStale => StationAdmissionState.StateChanged,
                    WorkspaceStaffStationObservationStatus.PrerequisitesNotObserved => StationAdmissionState.Denied,
                    _ => StationAdmissionState.Unavailable
                };
            }
            Common afterCommon = await ReadCommon().ConfigureAwait(false);
            Facts after = await ReadFacts().ConfigureAwait(false);
            DateTimeOffset completed = clock.UtcNow;
            if (!scope.IsEnabled || scope.ScopeId != tenantId || completed < started || before != after || beforeCommon != afterCommon ||
                date != LocalDate(after.Property?.TimeZoneId, completed))
            {
                return new(StationAdmissionState.StateChanged);
            }
            if (!ValidCommon(afterCommon) || (after.Property is not null && date is null) ||
                (observation is not null && (observation.ObservedAtUtc < started || observation.ObservedAtUtc > completed ||
                    observation.PropertyLocalDate != date || observation.CanonicalTimeZoneId != after.Property?.TimeZoneId ||
                    observation.CatalogVersion != TimeZoneCatalog.Default.CatalogVersion || observation.TzdbVersion != TimeZoneCatalog.Default.TzdbVersion)))
            {
                return new(StationAdmissionState.Unavailable);
            }
            if (afterCommon.Organization.Status != OrganizationScopeStatus.Open || afterCommon.Fence is not null ||
                afterCommon.Workspace.Outcome != WorkspaceOperationalAdmissionOutcome.Allowed)
            {
                return new(StationAdmissionState.Denied, date);
            }
            return new(state, date);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(StationAdmissionState.Unavailable); }

        async Task<Facts> ReadFacts() => new(
            await staff.FindAsync(tenantId, propertyId, staffId, cancellationToken).ConfigureAwait(false),
            await properties.FindAsync(tenantId, propertyId, cancellationToken).ConfigureAwait(false));
        async Task<Common> ReadCommon() => new(
            await organizations.GetSnapshotAsync(tenant, cancellationToken).ConfigureAwait(false),
            await workspace.EvaluateAsync(tenantId, cancellationToken).ConfigureAwait(false),
            await fences.GetCurrentAsync(cancellationToken).ConfigureAwait(false));
    }

    private static StationAdmissionState Classify(Facts facts, string tenant, Guid property, Guid member,
        StationEnrollmentBinding binding, DateOnly? date)
    {
        if (facts.Staff is not { } s || facts.Property is not { } p)
        {
            return StationAdmissionState.Denied;
        }
        if (s.ScopeId != tenant || p.ScopeId != tenant || s.PropertyId != property || p.PropertyId != property || s.StaffMemberId != member ||
            s.Version < 1 || p.Version < 1 || date is null ||
            s.Status is not (StaffStatus.Active or StaffStatus.Suspended or StaffStatus.Departed) ||
            p.Status is not (PropertyStatus.Active or PropertyStatus.Retired) ||
            p.ConfiguredProcessingStatus is not (PropertyProcessingStatus.Unconfigured or PropertyProcessingStatus.Enabled or PropertyProcessingStatus.Suspended) ||
            s.AuthLinkState is not (StaffStationAuthLinkState.Linked or StaffStationAuthLinkState.Unlinked) ||
            s.AssignmentState is not (StaffStationAssignmentState.None or StaffStationAssignmentState.Open) ||
            s.ProcessingRestriction.ObservedContractVersion != StaffProcessingRestrictionContract.CurrentVersion ||
            s.ProcessingRestriction.ProjectionRevision is null or < 0 ||
            s.ProcessingRestriction.Decision is not (StaffProcessingRestrictionDecision.Allowed or StaffProcessingRestrictionDecision.Restricted))
        {
            return StationAdmissionState.Unavailable;
        }
        if ((binding.Kind == StationActorKind.LinkedStation && (s.AuthLinkState != StaffStationAuthLinkState.Linked ||
                !string.Equals(binding.AuthSubjectId, s.AuthSubjectId, StringComparison.Ordinal))) ||
            (binding.Kind == StationActorKind.StationOnly && (s.AuthLinkState != StaffStationAuthLinkState.Unlinked || s.AuthSubjectId is not null)))
        {
            return StationAdmissionState.StateChanged;
        }
        if (s.Status != StaffStatus.Active || p.Status != PropertyStatus.Active ||
            s.ProcessingRestriction.Decision != StaffProcessingRestrictionDecision.Allowed)
        {
            return StationAdmissionState.Denied;
        }
        if (s.AssignmentState == StaffStationAssignmentState.None)
        {
            return s.OpenAssignment is null ? StationAdmissionState.Denied : StationAdmissionState.Unavailable;
        }
        if (s.OpenAssignment is not { } a || a.AssignmentId == Guid.Empty || a.PropertyId != property ||
            a.AssignedAtVersion < 1 || a.AssignedAtVersion > s.Version || a.EffectiveFrom == default)
        {
            return StationAdmissionState.Unavailable;
        }
        return a.EffectiveFrom <= date ? StationAdmissionState.Current : StationAdmissionState.Denied;
    }
    private static bool ValidCommon(Common value) => value.Organization.Revision >= 0 &&
        value.Organization.Status is OrganizationScopeStatus.Open or OrganizationScopeStatus.Missing or OrganizationScopeStatus.Closed &&
        value.Workspace.Outcome is WorkspaceOperationalAdmissionOutcome.Allowed or WorkspaceOperationalAdmissionOutcome.Restricted &&
        (value.Fence is null || (value.Fence.ProcessId != Guid.Empty && value.Fence.TerminationEpoch != Guid.Empty && value.Fence.Version > 0 &&
            value.Fence.State is WorkspaceTerminationFenceState.Frozen or WorkspaceTerminationFenceState.DestructionStarted or WorkspaceTerminationFenceState.Closed));
    private static DateOnly? LocalDate(string? zoneId, DateTimeOffset now)
    {
        if (zoneId is null || !TimeZoneCatalog.Default.TryResolve(zoneId, out var resolution) ||
            resolution.Kind != TimeZoneCatalogResolutionKind.Canonical || resolution.CanonicalTimeZoneId != zoneId)
        {
            return null;
        }
        var local = Instant.FromDateTimeOffset(now).InZone(DateTimeZoneProviders.Tzdb[zoneId]).Date;
        return new(local.Year, local.Month, local.Day);
    }
    private sealed record Facts(StaffStationEligibilitySnapshot? Staff, PropertyStationEligibilitySnapshot? Property);
    private sealed record Common(OrganizationScopeSnapshot Organization, WorkspaceOperationalAdmissionDecision Workspace,
        WorkspaceTerminationFenceSnapshot? Fence);
}
