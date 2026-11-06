namespace Integration.Tests;

using System.Data.Common;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class StaffAndPropertyStationFactsPostgreSqlIntegrationTests
{
    private const string TenantA = "aa000000-0000-0000-0000-000000000001";
    private const string TenantB = "bb000000-0000-0000-0000-000000000002";
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Date = DateOnly.FromDateTime(Now.UtcDateTime);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Registered_owner_readers_use_real_single_queries_preserve_scope_and_observe_changes()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithName($"bunkfy-station-facts-{Guid.NewGuid():N}")
            .WithLabel("bunkfy.test.grant", "STAFF-PIN-ELIGIBILITY-01")
            .WithDatabase("bunkfy_station_facts")
            .Build();
        await postgres.StartAsync();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "PostgreSql",
            ["ConnectionStrings:PostgreSql"] = postgres.GetConnectionString()
        });
        builder.Services.AddScoped<TestScope>();
        builder.Services.AddScoped<IScopeContext>(services => services.GetRequiredService<TestScope>());
        builder.Services.AddSingleton<IWorkspaceTerminationFenceReader>(OpenWorkspaceTerminationFenceReader.Instance);
        builder.AddPropertiesPersistence();
        builder.AddStaffPersistence();
        var sql = new ReadOnlyQueryObserver();
        builder.Services.ConfigureDbContext<StaffDbContext>((_, options) => options.AddInterceptors(sql));
        builder.Services.ConfigureDbContext<PropertiesDbContext>((_, options) => options.AddInterceptors(sql));
        await using ServiceProvider services = builder.Services.BuildServiceProvider();
        using (var migration = Scope(services, TenantA))
        {
            await migration.ServiceProvider.GetRequiredService<PropertiesDbContext>().Database.MigrateAsync();
            await migration.ServiceProvider.GetRequiredService<StaffDbContext>().Database.MigrateAsync();
        }

        Guid propertyA = Guid.NewGuid(), propertyB = Guid.NewGuid();
        Guid memberA = Guid.NewGuid(), memberB = Guid.NewGuid();
        const string sharedSubject = "synthetic-subject-shared-across-tenants";
        await Seed(services, TenantA, propertyA, memberA, sharedSubject);
        await Seed(services, TenantB, propertyB, memberB, sharedSubject);

        using var read = Scope(services, TenantA);
        var staffReader = read.ServiceProvider.GetRequiredService<IStaffStationEligibilitySource>();
        var propertyReader = read.ServiceProvider.GetRequiredService<IPropertyStationEligibilitySource>();
        sql.Commands.Clear();
        var staff = Assert.IsType<StaffStationEligibilitySnapshot>(await staffReader.FindAsync(TenantA, propertyA, memberA));
        Assert.Single(sql.Commands);
        Assert.DoesNotContain("DisplayName", sql.Commands[0], StringComparison.Ordinal);
        Assert.DoesNotContain("WorkEmail", sql.Commands[0], StringComparison.Ordinal);
        Assert.Equal(StaffStationAuthLinkState.Linked, staff.AuthLinkState);
        Assert.Equal(sharedSubject, staff.AuthSubjectId);
        Assert.Equal(StaffStationAssignmentState.Open, staff.AssignmentState);
        Assert.Equal(propertyA, staff.OpenAssignment!.PropertyId);
        Assert.Equal(StaffProcessingRestrictionDecision.Allowed, staff.ProcessingRestriction.Decision);
        Assert.Empty(read.ServiceProvider.GetRequiredService<StaffDbContext>().ChangeTracker.Entries());
        sql.Commands.Clear();
        var property = Assert.IsType<PropertyStationEligibilitySnapshot>(await propertyReader.FindAsync(TenantA, propertyA));
        Assert.Single(sql.Commands);
        Assert.Contains("\"Name\"", sql.Commands[0], StringComparison.Ordinal);
        Assert.Equal("Synthetic station property", property.Name);
        Assert.Equal(PropertyStatus.Active, property.Status);
        Assert.Equal("Etc/UTC", property.TimeZoneId);
        Assert.Equal(PropertyProcessingStatus.Unconfigured, property.ConfiguredProcessingStatus);
        Assert.Empty(read.ServiceProvider.GetRequiredService<PropertiesDbContext>().ChangeTracker.Entries());
        Assert.Null(await staffReader.FindAsync(TenantB, propertyA, memberA));
        Assert.Null(await staffReader.FindAsync(TenantA, propertyA, memberB));
        Assert.Null(await propertyReader.FindAsync(TenantB, propertyA));
        Assert.Null(await propertyReader.FindAsync(TenantA, propertyB));
        Assert.Equal(StaffStationAssignmentState.None, (await staffReader.FindAsync(TenantA, propertyB, memberA))!.AssignmentState);
        using (var second = Scope(services, TenantB))
        {
            var secondStaff = second.ServiceProvider.GetRequiredService<IStaffStationEligibilitySource>();
            Assert.Equal(StaffStationAuthLinkState.Linked, (await secondStaff.FindAsync(TenantB, propertyB, memberB))!.AuthLinkState);
            Assert.Null(await secondStaff.FindAsync(TenantB, propertyA, memberA));
            Assert.Null(await second.ServiceProvider.GetRequiredService<IPropertyStationEligibilitySource>().FindAsync(TenantB, propertyA));
        }

        using (var update = Scope(services, TenantA))
        {
            var db = update.ServiceProvider.GetRequiredService<StaffDbContext>();
            var member = await db.StaffMembers.Include(item => item.Assignments).SingleAsync(item => item.Id == memberA);
            Assert.True(member.Suspend(member.Version, "user:synthetic", "fixture", Guid.NewGuid(), Now).IsSuccess);
            Assert.True(member.SetAuthSubject(null, member.Version, "user:synthetic", Guid.NewGuid(), Now).IsSuccess);
            Assert.True(member.UnassignProperty(propertyA, Date, member.Version, "user:synthetic", "fixture", Guid.NewGuid(), Now).IsSuccess);
            var restriction = await db.ProcessingRestrictionProjections.SingleAsync(item => item.StaffMemberId == memberA);
            Assert.True(restriction.Apply(restriction.Revision, StaffProcessingRestrictionContract.CurrentVersion, Now).IsSuccess);
            await db.SaveChangesAsync();
            var properties = update.ServiceProvider.GetRequiredService<PropertiesDbContext>();
            var current = await properties.Properties.SingleAsync(item => item.Id == propertyA);
            Assert.True(current.Retire(current.Version, Guid.NewGuid(), Now).IsSuccess);
            await properties.SaveChangesAsync();
        }
        var changed = (await staffReader.FindAsync(TenantA, propertyA, memberA))!;
        Assert.True(changed.Version > staff.Version);
        Assert.Equal(StaffStatus.Suspended, changed.Status);
        Assert.Equal(StaffStationAuthLinkState.Unlinked, changed.AuthLinkState);
        Assert.Equal(StaffStationAssignmentState.None, changed.AssignmentState);
        Assert.Equal(StaffProcessingRestrictionDecision.Restricted, changed.ProcessingRestriction.Decision);
        var retired = (await propertyReader.FindAsync(TenantA, propertyA))!;
        Assert.Equal(PropertyStatus.Retired, retired.Status);
        Assert.True(retired.Version > property.Version);

        using (var update = Scope(services, TenantA))
        {
            var db = update.ServiceProvider.GetRequiredService<StaffDbContext>();
            var restriction = await db.ProcessingRestrictionProjections.SingleAsync(item => item.StaffMemberId == memberA);
            db.Entry(restriction).Property(item => item.ContractVersion).CurrentValue = StaffProcessingRestrictionContract.CurrentVersion + 1;
            await db.SaveChangesAsync();
        }
        Assert.Equal(StaffProcessingRestrictionDecision.UnsupportedContractVersion,
            (await staffReader.FindAsync(TenantA, propertyA, memberA))!.ProcessingRestriction.Decision);
        using (var update = Scope(services, TenantA))
        {
            var db = update.ServiceProvider.GetRequiredService<StaffDbContext>();
            db.ProcessingRestrictionProjections.Remove(await db.ProcessingRestrictionProjections.SingleAsync(item => item.StaffMemberId == memberA));
            await db.SaveChangesAsync();
        }
        Assert.Equal(StaffProcessingRestrictionDecision.Unknown,
            (await staffReader.FindAsync(TenantA, propertyA, memberA))!.ProcessingRestriction.Decision);

        using (var future = Scope(services, TenantB))
        {
            var db = future.ServiceProvider.GetRequiredService<StaffDbContext>();
            var assignment = await db.StaffMembers.Where(item => item.Id == memberB)
                .SelectMany(item => item.Assignments).SingleAsync();
            // A persisted future date is still an open record, not effective-now eligibility.
            DateOnly futureDate = Date.AddDays(7);
            db.Entry(assignment).Property(item => item.EffectiveFrom).CurrentValue = futureDate;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var facts = (await future.ServiceProvider.GetRequiredService<IStaffStationEligibilitySource>()
                .FindAsync(TenantB, propertyB, memberB))!;
            Assert.Equal(StaffStationAssignmentState.Open, facts.AssignmentState);
            Assert.Equal(futureDate, facts.OpenAssignment!.EffectiveFrom);
            Assert.True(facts.OpenAssignment.EffectiveFrom > Date);
        }

        using (var duplicate = Scope(services, TenantB))
        {
            var db = duplicate.ServiceProvider.GetRequiredService<StaffDbContext>();
            db.StaffMembers.Add(Member(Guid.NewGuid(), TenantB, sharedSubject));
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
        }
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => staffReader.FindAsync(TenantA, propertyA, memberA, cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => propertyReader.FindAsync(TenantA, propertyA, cancel.Token));
    }

    private static async Task Seed(ServiceProvider services, string tenant, Guid propertyId, Guid memberId, string subject)
    {
        using var scope = Scope(services, tenant);
        var properties = scope.ServiceProvider.GetRequiredService<PropertiesDbContext>();
        var property = Property.Create(propertyId, tenant, "Synthetic station property", "station", "Etc/UTC", Guid.NewGuid(), Now).Value;
        await scope.ServiceProvider.GetRequiredService<IPropertyRepository>().AddAsync(property, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IPropertyTimeZoneRevisionWriter>().AppendAsync(
            new PropertyTimeZoneRevisionWriteModel(Guid.NewGuid(), tenant, propertyId, propertyId,
                PropertyTimeZoneChangeKind.Created, property.TimeZoneId.Value, null, property.TimeZoneId.Value,
                BunkFy.TimeZones.TimeZoneCatalog.Default.CatalogVersion, 0, property.Version,
                "user:synthetic", property.CreatedAtUtc), CancellationToken.None);
        await properties.SaveChangesAsync();
        var staff = scope.ServiceProvider.GetRequiredService<StaffDbContext>();
        var member = Member(memberId, tenant, subject);
        Assert.True(member.AssignProperty(Guid.NewGuid(), propertyId, null, false, Date, member.Version, "user:synthetic", Guid.NewGuid(), Now).IsSuccess);
        staff.StaffMembers.Add(member);
        staff.ProcessingRestrictionProjections.Add(StaffProcessingRestrictionProjection.Create(tenant, memberId, StaffProcessingRestrictionContract.CurrentVersion, Now).Value);
        await staff.SaveChangesAsync();
    }

    private static StaffMember Member(Guid id, string tenant, string subject) => StaffMember.Create(id, tenant,
        "Synthetic operator", null, null, null, null, null, null, subject, "user:synthetic", Guid.NewGuid(), Now).Value;
    private static IServiceScope Scope(ServiceProvider services, string tenant)
    {
        var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TestScope>().ScopeId = tenant;
        return scope;
    }
    private sealed class TestScope : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; set; } = TenantA;
    }
    private sealed class ReadOnlyQueryObserver : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            this.Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
