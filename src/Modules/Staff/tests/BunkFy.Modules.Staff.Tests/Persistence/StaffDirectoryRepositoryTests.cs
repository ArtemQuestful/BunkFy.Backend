namespace BunkFy.Modules.Staff.Tests.Persistence;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Staff.Persistence.Repositories;
using Gma.Framework.Pagination;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffDirectoryRepositoryTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    [Fact]
    public async Task Account_directory_resolves_only_requested_operational_members_in_the_current_tenant()
    {
        InMemoryDatabaseRoot root = new();
        string databaseName = $"staff-account-directory-{Guid.NewGuid():N}";
        StaffMember tenantMember = CreateMember("Ada Operator", "ada");
        StaffMember otherTenantMember = CreateMember(
            "Foreign Operator",
            "foreign",
            TenantB);

        await using (StaffDbContext tenantA = CreateDbContext(
            root,
            databaseName,
            TenantA))
        {
            tenantA.StaffMembers.Add(tenantMember);
            tenantA.ProcessingRestrictionProjections.Add(
                CreateRestrictionProjection(tenantMember));
            await tenantA.SaveChangesAsync();
        }

        await using (StaffDbContext tenantB = CreateDbContext(
            root,
            databaseName,
            TenantB))
        {
            tenantB.StaffMembers.Add(otherTenantMember);
            tenantB.ProcessingRestrictionProjections.Add(
                CreateRestrictionProjection(otherTenantMember));
            await tenantB.SaveChangesAsync();
        }

        await using StaffDbContext queryContext = CreateDbContext(
            root,
            databaseName,
            TenantA);
        StaffMemberRepository repository = new(queryContext);

        StaffAccountDirectoryResponse response =
            await repository.ResolveAccountDirectoryAsync(
                [
                    tenantMember.AuthSubjectId!,
                    otherTenantMember.AuthSubjectId!,
                    "missing-subject"
                ],
                CancellationToken.None);

        StaffAccountDirectoryEntryDto entry = Assert.Single(response.Items);
        Assert.Equal(tenantMember.Id, entry.StaffMemberId);
        Assert.Equal(tenantMember.AuthSubjectId, entry.AuthSubjectId);
        Assert.Equal("Ada Operator", entry.DisplayName);
        Assert.Equal("Manager", entry.JobTitle);
        Assert.Equal(StaffStatus.Active, entry.Status);
        Assert.DoesNotContain(
            typeof(StaffAccountDirectoryEntryDto).GetProperties(),
            property => property.Name is "LegalName" or "WorkEmail" or
                "WorkPhone" or "EmployeeNumber" or "Department" or
                "Assignments");
    }

    [Fact]
    public async Task Directory_projection_is_active_property_scoped_and_searches_display_name_only()
    {
        await using StaffDbContext dbContext = CreateDbContext();
        Guid firstPropertyId = Guid.NewGuid();
        Guid secondPropertyId = Guid.NewGuid();
        Guid retiredPropertyId = Guid.NewGuid();
        StaffMember member = CreateMember();
        Assign(member, firstPropertyId, isPrimary: true);
        Assign(member, secondPropertyId, isPrimary: false);
        Assign(member, retiredPropertyId, isPrimary: false);
        dbContext.StaffMembers.Add(member);
        dbContext.ProcessingRestrictionProjections.Add(
            CreateRestrictionProjection(member));
        dbContext.PropertyProjections.AddRange(
            new StaffPropertyProjection("tenant-a", firstPropertyId, "First", PropertyStatus.Active, 1),
            new StaffPropertyProjection("tenant-a", secondPropertyId, "Second", PropertyStatus.Active, 1),
            new StaffPropertyProjection("tenant-a", retiredPropertyId, string.Empty, PropertyStatus.Retired, 2));
        await dbContext.SaveChangesAsync();
        StaffMemberRepository repository = new(dbContext);

        StaffDirectoryMemberDto tenantDirectory = (await repository.GetDirectoryAsync(
            member.Id,
            CancellationToken.None))!;
        StaffDirectoryMemberDto propertyDirectory = (await repository.GetDirectoryAtPropertyAsync(
            firstPropertyId,
            member.Id,
            CancellationToken.None))!;
        StaffDirectoryMemberDto? retiredPropertyDirectory = await repository.GetDirectoryAtPropertyAsync(
            retiredPropertyId,
            member.Id,
            CancellationToken.None);
        StaffDirectoryListResponse displayNameSearch = await repository.ListDirectoryAsync(
            "Ada",
            status: null,
            new PageRequest(PageRequest.DefaultPage, PageRequest.DefaultPageSize),
            CancellationToken.None);
        StaffDirectoryListResponse emailSearch = await repository.ListDirectoryAsync(
            "private@example.test",
            status: null,
            new PageRequest(PageRequest.DefaultPage, PageRequest.DefaultPageSize),
            CancellationToken.None);
        StaffPropertyDirectoryListResponse propertyList = await repository.ListDirectoryAtPropertyAsync(
            firstPropertyId,
            search: null,
            status: null,
            new PageRequest(PageRequest.DefaultPage, PageRequest.DefaultPageSize),
            CancellationToken.None);
        StaffPropertyDirectoryListResponse retiredPropertyList = await repository.ListDirectoryAtPropertyAsync(
            retiredPropertyId,
            search: null,
            status: null,
            new PageRequest(PageRequest.DefaultPage, PageRequest.DefaultPageSize),
            CancellationToken.None);

        Assert.Equal(3, member.Assignments.Count);
        Assert.Equal(2, tenantDirectory.Assignments.Count);
        Assert.DoesNotContain(
            tenantDirectory.Assignments,
            assignment => assignment.PropertyId == retiredPropertyId);
        Assert.Equal(firstPropertyId, Assert.Single(propertyDirectory.Assignments).PropertyId);
        Assert.Null(retiredPropertyDirectory);
        StaffDirectoryListItemDto listItem = Assert.Single(displayNameSearch.Items);
        Assert.Equal(2, listItem.CurrentPropertyCount);
        Assert.Equal(firstPropertyId, Assert.Single(propertyList.Items).Assignment.PropertyId);
        Assert.Empty(retiredPropertyList.Items);
        Assert.False(displayNameSearch.HasMore);
        Assert.False(propertyList.HasMore);
        Assert.Empty(emailSearch.Items);
        Assert.DoesNotContain(
            typeof(StaffDirectoryMemberDto).GetProperties(),
            property => property.Name is "LegalName" or "WorkEmail" or "WorkPhone" or
                "EmployeeNumber" or "AuthSubjectId" or "CreatedBy" or "LastChangedBy");
        Assert.DoesNotContain(
            typeof(StaffDirectoryListItemDto).GetProperties(),
            property => property.Name is "LegalName" or "WorkEmail" or "WorkPhone" or
                "EmployeeNumber" or "AuthSubjectId" or "Assignments");
    }

    [Fact]
    public async Task Foreign_property_authority_does_not_expose_local_assignments()
    {
        InMemoryDatabaseRoot root = new();
        string databaseName = $"staff-directory-scope-{Guid.NewGuid():N}";
        Guid propertyId = Guid.NewGuid();

        await using StaffDbContext tenantA = CreateDbContext(
            root,
            databaseName,
            TenantA);
        StaffMember member = CreateMember();
        Assign(member, propertyId, isPrimary: true);
        tenantA.StaffMembers.Add(member);
        tenantA.ProcessingRestrictionProjections.Add(
            CreateRestrictionProjection(member));
        tenantA.PropertyProjections.Add(new StaffPropertyProjection(
            TenantA,
            propertyId,
            string.Empty,
            PropertyStatus.Retired,
            1));
        await tenantA.SaveChangesAsync();

        await using (StaffDbContext tenantB = CreateDbContext(
            root,
            databaseName,
            TenantB))
        {
            tenantB.PropertyProjections.Add(new StaffPropertyProjection(
                TenantB,
                propertyId,
                "Foreign active property",
                PropertyStatus.Active,
                1));
            await tenantB.SaveChangesAsync();
        }

        tenantA.ChangeTracker.Clear();
        StaffMemberRepository repository = new(tenantA);
        StaffDirectoryMemberDto directory = (await repository.GetDirectoryAsync(
            member.Id,
            CancellationToken.None))!;
        StaffDirectoryMemberDto? propertyDirectory =
            await repository.GetDirectoryAtPropertyAsync(
                propertyId,
                member.Id,
                CancellationToken.None);
        StaffPropertyDirectoryListResponse propertyList =
            await repository.ListDirectoryAtPropertyAsync(
                propertyId,
                search: null,
                status: null,
                new PageRequest(
                    PageRequest.DefaultPage,
                    PageRequest.DefaultPageSize),
                CancellationToken.None);

        Assert.Empty(directory.Assignments);
        Assert.Null(propertyDirectory);
        Assert.Empty(propertyList.Items);
    }

    [Fact]
    public async Task Directory_pagination_uses_one_row_look_ahead()
    {
        await using StaffDbContext dbContext = CreateDbContext();
        StaffMember first = CreateMember("Ada Operator", "ada");
        StaffMember second = CreateMember("Grace Manager", "grace");
        dbContext.StaffMembers.AddRange(first, second);
        dbContext.ProcessingRestrictionProjections.AddRange(
            CreateRestrictionProjection(first),
            CreateRestrictionProjection(second));
        await dbContext.SaveChangesAsync();
        StaffMemberRepository repository = new(dbContext);

        StaffDirectoryListResponse firstPage = await repository.ListDirectoryAsync(
            search: null,
            status: null,
            new PageRequest(1, 1),
            CancellationToken.None);
        StaffDirectoryListResponse secondPage = await repository.ListDirectoryAsync(
            search: null,
            status: null,
            new PageRequest(2, 1),
            CancellationToken.None);

        Assert.Equal(first.Id, Assert.Single(firstPage.Items).StaffMemberId);
        Assert.True(firstPage.HasMore);
        Assert.Equal(second.Id, Assert.Single(secondPage.Items).StaffMemberId);
        Assert.False(secondPage.HasMore);
    }

    [Fact]
    public async Task Anonymised_member_is_hidden_from_all_operational_reads()
    {
        await using StaffDbContext dbContext = CreateDbContext();
        StaffMember member = CreateMember();
        string previousAuthSubject = member.AuthSubjectId!;
        DateTimeOffset departedAtUtc =
            new(2018, 1, 1, 9, 0, 0, TimeSpan.Zero);
        Assert.True(member.Depart(
            DateOnly.FromDateTime(departedAtUtc.UtcDateTime),
            member.Version,
            "user:owner",
            "employment-ended",
            Guid.NewGuid(),
            [],
            departedAtUtc).IsSuccess);
        Assert.True(member.Anonymise(
            member.Version,
            "user:privacy",
            Guid.NewGuid(),
            new DateTimeOffset(
                2026,
                7,
                30,
                12,
                0,
                0,
                TimeSpan.Zero)).IsSuccess);
        dbContext.StaffMembers.Add(member);
        dbContext.ProcessingRestrictionProjections.Add(
            CreateRestrictionProjection(member));
        await dbContext.SaveChangesAsync();
        StaffMemberRepository repository = new(dbContext);

        Assert.Null(await repository.GetAsync(
            member.Id,
            CancellationToken.None));
        Assert.Null(await repository.GetByAuthSubjectAsync(
            previousAuthSubject,
            CancellationToken.None));
        Assert.Null(await repository.GetDirectoryAsync(
            member.Id,
            CancellationToken.None));
        Assert.Empty((await repository.ListDirectoryAsync(
            search: null,
            status: null,
            new PageRequest(
                PageRequest.DefaultPage,
                PageRequest.DefaultPageSize),
            CancellationToken.None)).Items);
        Assert.NotNull(await repository.GetForDataRightsAsync(
            member.Id,
            CancellationToken.None));
        Assert.NotNull(await repository.GetForSafetyTransitionAsync(
            member.Id,
            CancellationToken.None));
    }

    private static StaffMember CreateMember(
        string displayName = "Ada Operator",
        string identity = "private",
        string scopeId = TenantA) => StaffMember.Create(
        Guid.NewGuid(),
        scopeId,
        displayName,
        $"{displayName} Legal",
        $"{identity}@example.test",
        "+15550100",
        $"EMP-{identity.ToUpperInvariant()}",
        "Manager",
        "Operations",
        $"auth-{identity}",
        "user:owner",
        Guid.NewGuid(),
        new DateTimeOffset(2026, 7, 21, 9, 0, 0, TimeSpan.Zero)).Value;

    private static void Assign(StaffMember member, Guid propertyId, bool isPrimary) =>
        Assert.True(member.AssignProperty(
            Guid.NewGuid(),
            propertyId,
            propertyJobTitle: null,
            isPrimary,
            new DateOnly(2026, 7, 21),
            member.Version,
            "user:owner",
            Guid.NewGuid(),
            new DateTimeOffset(2026, 7, 21, 9, 0, 0, TimeSpan.Zero)).IsSuccess);

    private static StaffProcessingRestrictionProjection
        CreateRestrictionProjection(StaffMember member) =>
        StaffProcessingRestrictionProjection.Create(
            member.ScopeId,
            member.Id,
            StaffProcessingRestrictionContract.CurrentVersion,
            member.CreatedAtUtc).Value;

    private static StaffDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<StaffDbContext>()
            .UseInMemoryDatabase($"staff-directory-{Guid.NewGuid():N}")
            .Options,
        new TestScopeContext());

    private static StaffDbContext CreateDbContext(
        InMemoryDatabaseRoot root,
        string databaseName,
        string scopeId) => new(
        new DbContextOptionsBuilder<StaffDbContext>()
            .UseInMemoryDatabase(databaseName, root)
            .Options,
        new TestScopeContext(scopeId));

    private sealed class TestScopeContext(string scopeId = TenantA)
        : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }
}
