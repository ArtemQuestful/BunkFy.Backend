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

[Trait("Category", "Unit")]
public sealed class StaffStationEligibilitySourceTests
{
    private const string Tenant = "tenant-a";
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Date = DateOnly.FromDateTime(Now.UtcDateTime);
    private static readonly string[] SnapshotProperties =
        ["AssignmentState", "AuthLinkState", "AuthSubjectId", "OpenAssignment",
            "ProcessingRestriction", "PropertyId", "ScopeId", "StaffMemberId", "Status", "Version"];

    [Fact]
    public async Task Exact_facts_are_minimal_untracked_and_do_not_claim_Auth_admission()
    {
        await using var db = Context();
        Guid property = Guid.NewGuid();
        StaffMember member = Member("owner-defined-subject");
        Assign(member, property);
        db.StaffMembers.Add(member);
        db.ProcessingRestrictionProjections.Add(Restriction(member));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var facts = Assert.IsType<StaffStationEligibilitySnapshot>(await Read(db, member.Id, property));
        Assert.Equal(Tenant, facts.ScopeId);
        Assert.Equal(member.Id, facts.StaffMemberId);
        Assert.Equal(property, facts.PropertyId);
        Assert.Equal(member.Version, facts.Version);
        Assert.Equal(StaffStatus.Active, facts.Status);
        Assert.Equal(StaffStationAuthLinkState.Linked, facts.AuthLinkState);
        Assert.Equal("owner-defined-subject", facts.AuthSubjectId);
        Assert.Equal(StaffStationAssignmentState.Open, facts.AssignmentState);
        Assert.Equal(Assert.Single(member.Assignments).Id, facts.OpenAssignment!.AssignmentId);
        Assert.Equal(member.Version, facts.OpenAssignment.AssignedAtVersion);
        Assert.Equal(Date, facts.OpenAssignment.EffectiveFrom);
        Assert.Equal(StaffProcessingRestrictionDecision.Allowed, facts.ProcessingRestriction.Decision);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(SnapshotProperties,
            typeof(StaffStationEligibilitySnapshot).GetProperties().Select(p => p.Name).Order());
    }

    [Theory]
    [InlineData("tenant-b", false)]
    [InlineData("", false)]
    [InlineData("tenant-a", true)]
    public async Task Wrong_scope_invalid_coordinates_and_disabled_scope_return_no_record(string requestedScope, bool disabled)
    {
        var scope = new TestScope();
        await using var db = Context(scope: scope);
        StaffMember member = Member();
        db.StaffMembers.Add(member);
        await db.SaveChangesAsync();
        scope.IsEnabled = !disabled;
        var reader = new StaffStationEligibilitySource(db);
        Assert.Null(await reader.FindAsync(requestedScope, Guid.NewGuid(), member.Id));
        Assert.Null(await reader.FindAsync(Tenant, Guid.Empty, member.Id));
        Assert.Null(await reader.FindAsync(Tenant, Guid.NewGuid(), Guid.Empty));
    }

    [Theory]
    [InlineData(StaffMemberState.Active, StaffStatus.Active)]
    [InlineData(StaffMemberState.Suspended, StaffStatus.Suspended)]
    [InlineData(StaffMemberState.Departed, StaffStatus.Departed)]
    [InlineData(StaffMemberState.Anonymised, StaffStatus.Unknown)]
    [InlineData((StaffMemberState)999, StaffStatus.Unknown)]
    public async Task Lifecycle_is_reported_as_facts_not_filtered_into_a_missing_record(StaffMemberState state, StaffStatus expected)
    {
        await using var db = Context();
        StaffMember member = Member();
        Set(member, nameof(member.Status), state);
        db.StaffMembers.Add(member);
        await db.SaveChangesAsync();
        var facts = Assert.IsType<StaffStationEligibilitySnapshot>(await Read(db, member.Id, Guid.NewGuid()));
        Assert.Equal(expected, facts.Status);
        Assert.Equal(StaffStationAssignmentState.None, facts.AssignmentState);
        Assert.Null(facts.OpenAssignment);
        Assert.Equal(StaffProcessingRestrictionDecision.Unknown, facts.ProcessingRestriction.Decision);
    }

