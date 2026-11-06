namespace BunkFy.Modules.Staff.Tests;

using System.Reflection;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Staff.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

public sealed class StaffStationLabelReaderTests
{
    private const string Tenant = "aa000000-0000-0000-0000-000000000001";
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Date = new(2026, 9, 20);
    private static readonly string[] Members = ["DisplayName", "StaffMemberId", "Version"];

    [Fact]
    public async Task Exact_bounded_IDs_return_only_minimized_current_owner_labels_without_tracking()
    {
        await using var db = Context();
        Guid property = Guid.NewGuid();
        StaffMember a = Add(db, property), b = Add(db, property);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var reader = new StaffStationLabelReader(db);
        Assert.Empty(await reader.ResolveAsync(Tenant, property, [], Date));
        var label = Assert.Single(await reader.ResolveAsync(Tenant, property, [a.Id, Guid.NewGuid()], Date));
        Assert.Equal(new(a.Id, a.DisplayName, a.Version), label);
        Assert.DoesNotContain(b.Id, (await reader.ResolveAsync(Tenant, property, [a.Id], Date)).Select(x => x.StaffMemberId));
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(Members, typeof(StaffStationLabel).GetProperties().Select(x => x.Name).Order());
    }

    [Theory]
    [InlineData("future")]
    [InlineData("restricted")]
    [InlineData("missing-restriction")]
    [InlineData("suspended")]
    [InlineData("other-property")]
    public async Task Ineligible_owner_state_never_discloses_a_label(string state)
    {
        await using var db = Context();
        Guid property = Guid.NewGuid();
        StaffMember member = Add(db, state == "other-property" ? Guid.NewGuid() : property);
        if (state == "future")
        { Set(member.Assignments.Single(), "EffectiveFrom", Date.AddDays(1)); }
        if (state == "suspended")
        { Assert.True(member.Suspend(member.Version, "user:fixture", "fixture", Guid.NewGuid(), Now).IsSuccess); }
        var restriction = db.ProcessingRestrictionProjections.Local.Single();
        if (state == "restricted")
        { Assert.True(restriction.Apply(0, StaffProcessingRestrictionContract.CurrentVersion, Now).IsSuccess); }
        if (state == "missing-restriction")
        { db.ProcessingRestrictionProjections.Remove(restriction); }
        await db.SaveChangesAsync();
        Assert.Empty(await new StaffStationLabelReader(db).ResolveAsync(Tenant, property, [member.Id], Date));
    }

    [Fact]
    public async Task No_cross_tenant_unbounded_duplicate_or_disabled_scope_query()
    {
        var scope = new Scope();
        await using var db = Context(scope);
        var reader = new StaffStationLabelReader(db);
        Guid property = Guid.NewGuid(), staff = Guid.NewGuid();
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ResolveAsync(Guid.NewGuid().ToString("D"), property, [staff], Date));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ResolveAsync(Tenant, property, [staff, staff], Date));
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ResolveAsync(Tenant, property, Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToArray(), Date));
        scope.IsEnabled = false;
        await Assert.ThrowsAsync<ArgumentException>(() => reader.ResolveAsync(Tenant, property, [staff], Date));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ResolveAsync(Tenant, property, [staff], Date, cancellation.Token));
    }

    private static StaffDbContext Context(Scope? scope = null) => new(
        new DbContextOptionsBuilder<StaffDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options, scope ?? new Scope());
    private static StaffMember Add(StaffDbContext db, Guid property)
    {
        var staff = StaffMember.Create(Guid.NewGuid(), Tenant, "Same synthetic label", null, null, null, null, null, null,
            null, "user:fixture", Guid.NewGuid(), Now).Value;
        Assert.True(staff.AssignProperty(Guid.NewGuid(), property, null, false, Date, staff.Version,
            "user:fixture", Guid.NewGuid(), Now).IsSuccess);
        db.StaffMembers.Add(staff);
        db.ProcessingRestrictionProjections.Add(StaffProcessingRestrictionProjection.Create(Tenant, staff.Id,
            StaffProcessingRestrictionContract.CurrentVersion, Now).Value);
        return staff;
    }
    private static void Set(object target, string property, object value) => target.GetType()
        .GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!.SetValue(target, value);
    private sealed class Scope : IScopeContext
    {
        public bool IsEnabled { get; set; } = true;
        public string ScopeId => Tenant;
    }
}
