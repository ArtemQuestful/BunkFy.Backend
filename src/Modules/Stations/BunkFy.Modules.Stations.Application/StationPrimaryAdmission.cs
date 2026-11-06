namespace BunkFy.Modules.Stations.Application;

using System.Globalization;
using System.Security.Claims;
using BunkFy.Modules.Stations.Contracts;
using BunkFy.Modules.Stations.Domain;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.Permissions;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Framework.Security;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.Options;

/// <summary>Host must supply its actual primary authentication result. This does not authenticate a transport.</summary>
public sealed class StationPrimaryAdmission(IScopeContext scope, IAuthMemberAdmissionReader accounts, IAuthSessionAdmissionReader sessions,
    IOrganizationMembershipReader memberships, IOrganizationScopeLifecycle organizations,
    IWorkspaceOperationalAdmissionPolicy workspace, IWorkspaceTerminationFenceReader fences,
    IAccessAuthorizationService authorization, IAccessControlSubjectAuthoritySnapshotReader authority,
    IOptions<StationOptions> options, StationPrimaryAssurance assurance, ISystemClock clock,
    IStationWorkspaceSettingsReader? workspaceSettings = null)
{
    public Task<StationPrimaryObservation> ManagerAsync(ClaimsPrincipal principal, Guid? propertyId, bool mutation,
        CancellationToken cancellationToken = default) => this.ObservePrincipal(principal, propertyId, null, mutation, cancellationToken);

    public Task<StationPrimaryObservation> SelfAsync(ClaimsPrincipal principal, string expectedSubject,
        Guid propertyId, CancellationToken cancellationToken = default) =>
        this.ObservePrincipal(principal, propertyId, expectedSubject, true, cancellationToken);

    public Task<StationPrimaryObservation> OwnOutcomeAsync(ClaimsPrincipal principal, Guid propertyId,
        CancellationToken cancellationToken = default) =>
        TrySubject(principal, out string? subject) ? this.ObservePrincipal(principal, propertyId, subject, false, cancellationToken)
            : Task.FromResult(new StationPrimaryObservation(StationAdmissionState.Denied));

    public async Task<StationPrimaryObservation> RevalidateIssuerAsync(StationSetupFacts setup, Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        if (setup.IssuerKind is not (StationIssuerKind.Manager or StationIssuerKind.Self) ||
            setup.IssuerSubjectId is not { } subject || setup.AssuranceExpiresAtUtc is not { } expires ||
            clock.UtcNow >= expires || (setup.IssuerKind == StationIssuerKind.Self &&
                (setup.Enrollment.Kind != StationActorKind.LinkedStation || setup.Enrollment.AuthSubjectId != subject)))
        { return new(StationAdmissionState.StateChanged); }
        // Deliberate primary sign-out is not account suspension or permission revocation.
        return await this.ObserveOwner(subject, setup.IssuerKind == StationIssuerKind.Manager ? null : propertyId,
            setup.IssuerKind, expires, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StationPrimaryObservation> ObservePrincipal(ClaimsPrincipal principal, Guid? propertyId,
        string? ownSubject, bool mutation, CancellationToken ct)
    {
        if (!TrySubject(principal, out string? subject) || (ownSubject is not null && subject != ownSubject) ||
            !TrySession(principal, out Guid sessionId) ||
            principal.FindAll(ApplicationClaimNames.AuthenticationContextReference).Count() > 1 ||
            principal.FindAll(ApplicationClaimNames.AuthenticationTime).Count() > 1)
        { return new(StationAdmissionState.Denied); }
        DateTimeOffset? expires = null;
        if (mutation)
        {
            if (principal.FindAll(ApplicationClaimNames.AuthenticationContextReference).Count() != 1 ||
                principal.FindAll(ApplicationClaimNames.AuthenticationTime).Count() != 1 ||
                !AuthenticationAssuranceEvaluator.IsSatisfied(principal, assurance.Requirement, clock.UtcNow))
            { return new(StationAdmissionState.Denied); }
            long time = long.Parse(principal.FindFirst(ApplicationClaimNames.AuthenticationTime)!.Value, CultureInfo.InvariantCulture);
            expires = DateTimeOffset.FromUnixTimeSeconds(time) + assurance.Requirement.MaxAuthenticationAge!.Value;
        }
        if (string.IsNullOrWhiteSpace(options.Value.ManagementAuthScopeId))
        { return new(StationAdmissionState.Unavailable); }
        try
        {
            if (!await sessions.IsActiveAsync(options.Value.ManagementAuthScopeId, Guid.Parse(subject!), sessionId, ct).ConfigureAwait(false))
            { return new(StationAdmissionState.Denied); }
            StationPrimaryObservation observed = await this.ObserveOwner(subject!, propertyId,
                ownSubject is null ? StationIssuerKind.Manager : StationIssuerKind.Self, expires, ct).ConfigureAwait(false);
            if (!await sessions.IsActiveAsync(options.Value.ManagementAuthScopeId, Guid.Parse(subject!), sessionId, ct).ConfigureAwait(false))
            { return new(StationAdmissionState.StateChanged); }
            return observed.Issuer is { } issuer ? observed with { Issuer = issuer with { SessionId = sessionId } } : observed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new(StationAdmissionState.Unavailable); }
    }

    private async Task<StationPrimaryObservation> ObserveOwner(string subject, Guid? propertyId, StationIssuerKind kind,
        DateTimeOffset? expires, CancellationToken ct)
    {
        DateTimeOffset started = clock.UtcNow;
        if (!scope.IsEnabled || !Guid.TryParseExact(scope.ScopeId, "D", out Guid tenant) || tenant == Guid.Empty ||
            scope.ScopeId != tenant.ToString("D") || !Guid.TryParseExact(subject, "D", out Guid account) || account == Guid.Empty ||
            subject != account.ToString("D") || propertyId == Guid.Empty || string.IsNullOrWhiteSpace(options.Value.ManagementAuthScopeId))
        { return new(StationAdmissionState.Denied); }
        string tenantId = scope.ScopeId;
        AccessScope target = propertyId is { } p ? WorkspaceAccessScopes.CreateProperty(tenantId, p) : WorkspaceAccessScopes.Create(tenantId);
        try
        {
            bool accountBefore = await Account().ConfigureAwait(false);
            Facts before = await Read().ConfigureAwait(false);
            AccessDecision? decisionBefore = kind == StationIssuerKind.Manager ? await Decide().ConfigureAwait(false) : null;
            AccessDecision? decisionAfter = kind == StationIssuerKind.Manager ? await Decide().ConfigureAwait(false) : null;
            Facts after = await Read().ConfigureAwait(false);
            bool accountAfter = await Account().ConfigureAwait(false);
            DateTimeOffset completed = clock.UtcNow;
            if (!scope.IsEnabled || scope.ScopeId != tenantId || completed < started || accountBefore != accountAfter ||
                decisionBefore != decisionAfter || !Equivalent(before, after) ||
                before.Authority.Status == AccessControlAuthoritySnapshotStatus.Stale || after.Authority.Status == AccessControlAuthoritySnapshotStatus.Stale ||
                (before.Authority.EarliestExpiryAtUtc is { } expiry && expiry > started && expiry <= completed))
            { return new(StationAdmissionState.StateChanged); }
            if (!Valid(before) || !Valid(after))
            { return new(StationAdmissionState.Unavailable); }
            if (!before.Settings.SharedStationPinEnabled || !after.Settings.SharedStationPinEnabled)
            { return new(StationAdmissionState.Denied); }
            if (!accountAfter || after.Organization.Status != OrganizationScopeStatus.Open || after.Fence is not null ||
                after.Workspace.Outcome != WorkspaceOperationalAdmissionOutcome.Allowed ||
                after.Membership is not
                {
                    OrganizationStatus: OrganizationStatus.Active,
                    Membership: { Status: OrganizationMembershipStatus.Active, Role: OrganizationMembershipRole.Owner or OrganizationMembershipRole.Member }
                } ||
                after.Authority.Status == AccessControlAuthoritySnapshotStatus.Closed ||
                (kind == StationIssuerKind.Manager && (decisionAfter?.IsAllowed != true || after.Authority.EarliestExpiryAtUtc <= completed)))
            { return new(StationAdmissionState.Denied); }
            return new(StationAdmissionState.Current, new(subject, kind, expires));

            bool Valid(Facts f) => f.Organization.Revision >= 0 && f.Settings.Revision >= 0 &&
                f.Organization.Status is OrganizationScopeStatus.Open or OrganizationScopeStatus.Closed or OrganizationScopeStatus.Missing &&
                f.Workspace.Outcome is WorkspaceOperationalAdmissionOutcome.Allowed or WorkspaceOperationalAdmissionOutcome.Restricted &&
                (f.Fence is null || (f.Fence.Version > 0 && f.Fence.ProcessId != Guid.Empty && f.Fence.TerminationEpoch != Guid.Empty)) &&
                (f.Membership is null || (f.Membership.OrganizationId == tenant &&
                    (f.Membership.Membership is null || (f.Membership.Membership.OrganizationId == tenant && f.Membership.Membership.SubjectId == subject && f.Membership.Membership.Version > 0)))) &&
                f.Authority.ContractVersion == 2 && f.Authority.SubjectKind == AccessSubjectKind.User && f.Authority.SubjectId == subject &&
                f.Authority.Status is AccessControlAuthoritySnapshotStatus.Stable or AccessControlAuthoritySnapshotStatus.Closed &&
                target.Equals(f.Authority.RootScope) && f.Authority.ExpectedTargetScopes.Count == 1 && target.Equals(f.Authority.ExpectedTargetScopes[0]) &&
                f.Authority.ManagementRevision >= 0 && f.Authority.ObservedAtUtc >= started && f.Authority.ObservedAtUtc <= completed &&
                f.Authority.FactsSha256 is { Length: 64 } digest && digest.All(Uri.IsHexDigit);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new(StationAdmissionState.Unavailable); }

        async Task<bool> Account() => await accounts.FindActiveAsync(options.Value.ManagementAuthScopeId, account, ct).ConfigureAwait(false) is not null;
        async Task<AccessDecision> Decide() => await authorization.AuthorizeAsync(new(AccessSubject.User(subject),
            PermissionCode.Create(StationsPermissionCodes.Manage), target), ct).ConfigureAwait(false);
        async Task<Facts> Read() => new(await organizations.GetSnapshotAsync(tenant, ct).ConfigureAwait(false),
            await workspace.EvaluateAsync(tenantId, ct).ConfigureAwait(false), await fences.GetCurrentAsync(ct).ConfigureAwait(false),
            await memberships.FindAsync(tenant, subject, ct).ConfigureAwait(false),
            await authority.ReadAsync(new(AccessSubjectKind.User, subject, target, [target]), ct).ConfigureAwait(false),
            workspaceSettings is null
                ? new(true, 0)
                : await workspaceSettings.ReadWorkspaceSettingsAsync(ct).ConfigureAwait(false));
    }

    private static bool TrySubject(ClaimsPrincipal principal, out string? subject)
    {
        subject = null;
        if (principal.Identities.Count() != 1 || principal.Identity?.IsAuthenticated != true)
        { return false; }
        string[] values = principal.FindAll("sub").Select(x => x.Value).ToArray();
        string[] names = principal.FindAll(ClaimTypes.NameIdentifier).Select(x => x.Value).ToArray();
        if (values.Length != 1 || names.Length > 1 || (names.Length == 1 && names[0] != values[0]))
        { return false; }
        subject = values[0];
        return Guid.TryParseExact(subject, "D", out Guid id) && id != Guid.Empty && subject == id.ToString("D");
    }
    private static bool Equivalent(Facts a, Facts b) => a.Organization == b.Organization && a.Workspace == b.Workspace &&
        a.Fence == b.Fence && a.Membership == b.Membership && a.Authority.Status == b.Authority.Status &&
        a.Authority.ManagementRevision == b.Authority.ManagementRevision && a.Authority.FactsSha256 == b.Authority.FactsSha256 &&
        a.Authority.EarliestExpiryAtUtc == b.Authority.EarliestExpiryAtUtc && a.Settings == b.Settings;
    private static bool TrySession(ClaimsPrincipal principal, out Guid sessionId)
    {
        sessionId = Guid.Empty;
        string[] values = principal.FindAll(ApplicationClaimNames.SessionId).Select(x => x.Value).ToArray();
        return values.Length == 1 && Guid.TryParseExact(values[0], "D", out sessionId) &&
            sessionId != Guid.Empty && values[0] == sessionId.ToString("D");
    }
    private sealed record Facts(OrganizationScopeSnapshot Organization, WorkspaceOperationalAdmissionDecision Workspace,
        WorkspaceTerminationFenceSnapshot? Fence, OrganizationMembershipSnapshotDto? Membership,
        AccessControlSubjectAuthoritySnapshot Authority, StationWorkspaceSettings Settings);
}

public sealed class StationPrimaryAssurance
{
    public StationPrimaryAssurance(AuthenticationAssuranceRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (requirement.MaxAuthenticationAge is not { } age || age <= TimeSpan.Zero || age > TimeSpan.FromHours(1) ||
            requirement.AcceptedContextReferences.Count != 2 ||
            !requirement.AcceptedContextReferences.Contains("urn:gma:acr:mfa") || !requirement.AcceptedContextReferences.Contains("urn:gma:acr:two-step"))
        { throw new ArgumentException("Exact destructive-operation assurance is required.", nameof(requirement)); }
        this.Requirement = requirement;
    }
    public AuthenticationAssuranceRequirement Requirement { get; }
}