    [Theory]
    [InlineData(null, StaffStationAuthLinkState.Unlinked)]
    [InlineData("", StaffStationAuthLinkState.Malformed)]
    [InlineData(" x ", StaffStationAuthLinkState.Malformed)]
    [InlineData("x\n", StaffStationAuthLinkState.Malformed)]
    public async Task Missing_and_malformed_linkage_are_explicit(string? subject, StaffStationAuthLinkState expected)
    {
        await using var db = Context();
        StaffMember member = Member();
        Set(member, nameof(member.AuthSubjectId), subject);
        db.StaffMembers.Add(member);
        await db.SaveChangesAsync();
        var facts = Assert.IsType<StaffStationEligibilitySnapshot>(await Read(db, member.Id, Guid.NewGuid()));
        Assert.Equal(expected, facts.AuthLinkState);
        Assert.Null(facts.AuthSubjectId);
    }

    [Fact]
    public async Task Duplicate_owner_backlinks_are_not_reported_as_unique_links()
    {
        await using var db = Context();
        StaffMember member = Member("same-subject");
        db.StaffMembers.AddRange(member, Member("same-subject"));
        // InMemory deliberately permits corrupt duplicates; PostgreSQL unique protection is tested separately.
        await db.SaveChangesAsync();
        var facts = Assert.IsType<StaffStationEligibilitySnapshot>(await Read(db, member.Id, Guid.NewGuid()));
        Assert.Equal(StaffStationAuthLinkState.Ambiguous, facts.AuthLinkState);
        Assert.Null(facts.AuthSubjectId);
    }

