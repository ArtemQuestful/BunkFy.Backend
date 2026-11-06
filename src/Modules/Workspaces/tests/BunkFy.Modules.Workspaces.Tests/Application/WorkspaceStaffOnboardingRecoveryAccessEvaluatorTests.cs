namespace BunkFy.Modules.Workspaces.Tests.Application;

using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.AccessControl;
using Gma.Modules.AccessControl.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffOnboardingRecoveryAccessEvaluatorTests
{
    private const string WorkspaceId =
        "11111111-1111-1111-1111-111111111111";
    private const string SubjectId = "auth:manager";
    private static readonly Guid FirstPropertyId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SecondPropertyId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 25, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Exact_marker_and_current_profile_targets_are_equivalent()
    {
        AccessProfileDto profile = Profile();

        WorkspaceRecoveryVerification result =
            WorkspaceStaffOnboardingRecoveryAccessEvaluator.Evaluate(
                ExactSnapshot(profile),
                profile,
                SubjectId,
                WorkspaceId,
                [FirstPropertyId, SecondPropertyId]);

        Assert.Equal(WorkspaceRecoveryVerification.Equivalent, result);
    }

    [Fact]
    public void Stale_or_unavailable_snapshot_is_retryable()
    {
        AccessProfileDto profile = Profile();
        AccessControlSubjectAuthoritySnapshot exact = ExactSnapshot(profile);

        Assert.Equal(
            WorkspaceRecoveryVerification.RetryLater,
            Evaluate(exact with
            {
                Status = AccessControlAuthoritySnapshotStatus.Stale,
                FactsSha256 = null
            }, profile));
        Assert.Equal(
            WorkspaceRecoveryVerification.RetryLater,
            Evaluate(exact with
            {
                Status = AccessControlAuthoritySnapshotStatus.Unavailable,
                FactsSha256 = null
            }, profile));
    }

    [Fact]
    public void Extra_global_custom_or_drifted_authority_fails_closed()
    {
        AccessProfileDto profile = Profile();
        AccessControlSubjectAuthoritySnapshot exact = ExactSnapshot(profile);
        AccessControlAuthorityRoleGrant marker = Assert.Single(exact.RoleGrants);
        AccessControlAuthorityProfileGrant first = exact.ProfileGrants[0];
        AccessControlAuthorityRoleGrant extraGlobal = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "custom-global",
            ["staff.manage"],
            AccessScope.Global,
            ExpiresAtUtc: null);
        AccessControlAuthorityProfileGrant extraProfile = first with
        {
            AssignmentId = Guid.NewGuid(),
            AssignmentScope = WorkspaceAccessScopes.Create(WorkspaceId)
        };
        AccessControlSubjectAuthoritySnapshot[] conflicts =
        [
            exact with { SubjectId = "auth:other" },
            exact with
            {
                RootScope = WorkspaceAccessScopes.CreateProperty(
                    WorkspaceId,
                    FirstPropertyId)
            },
            exact with { RoleGrants = [marker, extraGlobal] },
            exact with
            {
                RoleGrants = [marker with { Permissions = ["staff.read"] }]
            },
            exact with
            {
                RoleGrants = [marker with { RoleName = "custom-role" }]
            },
            exact with
            {
                ProfileGrants =
                [first with { ProfileVersion = profile.Version + 1 },
                 exact.ProfileGrants[1]]
            },
            exact with
            {
                ProfileGrants =
                [first with { ProfileStatus = AccessProfileStatus.Archived },
                 exact.ProfileGrants[1]]
            },
            exact with { ProfileGrants = [first] },
            exact with
            {
                ProfileGrants = exact.ProfileGrants.Append(extraProfile).ToArray()
            }
        ];

        Assert.All(conflicts, conflict => Assert.Equal(
            WorkspaceRecoveryVerification.ReviewRequired,
            Evaluate(conflict, profile)));
    }

    private static WorkspaceRecoveryVerification Evaluate(
        AccessControlSubjectAuthoritySnapshot snapshot,
        AccessProfileDto profile) =>
        WorkspaceStaffOnboardingRecoveryAccessEvaluator.Evaluate(
            snapshot,
            profile,
            SubjectId,
            WorkspaceId,
            [FirstPropertyId, SecondPropertyId]);

    private static AccessControlSubjectAuthoritySnapshot ExactSnapshot(
        AccessProfileDto profile)
    {
        AccessScope workspace = WorkspaceAccessScopes.Create(WorkspaceId);
        string[] permissions = profile.Permissions
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new AccessControlSubjectAuthoritySnapshot(
            AccessControlAuthoritySnapshotStatus.Stable,
            ContractVersion: 2,
            AccessSubjectKind.User,
            SubjectId,
            workspace,
            [
                WorkspaceAccessScopes.CreateProperty(
                    WorkspaceId,
                    FirstPropertyId),
                WorkspaceAccessScopes.CreateProperty(
                    WorkspaceId,
                    SecondPropertyId)
            ],
            ManagementRevision: 7,
            Now,
            EarliestExpiryAtUtc: null,
            new string('a', 64),
            [
                new AccessControlAuthorityRoleGrant(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    WorkspaceAccessRoles.MembershipMarker,
                    [],
                    workspace,
                    ExpiresAtUtc: null)
            ],
            [
                Grant(profile, FirstPropertyId, permissions),
                Grant(profile, SecondPropertyId, permissions)
            ]);
    }

    private static AccessControlAuthorityProfileGrant Grant(
        AccessProfileDto profile,
        Guid propertyId,
        IReadOnlyList<string> permissions) => new(
        Guid.NewGuid(),
        WorkspaceAccessScopes.CreateProperty(WorkspaceId, propertyId),
        profile.Id,
        WorkspaceAccessScopes.Create(WorkspaceId),
        profile.Key,
        profile.Status,
        profile.Version,
        permissions);

    private static AccessProfileDto Profile() => new(
        Guid.Parse("44444444-4444-4444-4444-444444444444"),
        WorkspaceAccessScopes.Create(WorkspaceId).Value,
        "manager",
        "Manager",
        string.Empty,
        AccessProfileStatus.Active,
        Version: 3,
        ["staff.read", "properties.read"],
        AssignmentCount: 2,
        Now,
        Now);
}
