namespace BunkFy.Extensions.Workspaces.Tests;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class
    WorkspaceStaffOnboardingIdentityAnchorResolutionHandlerTests
{
    [Theory]
    [InlineData(
        StaffWorkspaceOnboardingIdentityAnchorResolutionStatus.Recorded)]
    [InlineData(
        StaffWorkspaceOnboardingIdentityAnchorResolutionStatus.AlreadyRecorded)]
    public async Task Exact_resolution_is_recorded_idempotently(
        StaffWorkspaceOnboardingIdentityAnchorResolutionStatus status)
    {
        RecordingRecorder recorder = new(status);
        WorkspaceStaffOnboardingIdentityAnchorResolutionHandler handler =
            new(recorder);
        WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent
            integrationEvent = CreateEvent();

        await handler.HandleAsync(integrationEvent, CancellationToken.None);

        StaffWorkspaceOnboardingIdentityAnchorResolutionRequest request =
            Assert.Single(recorder.Requests);
        Assert.Equal(integrationEvent.EventId, request.ResolutionEventId);
        Assert.Equal(integrationEvent.ApplicationId, request.ApplicationId);
        Assert.Equal(integrationEvent.StaffMemberId, request.StaffMemberId);
        Assert.Equal(
            integrationEvent.WorkspaceApplicationVersion,
            request.WorkspaceApplicationVersion);
        Assert.Equal(
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .CompletedRedacted,
            request.Disposition);
        Assert.Equal(integrationEvent.OccurredAtUtc, request.ResolvedAtUtc);
    }

    [Theory]
    [InlineData(
        StaffWorkspaceOnboardingIdentityAnchorResolutionStatus.AnchorAbsent)]
    [InlineData(
        StaffWorkspaceOnboardingIdentityAnchorResolutionStatus.Conflict)]
    public async Task Non_recorded_resolution_keeps_the_inbox_retryable(
        StaffWorkspaceOnboardingIdentityAnchorResolutionStatus status)
    {
        RecordingRecorder recorder = new(status);
        WorkspaceStaffOnboardingIdentityAnchorResolutionHandler handler =
            new(recorder);

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(() => handler.HandleAsync(
                CreateEvent(),
                CancellationToken.None));

        Assert.Contains(status.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Single(recorder.Requests);
    }

    private static
        WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent
        CreateEvent() => new(
            ResolutionEventId,
            TenantId,
            Now,
            ApplicationId,
            StaffMemberId,
            workspaceApplicationVersion: 7,
            WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                .CompletedRedacted);

    private sealed class RecordingRecorder(
        StaffWorkspaceOnboardingIdentityAnchorResolutionStatus status)
        : IStaffWorkspaceOnboardingIdentityAnchorResolutionRecorder
    {
        public List<StaffWorkspaceOnboardingIdentityAnchorResolutionRequest>
            Requests
        { get; } = [];

        public Task<StaffWorkspaceOnboardingIdentityAnchorResolutionResult>
            RecordAsync(
                StaffWorkspaceOnboardingIdentityAnchorResolutionRequest request,
                CancellationToken cancellationToken = default)
        {
            this.Requests.Add(request);
            return Task.FromResult(
                new StaffWorkspaceOnboardingIdentityAnchorResolutionResult(
                    status));
        }
    }

    private static readonly Guid ApplicationId =
        Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid StaffMemberId =
        Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid ResolutionEventId =
        Guid.Parse("20000000-0000-0000-0000-000000000003");
    private const string TenantId =
        "30000000-0000-0000-0000-000000000003";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 11, 16, 0, 0, TimeSpan.Zero);
}