    [Theory]
    [InlineData(0, StaffProcessingRestrictionDecision.Unknown)]
    [InlineData(1, StaffProcessingRestrictionDecision.Allowed)]
    [InlineData(2, StaffProcessingRestrictionDecision.Restricted)]
    [InlineData(3, StaffProcessingRestrictionDecision.UnsupportedContractVersion)]
    [InlineData(4, StaffProcessingRestrictionDecision.Unknown)]
    public async Task Restriction_missing_allowed_restricted_unsupported_and_malformed_are_distinct(int scenario, StaffProcessingRestrictionDecision expected)
    {
        await using var db = Context();
        StaffMember member = Member();
        db.StaffMembers.Add(member);
        if (scenario > 0)
        {
            var restriction = Restriction(member);
            if (scenario == 2)
            {
                Assert.True(restriction.Apply(0, StaffProcessingRestrictionContract.CurrentVersion, Now).IsSuccess);
            }
            if (scenario == 3)
            {
                Set(restriction, nameof(restriction.ContractVersion), StaffProcessingRestrictionContract.CurrentVersion + 1);
            }
            if (scenario == 4)
            {
                Set(restriction, nameof(restriction.ActiveRestrictionCount), 1);
            }
            db.ProcessingRestrictionProjections.Add(restriction);
        }
        await db.SaveChangesAsync();
        var facts = Assert.IsType<StaffStationEligibilitySnapshot>(await Read(db, member.Id, Guid.NewGuid()));
        Assert.Equal(expected, facts.ProcessingRestriction.Decision);
        Assert.Equal(scenario is 0 or 4 ? null : scenario == 2 ? 1L : 0L,
            facts.ProcessingRestriction.ProjectionRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_or_ambiguous_current_assignment_never_returns_assignment_facts(bool duplicate)
    {
        await using var db = Context();
        StaffMember member = Member();
        Guid property = Guid.NewGuid();
        Assign(member, property);
        if (duplicate)
        {
            Assign(member, Guid.NewGuid());
            Set(member.Assignments.Last(), "PropertyId", property);
        }
        else
        {
            Set(member.Assignments.Single(), "AssignedAtVersion", member.Version + 1);
        }
        db.StaffMembers.Add(member);
        await db.SaveChangesAsync();
        var facts = Assert.IsType<StaffStationEligibilitySnapshot>(await Read(db, member.Id, property));
        Assert.Equal(duplicate ? StaffStationAssignmentState.Ambiguous : StaffStationAssignmentState.Malformed, facts.AssignmentState);
        Assert.Null(facts.OpenAssignment);
    }

    [Fact]
    public async Task Open_assignment_preserves_future_date_without_claiming_effective_now()
    {
        await using var db = Context();
        StaffMember member = Member();
        Guid property = Guid.NewGuid();
        Assign(member, property);
        // Persisted owner facts can outlive clock/restore changes. Do not infer eligibility from IsCurrent.
        DateOnly futureDate = Date.AddDays(7);
        Set(member.Assignments.Single(), "EffectiveFrom", futureDate);
        db.StaffMembers.Add(member);
        await db.SaveChangesAsync();
        var facts = Assert.IsType<StaffStationEligibilitySnapshot>(await Read(db, member.Id, property));
        Assert.Equal(StaffStationAssignmentState.Open, facts.AssignmentState);
        Assert.Equal(futureDate, facts.OpenAssignment!.EffectiveFrom);
        Assert.True(facts.OpenAssignment.EffectiveFrom > Date);
    }

    [Fact]
    public async Task Next_read_observes_link_assignment_restriction_and_lifecycle_changes_without_caching()
    {
        string store = Guid.NewGuid().ToString("N");
        await using var db = Context(store);
        StaffMember member = Member();
        Guid property = Guid.NewGuid();
        Assign(member, property);
        db.StaffMembers.Add(member);
        var restriction = Restriction(member);
        db.ProcessingRestrictionProjections.Add(restriction);
        await db.SaveChangesAsync();
        long before = member.Version;
        Assert.Equal(StaffStationAuthLinkState.Linked, (await Read(db, member.Id, property))!.AuthLinkState);
        Assert.True(member.Suspend(member.Version, "user:owner", "changed", Guid.NewGuid(), Now).IsSuccess);
        Assert.True(member.SetAuthSubject(null, member.Version, "user:owner", Guid.NewGuid(), Now).IsSuccess);
        Assert.True(member.UnassignProperty(property, Date, member.Version, "user:owner", "changed", Guid.NewGuid(), Now).IsSuccess);
        Assert.True(restriction.Apply(0, StaffProcessingRestrictionContract.CurrentVersion, Now).IsSuccess);
        await db.SaveChangesAsync();
        var changed = (await Read(db, member.Id, property))!;
        Assert.True(changed.Version > before);
        Assert.Equal(StaffStatus.Suspended, changed.Status);
        Assert.Equal(StaffStationAuthLinkState.Unlinked, changed.AuthLinkState);
        Assert.Equal(StaffStationAssignmentState.None, changed.AssignmentState);
        Assert.Equal(StaffProcessingRestrictionDecision.Restricted, changed.ProcessingRestriction.Decision);
        Assert.True(member.Resume(member.Version, "user:owner", "changed", Guid.NewGuid(), Now).IsSuccess);
        Assert.True(member.SetAuthSubject("replacement", member.Version, "user:owner", Guid.NewGuid(), Now).IsSuccess);
        await db.SaveChangesAsync();
        Assert.Equal("replacement", (await Read(db, member.Id, property))!.AuthSubjectId);
        Assert.True(member.Depart(Date, member.Version, "user:owner", "changed", Guid.NewGuid(), [], Now).IsSuccess);
        await db.SaveChangesAsync();
        Assert.Equal(StaffStatus.Departed, (await Read(db, member.Id, property))!.Status);
    }

    [Fact]
    public async Task Missing_invalid_version_and_cancellation_do_not_produce_facts()
    {
        await using var db = Context();
        Assert.Null(await Read(db, Guid.NewGuid(), Guid.NewGuid()));
        StaffMember member = Member();
        Set(member, nameof(member.Version), 0L);
        db.StaffMembers.Add(member);
        await db.SaveChangesAsync();
        Assert.Null(await Read(db, member.Id, Guid.NewGuid()));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new StaffStationEligibilitySource(db).FindAsync(Tenant, Guid.NewGuid(), member.Id, cancel.Token));
    }

    private static Task<StaffStationEligibilitySnapshot?> Read(StaffDbContext db, Guid member, Guid property) =>
        new StaffStationEligibilitySource(db).FindAsync(Tenant, property, member);
    private static StaffDbContext Context(string? name = null, TestScope? scope = null) => new(
        new DbContextOptionsBuilder<StaffDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString("N")).Options,
        scope ?? new TestScope());
    private static StaffMember Member(string? subject = "subject-one") => StaffMember.Create(Guid.NewGuid(), Tenant,
        "Synthetic staff", null, null, null, null, null, null, subject, "user:owner", Guid.NewGuid(), Now).Value;
    private static void Assign(StaffMember member, Guid property) => Assert.True(member.AssignProperty(
        Guid.NewGuid(), property, null, false, Date, member.Version, "user:owner", Guid.NewGuid(), Now).IsSuccess);
    private static StaffProcessingRestrictionProjection Restriction(StaffMember member) =>
        StaffProcessingRestrictionProjection.Create(Tenant, member.Id, StaffProcessingRestrictionContract.CurrentVersion, Now).Value;
    private static void Set(object target, string property, object? value) => target.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);
    private sealed class TestScope : IScopeContext
    {
        public bool IsEnabled { get; set; } = true;
        public string ScopeId => Tenant;
    }
}
