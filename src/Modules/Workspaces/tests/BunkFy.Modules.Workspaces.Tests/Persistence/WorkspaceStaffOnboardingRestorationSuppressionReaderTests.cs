namespace BunkFy.Modules.Workspaces.Tests;

using System.Reflection;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffOnboardingRestorationSuppressionReaderTests
{
    private const string TenantId = "tenant-a";
    private const string SubjectId = "subject-a";
    private static readonly Guid StaffMemberId = Guid.Parse(
        "30000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        11,
        12,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task Completed_resolution_does_not_suppress_normal_restoration()
    {
        await using WorkspacesDbContext context = CreateContext();
        context.StaffOnboardingApplications.Add(
            CreateCompletedApplication(
                Guid.Parse("10000000-0000-0000-0000-000000000001"),
                StaffMemberId,
                SubjectId));
        await context.SaveChangesAsync();

        WorkspaceStaffOnboardingRestorationSuppressionReader reader = new(
            context);
        WorkspaceStaffOnboardingRestorationSuppressionState result =
            await reader.ReadAsync(
                TenantId,
                StaffMemberId,
                SubjectId,
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingRestorationSuppressionState.None,
            result);
    }

    [Fact]
    public async Task Any_negative_resolution_is_sticky_for_the_exact_member_and_subject()
    {
        await using WorkspacesDbContext context = CreateContext();
        context.StaffOnboardingApplications.AddRange(
            CreateTerminalApplication(
                Guid.Parse("10000000-0000-0000-0000-000000000002"),
                StaffMemberId,
                SubjectId),
            CreateCompletedApplication(
                Guid.Parse("10000000-0000-0000-0000-000000000003"),
                StaffMemberId,
                SubjectId));
        await context.SaveChangesAsync();

        WorkspaceStaffOnboardingRestorationSuppressionReader reader = new(
            context);
        WorkspaceStaffOnboardingRestorationSuppressionState result =
            await reader.ReadAsync(
                TenantId,
                StaffMemberId,
                SubjectId,
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingRestorationSuppressionState.Suppressed,
            result);
    }

    [Fact]
    public async Task Negative_resolution_does_not_poison_another_subject_or_member()
    {
        await using WorkspacesDbContext context = CreateContext();
        context.StaffOnboardingApplications.Add(
            CreateTerminalApplication(
                Guid.Parse("10000000-0000-0000-0000-000000000004"),
                StaffMemberId,
                SubjectId));
        await context.SaveChangesAsync();

        WorkspaceStaffOnboardingRestorationSuppressionReader reader = new(
            context);
        WorkspaceStaffOnboardingRestorationSuppressionState anotherSubject =
            await reader.ReadAsync(
                TenantId,
                StaffMemberId,
                "subject-b",
                CancellationToken.None);
        WorkspaceStaffOnboardingRestorationSuppressionState anotherMember =
            await reader.ReadAsync(
                TenantId,
                Guid.Parse("30000000-0000-0000-0000-000000000002"),
                SubjectId,
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingRestorationSuppressionState.None,
            anotherSubject);
        Assert.Equal(
            WorkspaceStaffOnboardingRestorationSuppressionState.None,
            anotherMember);
    }

    [Fact]
    public async Task Partial_resolution_coordinates_fail_closed()
    {
        await using WorkspacesDbContext context = CreateContext();
        WorkspaceStaffOnboarding malformed = CreateApplication(
            Guid.Parse("10000000-0000-0000-0000-000000000005"),
            SubjectId);
        SetProperty(
            malformed,
            nameof(WorkspaceStaffOnboarding.StaffMemberId),
            StaffMemberId);
        SetProperty(
            malformed,
            nameof(WorkspaceStaffOnboarding
                .IdentityAnchorResolutionStaffMemberId),
            StaffMemberId);
        context.StaffOnboardingApplications.Add(malformed);
        await context.SaveChangesAsync();

        WorkspaceStaffOnboardingRestorationSuppressionReader reader = new(
            context);
        WorkspaceStaffOnboardingRestorationSuppressionState result =
            await reader.ReadAsync(
                TenantId,
                StaffMemberId,
                SubjectId,
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingRestorationSuppressionState.Conflict,
            result);
    }

    private static WorkspacesDbContext CreateContext() => new(
        new DbContextOptionsBuilder<WorkspacesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options,
        new TestScopeContext());

    private static WorkspaceStaffOnboarding CreateCompletedApplication(
        Guid applicationId,
        Guid staffMemberId,
        string subjectId)
    {
        WorkspaceStaffOnboarding application = CreateApplication(
            applicationId,
            subjectId);
        Assert.True(application.ObserveInvitationAccepted(
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(application.MarkStaffReady(
            staffMemberId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Now.AddMinutes(2)).IsSuccess);
        Assert.True(application.Complete(Now.AddMinutes(3)).IsSuccess);
        return application;
    }

    private static WorkspaceStaffOnboarding CreateTerminalApplication(
        Guid applicationId,
        Guid staffMemberId,
        string subjectId)
    {
        WorkspaceStaffOnboarding application = CreateApplication(
            applicationId,
            subjectId);
        Assert.True(application.Supersede(Now.AddMinutes(1)).IsSuccess);
        Assert.True(application.ConvergeTerminalStaffAnchor(
            staffMemberId,
            Guid.NewGuid(),
            Now.AddMinutes(2)).IsSuccess);
        return application;
    }

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

    private static void SetProperty(
        WorkspaceStaffOnboarding application,
        string propertyName,
        object value) => typeof(WorkspaceStaffOnboarding)
        .GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public |
            BindingFlags.NonPublic)!
        .SetValue(application, value);

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }
}
