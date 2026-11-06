namespace Integration.Tests;

using BunkFy.Extensions.Workspaces;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.TimeZones;
using Gma.Framework.AccessControl;
using Gma.Framework.Runtime.Infrastructure;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Modules.AccessControl.Application;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.AccessControl.Persistence;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Auth.Domain.Aggregates;
using Gma.Modules.Auth.Domain.Enums;
using Gma.Modules.Auth.Domain.ValueObjects;
using Gma.Modules.Auth.Persistence;
using Gma.Modules.Organizations.Domain.Aggregates;
using Gma.Modules.Organizations.Domain.Enums;
using Gma.Modules.Organizations.Persistence;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;
using Status = BunkFy.Modules.Workspaces.Contracts.WorkspaceStaffStationObservationStatus;

public sealed class StaffStationAdmissionPostgreSqlIntegrationTests
{
    private const string TenantA = "aa000000-0000-0000-0000-000000000001";
    private const string TenantB = "bb000000-0000-0000-0000-000000000002";
    private const string AuthScope = "global";
    private const string Role = "station-check-in-fixture";
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Registered_observer_composes_current_owners_and_observes_revocation_expiry_and_isolation()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithName($"bunkfy-station-admission-{Guid.NewGuid():N}")
            .WithLabel("bunkfy.test.grant", "STAFF-PIN-ADMISSION-01")
            .WithDatabase("bunkfy_station_admission").Build();
        await postgres.StartAsync();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "PostgreSql",
            ["ConnectionStrings:PostgreSql"] = postgres.GetConnectionString()
        });
        var time = new TestClock();
        builder.Services.AddSingleton<ISystemClock>(time);
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.AddRuntimeInfrastructure();
        builder.Services.AddScoped<TestScope>();
        builder.Services.AddScoped<IScopeContext>(sp => sp.GetRequiredService<TestScope>());
        builder.Services.AddScoped<IScopeContextAccessor>(sp => sp.GetRequiredService<TestScope>());
        builder.AddPropertiesPersistence();
        builder.AddStaffPersistence();
        builder.AddWorkspacesPersistence();
        builder.AddOrganizationsPersistence();
        builder.AddAuthPersistence(AuthProfile.Global(AuthScope));
        builder.AddAccessControlPersistence();
        builder.Services.AddGmaAccessControl();
        builder.Services.AddAccessControlApplication(builder.Configuration);
        builder.Services.AddWorkspacesApplication(builder.Configuration, AuthScope);
        builder.Services.AddBunkFyWorkspaces(options => options.GlobalAuthScopeId = AuthScope);
        await using ServiceProvider services = builder.Services.BuildServiceProvider();
        using (var migration = Scope(services, TenantA))
        {
            var sp = migration.ServiceProvider;
            await sp.GetRequiredService<PropertiesDbContext>().Database.MigrateAsync();
            await sp.GetRequiredService<StaffDbContext>().Database.MigrateAsync();
            await sp.GetRequiredService<WorkspacesDbContext>().Database.MigrateAsync();
            await sp.GetRequiredService<OrganizationsDbContext>().Database.MigrateAsync();
            await sp.GetRequiredService<AuthDbContext>().Database.MigrateAsync();
            await sp.GetRequiredService<AccessControlDbContext>().Database.MigrateAsync();
        }
        Guid account = Guid.NewGuid(), propertyA = Guid.NewGuid(), propertyB = Guid.NewGuid();
        Guid staffA = Guid.NewGuid(), staffB = Guid.NewGuid();
        using (var seed = Scope(services, TenantA))
        {
            var auth = seed.ServiceProvider.GetRequiredService<AuthDbContext>();
            auth.Members.Add(Member.Create(new MemberId(account), AuthScope, "station-synthetic@example.test",
                MemberUsernameType.Email, "synthetic-non-authenticating-hash", new MemberUsernameId(Guid.NewGuid()),
                Guid.NewGuid(), Now).Value);
            await auth.SaveChangesAsync();
        }
        await Seed(services, TenantA, propertyA, staffA, account, OrganizationMembershipRole.Owner);
        await Seed(services, TenantB, propertyB, staffB, account, OrganizationMembershipRole.Member);

        using var readA = Scope(services, TenantA);
        using var readB = Scope(services, TenantB);
        var observerA = readA.ServiceProvider.GetRequiredService<IWorkspaceStaffStationAdmissionObserver>();
        var observerB = readB.ServiceProvider.GetRequiredService<IWorkspaceStaffStationAdmissionObserver>();
        Task<WorkspaceStaffStationObservation> ObserveA() => observerA.ObserveAsync(TenantA, propertyA, staffA, WorkspaceStaffStationAction.ReservationCheckIn);
        Task<WorkspaceStaffStationObservation> ObserveB() => observerB.ObserveAsync(TenantB, propertyB, staffB, WorkspaceStaffStationAction.ReservationCheckIn);
        var observed = await ObserveA();
        Assert.Equal(Status.LinkedAccountPrerequisitesObserved, observed.Status);
        Assert.Equal("America/New_York", observed.CanonicalTimeZoneId);
        Assert.Equal(new DateOnly(2026, 9, 20), observed.PropertyLocalDate);
        Assert.Equal(TimeZoneCatalog.Default.TzdbVersion, observed.TzdbVersion);
        Assert.Equal(Status.LinkedAccountPrerequisitesObserved, (await ObserveB()).Status);
        Assert.Equal(Status.PrerequisitesNotObserved,
            (await observerA.ObserveAsync(TenantB, propertyB, staffB, WorkspaceStaffStationAction.ReservationCheckIn)).Status);
        Assert.Equal(Status.PrerequisitesNotObserved,
            (await observerA.ObserveAsync(TenantA, propertyB, staffA, WorkspaceStaffStationAction.ReservationCheckIn)).Status);

        // The same scoped observer must query current persisted grants, not a positive cache.
        using (var change = Scope(services, TenantA))
        {
            var roles = change.ServiceProvider.GetRequiredService<IAccessControlRoleProvisioner>();
            Assert.Equal(AccessControlAssignmentRemovalOutcome.Removed,
                await roles.RemoveAssignmentAsync(AccessSubject.User(account.ToString("D")), Role,
                    WorkspaceAccessScopes.CreateProperty(TenantA, propertyA)));
        }
        Assert.Equal(Status.PrerequisitesNotObserved, (await ObserveA()).Status);
        Assert.Equal(Status.LinkedAccountPrerequisitesObserved, (await ObserveB()).Status);
        using (var change = Scope(services, TenantA))
        {
            await change.ServiceProvider.GetRequiredService<IAccessControlRoleProvisioner>().EnsureAssignmentAsync(
                AccessSubject.User(account.ToString("D")), Role, WorkspaceAccessScopes.CreateProperty(TenantA, propertyA));
            var access = change.ServiceProvider.GetRequiredService<AccessControlDbContext>();
            var assignment = await access.SubjectRoleAssignments.SingleAsync(row =>
                row.SubjectId == account.ToString("D") && row.ScopeValue == WorkspaceAccessScopes.CreateProperty(TenantA, propertyA).Value &&
                row.RevokedAtUnixMilliseconds == null);
            access.Entry(assignment).Property(row => row.ExpiresAtUnixMilliseconds).CurrentValue = Now.AddMinutes(1).ToUnixTimeMilliseconds();
            await access.SaveChangesAsync();
        }
        Assert.Equal(Status.LinkedAccountPrerequisitesObserved, (await ObserveA()).Status);
        time.UtcNow = Now.AddMinutes(1);
        Assert.Equal(Status.PrerequisitesNotObserved, (await ObserveA()).Status);
        Assert.Equal(Status.LinkedAccountPrerequisitesObserved, (await ObserveB()).Status);

        using (var change = Scope(services, TenantB))
        {
            var db = change.ServiceProvider.GetRequiredService<OrganizationsDbContext>();
            var member = await db.Memberships.SingleAsync(item => item.OrganizationId == Guid.Parse(TenantB));
            Assert.True(member.Suspend(member.Version, "user:synthetic", Guid.NewGuid(), time.UtcNow).IsSuccess);
            await db.SaveChangesAsync();
        }
        Assert.Equal(Status.PrerequisitesNotObserved, (await ObserveB()).Status);
        using (var change = Scope(services, TenantB))
        {
            var db = change.ServiceProvider.GetRequiredService<StaffDbContext>();
            var member = await db.StaffMembers.SingleAsync(item => item.Id == staffB);
            Assert.True(member.Suspend(member.Version, "user:synthetic", "fixture", Guid.NewGuid(), time.UtcNow).IsSuccess);
            Assert.True(member.SetAuthSubject(null, member.Version, "user:synthetic", Guid.NewGuid(), time.UtcNow).IsSuccess);
            await db.SaveChangesAsync();
        }
        Assert.Equal(Status.PrerequisitesNotObserved, (await ObserveB()).Status);
        using (var change = Scope(services, TenantA))
        {
            var db = change.ServiceProvider.GetRequiredService<AuthDbContext>();
            var member = await db.Members.SingleAsync(item => item.Id == new MemberId(account));
            Assert.True(member.Disable("synthetic fixture", Guid.NewGuid(), time.UtcNow).IsSuccess);
            await db.SaveChangesAsync();
        }
        Assert.Equal(WorkspaceStaffStationObservationReason.AuthAccountNotActive, (await ObserveA()).Reason);
    }

    private static async Task Seed(ServiceProvider services, string tenant, Guid propertyId, Guid staffId,
        Guid account, OrganizationMembershipRole role)
    {
        using var scope = Scope(services, tenant);
        var sp = scope.ServiceProvider;
        var organizations = sp.GetRequiredService<OrganizationsDbContext>();
        var organization = Organization.Create(Guid.Parse(tenant), "Synthetic station organization", tenant,
            "user:synthetic", Guid.NewGuid(), Now).Value;
        organizations.Organizations.Add(organization);
        organizations.Memberships.Add(OrganizationMembership.Create(Guid.NewGuid(), organization.Id, account.ToString("D"),
            role, "user:synthetic", Guid.NewGuid(), Now).Value);
        await organizations.SaveChangesAsync();
        var property = Property.Create(propertyId, tenant, "Synthetic station property", "station",
            "America/New_York", Guid.NewGuid(), Now).Value;
        await sp.GetRequiredService<IPropertyRepository>().AddAsync(property, CancellationToken.None);
        await sp.GetRequiredService<IPropertyTimeZoneRevisionWriter>().AppendAsync(
            new PropertyTimeZoneRevisionWriteModel(Guid.NewGuid(), tenant, propertyId, propertyId,
                PropertyTimeZoneChangeKind.Created, property.TimeZoneId.Value, null, property.TimeZoneId.Value,
                TimeZoneCatalog.Default.CatalogVersion, 0, property.Version, "user:synthetic", Now), CancellationToken.None);
        await sp.GetRequiredService<PropertiesDbContext>().SaveChangesAsync();
        var staff = StaffMember.Create(staffId, tenant, "Synthetic operator", null, null, null, null, null, null,
            account.ToString("D"), "user:synthetic", Guid.NewGuid(), Now).Value;
        Assert.True(staff.AssignProperty(Guid.NewGuid(), propertyId, null, false, new DateOnly(2026, 9, 20),
            staff.Version, "user:synthetic", Guid.NewGuid(), Now).IsSuccess);
        await sp.GetRequiredService<IStaffMemberRepository>().AddAsync(staff, CancellationToken.None);
        await sp.GetRequiredService<StaffDbContext>().SaveChangesAsync();
        var roles = sp.GetRequiredService<IAccessControlRoleProvisioner>();
        await roles.EnsureRoleAsync(new(Role, [ReservationsAdminPermissionCodes.CheckIn]));
        await roles.EnsureAssignmentAsync(AccessSubject.User(account.ToString("D")), Role,
            WorkspaceAccessScopes.CreateProperty(tenant, propertyId));
    }

    private static IServiceScope Scope(ServiceProvider services, string tenant)
    {
        var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IScopeContextAccessor>().SetScope(tenant);
        return scope;
    }
    private sealed class TestScope : IScopeContextAccessor
    {
        public bool IsEnabled { get; private set; }
        public string? ScopeId { get; private set; }
        public void SetScope(string scopeId) { this.ScopeId = scopeId; this.IsEnabled = true; }
        public void ClearScope() { this.ScopeId = null; this.IsEnabled = false; }
    }
    private sealed class TestClock : TimeProvider, ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => this.UtcNow;
    }
}
