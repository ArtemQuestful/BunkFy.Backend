namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

[Trait("Category", "Unit")]
public sealed class
    WorkspaceStaffOnboardingIdentityAnchorSubjectMutationFenceTests
{
    private const string TenantId =
        "10000000-0000-0000-0000-000000000001";
    private const string SubjectId = "subject-a";
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        11,
        12,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task Exact_absent_unanchored_rows_are_read_in_bounded_pages()
    {
        await using WorkspacesDbContext context = CreateContext();
        for (int index = 1; index <= 501; index++)
        {
            context.StaffOnboardingApplications.Add(
                CreateApplication(CreateOrderedId(index), SubjectId));
        }

        context.StaffOnboardingApplications.Add(
            CreateApplication(
                CreateOrderedId(502),
                "unrelated-subject"));
        await context.SaveChangesAsync();
        RecordingOutcomeReader outcomes = new();
        WorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence fence =
            CreateFence(context, outcomes);

        bool allowed = await fence.CanMutateAsync(
            TenantId,
            SubjectId,
            CancellationToken.None);

        Assert.True(allowed);
        Assert.Equal([500, 1], outcomes.BatchSizes);
        Assert.All(
            outcomes.ExpectedSubjects,
            subject => Assert.Equal(SubjectId, subject));
    }

    [Fact]
    public async Task Unresolved_anchor_blocks_subject_mutation()
    {
        await using WorkspacesDbContext context = CreateContext();
        WorkspaceStaffOnboarding application = CreateApplication(
            CreateOrderedId(1),
            SubjectId);
        context.StaffOnboardingApplications.Add(application);
        await context.SaveChangesAsync();
        Guid staffMemberId = Guid.Parse(
            "20000000-0000-0000-0000-000000000001");
        RecordingOutcomeReader outcomes = new(request => new(
            request.ApplicationId,
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved,
            staffMemberId,
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact,
            WorkspaceApplicationVersion: application.Version,
            ResolutionDisposition: null,
            ResolutionEventId: Guid.Parse(
                "30000000-0000-0000-0000-000000000001")));

        bool allowed = await CreateFence(context, outcomes).CanMutateAsync(
            TenantId,
            SubjectId,
            CancellationToken.None);

        Assert.False(allowed);
        Assert.Equal([1], outcomes.BatchSizes);
    }

    [Fact]
    public async Task Malformed_result_set_fails_closed()
    {
        await using WorkspacesDbContext context = CreateContext();
        context.StaffOnboardingApplications.Add(
            CreateApplication(CreateOrderedId(1), SubjectId));
        await context.SaveChangesAsync();
        RecordingOutcomeReader outcomes = new(returnEmpty: true);

        bool allowed = await CreateFence(context, outcomes).CanMutateAsync(
            TenantId,
            SubjectId,
            CancellationToken.None);

        Assert.False(allowed);
    }

    [Fact]
    public async Task Empty_tenant_fails_closed_even_without_a_subject()
    {
        await using WorkspacesDbContext context = CreateContext();
        RecordingOutcomeReader outcomes = new();

        bool allowed = await CreateFence(context, outcomes).CanMutateAsync(
            " ",
            subjectId: null,
            CancellationToken.None);

        Assert.False(allowed);
        Assert.Empty(outcomes.BatchSizes);
    }

    [Fact]
    public async Task Different_active_tenant_fails_closed_before_read()
    {
        await using WorkspacesDbContext context = CreateContext();
        RecordingOutcomeReader outcomes = new();

        bool allowed = await CreateFence(context, outcomes).CanMutateAsync(
            "20000000-0000-0000-0000-000000000002",
            SubjectId,
            CancellationToken.None);

        Assert.False(allowed);
        Assert.Empty(outcomes.BatchSizes);
    }

    private static WorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence
        CreateFence(
            WorkspacesDbContext context,
            IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader outcomes) =>
        new(
            context,
            outcomes,
            new TestScopeContext(),
            NullLogger<
                WorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence>
                .Instance);

    private static WorkspacesDbContext CreateContext() => new(
        new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options,
        new TestScopeContext());

    private static WorkspaceStaffOnboarding CreateApplication(
        Guid applicationId,
        string subjectId) => WorkspaceStaffOnboarding.Create(
            applicationId,
            TenantId,
            WorkspaceStaffOnboardingSource.Invitation,
            Guid.NewGuid(),
            subjectId,
            $"{subjectId}@example.test",
            "Subject",
            legalName: null,
            workEmail: null,
            workPhone: null,
            employeeNumber: null,
            jobTitle: null,
            department: null,
            Now).Value;

    private static Guid CreateOrderedId(int value) => Guid.Parse(
        $"00000000-0000-0000-0000-{value:D12}");

    private sealed class RecordingOutcomeReader(
        Func<
            StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest,
            StaffWorkspaceOnboardingIdentityAnchorOutcome>? resolve = null,
        bool returnEmpty = false)
        : IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
    {
        public List<int> BatchSizes { get; } = [];
        public List<string> ExpectedSubjects { get; } = [];

        public Task<IReadOnlyList<
            StaffWorkspaceOnboardingIdentityAnchorOutcome>> ReadAsync(
            IReadOnlyList<
                StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest>
                requests,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.BatchSizes.Add(requests.Count);
            this.ExpectedSubjects.AddRange(
                requests.Select(request => request.ExpectedAuthSubjectId));
            if (returnEmpty)
            {
                return Task.FromResult<IReadOnlyList<
                    StaffWorkspaceOnboardingIdentityAnchorOutcome>>([]);
            }

            return Task.FromResult<IReadOnlyList<
                StaffWorkspaceOnboardingIdentityAnchorOutcome>>(
                requests.Select(request => resolve?.Invoke(request) ?? new(
                        request.ApplicationId,
                        StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus
                            .Absent,
                        StaffMemberId: null,
                        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                            .Unknown,
                        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                            .Unknown,
                        WorkspaceApplicationVersion: null,
                        ResolutionDisposition: null,
                        ResolutionEventId: null))
                    .ToArray());
        }
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }
}
