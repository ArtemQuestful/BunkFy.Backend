namespace BunkFy.Modules.Staff.Tests.Persistence;

using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Staff.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffOnboardingStateVerifierTests
{
    private const string TenantId = "tenant-proof";
    private const string SubjectId = "auth:staff-proof";
    private static readonly Guid MemberId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OperationId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid FirstPropertyId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SecondPropertyId =
        Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Exact_state_is_deterministic_and_performs_no_write()
    {
        await using StaffDbContext context = await CreateFixtureAsync(
            "staff-proof-stable",
            Now.AddMinutes(3));
        long revisionBefore = (await context.TenantRevisions
            .AsNoTracking()
            .SingleAsync()).Revision;
        context.ChangeTracker.Clear();
        StaffOnboardingStateVerifier verifier = new(
            context,
            new FixedTimeProvider(Now.AddMinutes(10)));
        StaffOnboardingStateVerificationRequest request = Request();

        StaffOnboardingStateVerificationResult first =
            await verifier.VerifyAsync(request);
        StaffOnboardingStateVerificationResult second =
            await verifier.VerifyAsync(request);

        Assert.Equal(
            StaffOnboardingStateVerificationStatus.Equivalent,
            first.Status);
        Assert.Equal(StaffOnboardingStateVerificationReason.Equivalent, first.Reason);
        Assert.Equal(first, second);
        Assert.Equal(2, first.PropertyCount);
        Assert.Equal(64, first.PropertySetSha256?.Length);
        Assert.Equal(64, first.StateProofSha256?.Length);
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(
            revisionBefore,
            (await context.TenantRevisions.AsNoTracking().SingleAsync()).Revision);
    }

    [Fact]
    public async Task State_proof_changes_when_verified_operation_time_changes()
    {
        await using StaffDbContext firstContext = await CreateFixtureAsync(
            "staff-proof-time-a",
            Now.AddMinutes(3));
        await using StaffDbContext secondContext = await CreateFixtureAsync(
            "staff-proof-time-b",
            Now.AddMinutes(4));
        FixedTimeProvider clock = new(Now.AddMinutes(10));

        StaffOnboardingStateVerificationResult first =
            await new StaffOnboardingStateVerifier(firstContext, clock)
                .VerifyAsync(Request());
        StaffOnboardingStateVerificationResult second =
            await new StaffOnboardingStateVerifier(secondContext, clock)
                .VerifyAsync(Request());

        Assert.Equal(
            StaffOnboardingStateVerificationStatus.Equivalent,
            first.Status);
        Assert.Equal(
            StaffOnboardingStateVerificationStatus.Equivalent,
            second.Status);
        Assert.NotEqual(first.StateProofSha256, second.StateProofSha256);
        Assert.Equal(first.PropertySetSha256, second.PropertySetSha256);
    }

    [Fact]
    public async Task Subject_or_property_drift_fails_closed()
    {
        await using StaffDbContext context = await CreateFixtureAsync(
            "staff-proof-conflict",
            Now.AddMinutes(3));
        StaffOnboardingStateVerifier verifier = new(
            context,
            new FixedTimeProvider(Now.AddMinutes(10)));

        StaffOnboardingStateVerificationResult subjectConflict =
            await verifier.VerifyAsync(Request(authSubjectId: "auth:other"));
        StaffOnboardingStateVerificationResult propertyConflict =
            await verifier.VerifyAsync(Request(propertyIds: [FirstPropertyId]));

        Assert.Equal(
            StaffOnboardingStateVerificationStatus.ReviewRequired,
            subjectConflict.Status);
        Assert.Equal(
            StaffOnboardingStateVerificationReason.MemberConflict,
            subjectConflict.Reason);
        Assert.Equal(
            StaffOnboardingStateVerificationStatus.ReviewRequired,
            propertyConflict.Status);
        Assert.Equal(
            StaffOnboardingStateVerificationReason.PropertySetConflict,
            propertyConflict.Reason);
    }

    [Fact]
    public async Task Missing_tenant_revision_is_retryable_not_equivalent()
    {
        await using StaffDbContext context = CreateContext(
            "staff-proof-unavailable");
        StaffOnboardingStateVerifier verifier = new(
            context,
            new FixedTimeProvider(Now));

        StaffOnboardingStateVerificationResult result =
            await verifier.VerifyAsync(Request());

        Assert.Equal(
            StaffOnboardingStateVerificationStatus.RetryLater,
            result.Status);
        Assert.Equal(
            StaffOnboardingStateVerificationReason.TenantStateUnavailable,
            result.Reason);
    }

    [Fact]
    public async Task Operation_member_mismatch_fails_closed()
    {
        await using StaffDbContext context = await CreateFixtureAsync(
            "staff-proof-operation-conflict",
            Now.AddMinutes(3));
        StaffOnboardingStateVerifier verifier = new(
            context,
            new FixedTimeProvider(Now.AddMinutes(10)));

        StaffOnboardingStateVerificationResult result =
            await verifier.VerifyAsync(new StaffOnboardingStateVerificationRequest(
                TenantId,
                OperationId,
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                SubjectId,
                [FirstPropertyId, SecondPropertyId]));

        Assert.Equal(
            StaffOnboardingStateVerificationStatus.ReviewRequired,
            result.Status);
        Assert.Equal(
            StaffOnboardingStateVerificationReason.OperationConflict,
            result.Reason);
    }

    [Fact]
    public async Task Active_processing_restriction_fails_closed()
    {
        await using StaffDbContext context = await CreateFixtureAsync(
            "staff-proof-restricted",
            Now.AddMinutes(3));
        context.ProcessingRestrictions.Add(
            StaffProcessingRestriction.Create(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                TenantId,
                MemberId,
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                applyApprovalRevision: 1,
                applySelectedStaffVersion: 3,
                "user:privacy",
                Now.AddMinutes(4)).Value);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        StaffOnboardingStateVerificationResult result =
            await new StaffOnboardingStateVerifier(
                    context,
                    new FixedTimeProvider(Now.AddMinutes(10)))
                .VerifyAsync(Request());

        Assert.Equal(
            StaffOnboardingStateVerificationStatus.ReviewRequired,
            result.Status);
        Assert.Equal(
            StaffOnboardingStateVerificationReason.ProcessingRestricted,
            result.Reason);
    }

    [Fact]
    public async Task Anonymised_member_fails_closed()
    {
        await using StaffDbContext context = await CreateFixtureAsync(
            "staff-proof-anonymised",
            Now.AddMinutes(3));
        StaffMember member = await context.StaffMembers
            .Include(item => item.Assignments)
            .SingleAsync();
        Assert.True(member.Depart(
            DateOnly.FromDateTime(Now.UtcDateTime),
            member.Version,
            "user:privacy",
            "retention",
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            [
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")
            ],
            Now.AddMinutes(4)).IsSuccess);
        Assert.True(member.Anonymise(
            member.Version,
            "user:privacy",
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            Now.AddMinutes(5)).IsSuccess);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        StaffOnboardingStateVerificationResult result =
            await new StaffOnboardingStateVerifier(
                    context,
                    new FixedTimeProvider(Now.AddMinutes(10)))
                .VerifyAsync(Request(propertyIds: []));

        Assert.Equal(
            StaffOnboardingStateVerificationStatus.ReviewRequired,
            result.Status);
        Assert.Equal(
            StaffOnboardingStateVerificationReason.MemberConflict,
            result.Reason);
    }

    private static StaffOnboardingStateVerificationRequest Request(
        string authSubjectId = SubjectId,
        IReadOnlyList<Guid>? propertyIds = null) => new(
        TenantId,
        OperationId,
        MemberId,
        authSubjectId,
        propertyIds ?? [FirstPropertyId, SecondPropertyId]);

    private static async Task<StaffDbContext> CreateFixtureAsync(
        string databaseName,
        DateTimeOffset operationCompletedAtUtc)
    {
        StaffDbContext context = CreateContext(databaseName);
        StaffMember member = StaffMember.Create(
            MemberId,
            TenantId,
            "Proof Operator",
            legalName: null,
            workEmail: null,
            workPhone: null,
            employeeNumber: null,
            jobTitle: null,
            department: null,
            SubjectId,
            "system:onboarding",
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            Now).Value;
        Assert.True(member.AssignProperty(
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            FirstPropertyId,
            propertyJobTitle: null,
            isPrimary: false,
            DateOnly.FromDateTime(Now.UtcDateTime),
            member.Version,
            "system:onboarding",
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(member.AssignProperty(
            Guid.Parse("88888888-8888-8888-8888-888888888888"),
            SecondPropertyId,
            propertyJobTitle: null,
            isPrimary: false,
            DateOnly.FromDateTime(Now.UtcDateTime),
            member.Version,
            "system:onboarding",
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            Now.AddMinutes(2)).IsSuccess);
        context.StaffMembers.Add(member);
        context.MemberMutationOperations.Add(new StaffMemberMutationOperation(
            new StaffMemberMutationOperationRecord(
                OperationId,
                TenantId,
                MemberId,
                StaffMemberMutationKind.OnboardingProvision,
                1,
                new string('a', 64),
                StaffStatus.Active,
                member.Version,
                operationCompletedAtUtc)));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return context;
    }

    private static StaffDbContext CreateContext(string databaseName) => new(
        new DbContextOptionsBuilder<StaffDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options,
        new TestScopeContext());

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
