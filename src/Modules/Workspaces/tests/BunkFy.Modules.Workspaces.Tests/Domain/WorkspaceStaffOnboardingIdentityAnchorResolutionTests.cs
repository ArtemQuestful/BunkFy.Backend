namespace BunkFy.Modules.Workspaces.Tests;

using System.Reflection;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.Events;
using Gma.Framework.Messaging;
using Xunit;
using ContractResolutionDisposition =
    BunkFy.Modules.Workspaces.Contracts.WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition;
using DomainResolutionDisposition =
    BunkFy.Modules.Workspaces.Domain.WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffOnboardingIdentityAnchorResolutionTests
{
    [Fact]
    public void Exact_recovery_records_the_bound_resolution_once_at_the_receipted_version()
    {
        WorkspaceStaffOnboarding application = CreateProvisioningApplication();
        Assert.True(application.BindAcceptedMembership(
            Guid.NewGuid(),
            membershipVersion: 1,
            Now.AddMinutes(1)).IsSuccess);
        AnchorCoordinates coordinates = BindStaffAnchor(application);
        Assert.True(application.Fail(
            "Workspaces.AccessProvisioningFailed",
            Now.AddMinutes(3)).IsSuccess);
        application.ClearDomainEvents();
        long selectedVersion = application.Version;

        Assert.True(application.RecoverCompletedExact(
            Now.AddMinutes(4)).IsSuccess);

        Assert.Equal(WorkspaceStaffOnboardingState.Completed, application.Status);
        Assert.Null(application.FailureCode);
        AssertApplicantDataRedacted(application);
        Assert.Equal(selectedVersion + 1, application.Version);
        Assert.Equal(coordinates.ResolutionEventId,
            application.IdentityAnchorResolutionEventId);
        Assert.Equal(application.Version,
            application.IdentityAnchorResolutionApplicationVersion);
        WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent resolution =
            Assert.IsType<WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent>(
                Assert.Single(application.DomainEvents));
        Assert.Equal(coordinates.ResolutionEventId, resolution.EventId);
        Assert.Equal(application.Version, resolution.WorkspaceApplicationVersion);

        Assert.True(application.RecoverCompletedExact(
            Now.AddMinutes(10)).IsSuccess);
        Assert.Equal(selectedVersion + 1, application.Version);
        Assert.Single(application.DomainEvents);
    }

    [Fact]
    public void Identity_anchor_uses_explicit_distinct_random_coordinates()
    {
        Guid applicationId =
            Guid.Parse("10000000-0000-0000-0000-000000000001");
        AnchorCoordinates coordinates = NewAnchorCoordinates(applicationId);
        WorkspaceStaffOnboarding application = CreateProvisioningApplication(
            applicationId);
        Assert.True(application.MarkStaffReady(
            StaffMemberId,
            coordinates.ResolutionEventId,
            coordinates.ContinuationEventId,
            Now.AddMinutes(2)).IsSuccess);
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedDomainEvent
            continuation = Assert.IsType<
                WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedDomainEvent>(
                    Assert.Single(application.DomainEvents));
        application.ClearDomainEvents();
        Assert.True(application.Complete(Now.AddMinutes(3)).IsSuccess);
        WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent resolution =
            Assert.IsType<
                WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent>(
                    Assert.Single(application.DomainEvents));
        Assert.Equal(
            coordinates.ContinuationEventId,
            continuation.EventId);
        Assert.Equal(
            coordinates.ResolutionEventId,
            resolution.EventId);
        Assert.Equal(
            coordinates.ResolutionEventId,
            application.IdentityAnchorExpectedResolutionEventId);
        Assert.Equal(
            coordinates.ContinuationEventId,
            application.IdentityAnchorContinuationEventId);
        Assert.NotEqual(applicationId, coordinates.ContinuationEventId);
        Assert.NotEqual(applicationId, coordinates.ResolutionEventId);
        Assert.NotEqual(
            coordinates.ContinuationEventId,
            coordinates.ResolutionEventId);
        _ = new WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent(
            coordinates.ResolutionEventId,
            WorkspaceStaffOnboardingTests.OrganizationId.ToString("D"),
            Now,
            applicationId,
            StaffMemberId,
            workspaceApplicationVersion: 1,
            ContractResolutionDisposition.CompletedRedacted);
        Assert.Throws<ArgumentException>(() =>
            new WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent(
                applicationId,
                WorkspaceStaffOnboardingTests.OrganizationId.ToString("D"),
                Now,
                applicationId,
                StaffMemberId,
                workspaceApplicationVersion: 1,
                ContractResolutionDisposition.CompletedRedacted));
    }

    [Fact]
    public void Staff_ready_redacts_the_redundant_applicant_profile_immediately()
    {
        WorkspaceStaffOnboarding application = CreateProvisioningApplication();

        AnchorCoordinates coordinates = BindStaffAnchor(application);

        Assert.Equal(WorkspaceStaffOnboardingState.StaffReady, application.Status);
        Assert.Equal(StaffMemberId, application.StaffMemberId);
        AssertApplicantDataRedacted(application);
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedDomainEvent
            continuation = Assert.IsType<
                WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedDomainEvent>(
                    Assert.Single(application.DomainEvents));
        Assert.NotEqual(application.Id, continuation.EventId);
        Assert.Equal(coordinates.ContinuationEventId, continuation.EventId);
        Assert.Equal(
            coordinates.ResolutionEventId,
            application.IdentityAnchorExpectedResolutionEventId);
    }

    [Fact]
    public void Resolution_intent_replay_uses_the_captured_time_and_emits_once()
    {
        WorkspaceStaffOnboarding application = CreateProvisioningApplication();
        AnchorCoordinates coordinates = BindStaffAnchor(application);
        application.ClearDomainEvents();
        Assert.True(application.Complete(Now.AddMinutes(3)).IsSuccess);
        long capturedVersion = application.Version;
        DateTimeOffset? capturedAt =
            application.IdentityAnchorResolutionIntentAtUtc;

        Assert.True(application.RecordResolutionIntent(
            StaffMemberId,
            DomainResolutionDisposition.CompletedRedacted,
            Now.AddMinutes(30)).IsSuccess);

        Assert.Equal(capturedVersion, application.Version);
        Assert.Equal(capturedAt, application.IdentityAnchorResolutionIntentAtUtc);
        WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent domainEvent =
            Assert.IsType<
                WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent>(
                    Assert.Single(application.DomainEvents));
        Assert.Equal(
            coordinates.ResolutionEventId,
            domainEvent.EventId);
        Assert.NotEqual(application.Id, domainEvent.EventId);
        Assert.Equal(application.Id, domainEvent.ApplicationId);
        Assert.Equal(StaffMemberId, domainEvent.StaffMemberId);
        Assert.Equal(capturedVersion, domainEvent.WorkspaceApplicationVersion);
    }

    [Fact]
    public void Resolution_observation_advances_once_and_preserves_the_captured_version()
    {
        WorkspaceStaffOnboarding application = CreateProvisioningApplication();
        _ = BindStaffAnchor(application);
        application.ClearDomainEvents();
        Assert.True(application.Complete(Now.AddMinutes(3)).IsSuccess);
        long capturedVersion = application.Version;
        DateTimeOffset firstObservedAt = Now.AddMinutes(4);

        Assert.True(application.ObserveResolution(
            application.IdentityAnchorResolutionEventId!.Value,
            StaffMemberId,
            capturedVersion,
            DomainResolutionDisposition.CompletedRedacted,
            firstObservedAt).IsSuccess);
        long observedVersion = application.Version;
        Assert.True(application.ObserveResolution(
            application.IdentityAnchorResolutionEventId!.Value,
            StaffMemberId,
            capturedVersion,
            DomainResolutionDisposition.CompletedRedacted,
            Now.AddMinutes(10)).IsSuccess);

        Assert.Equal(capturedVersion + 1, observedVersion);
        Assert.Equal(observedVersion, application.Version);
        Assert.Equal(
            capturedVersion,
            application.IdentityAnchorResolutionApplicationVersion);
        Assert.Equal(
            firstObservedAt,
            application.IdentityAnchorResolutionObservedAtUtc);
    }

    [Fact]
    public void Resolution_observation_normalizes_a_backward_clock_to_causal_time()
    {
        WorkspaceStaffOnboarding application = CreateProvisioningApplication();
        _ = BindStaffAnchor(application);
        application.ClearDomainEvents();
        Assert.True(application.Complete(Now.AddMinutes(3)).IsSuccess);
        long capturedVersion =
            application.IdentityAnchorResolutionApplicationVersion!.Value;
        DateTimeOffset intentAt =
            application.IdentityAnchorResolutionIntentAtUtc!.Value;

        Assert.True(application.ObserveResolution(
            application.IdentityAnchorResolutionEventId!.Value,
            StaffMemberId,
            capturedVersion,
            DomainResolutionDisposition.CompletedRedacted,
            Now.AddHours(-1)).IsSuccess);

        Assert.Equal(intentAt, application.IdentityAnchorResolutionObservedAtUtc);
        Assert.Equal(intentAt, application.LastChangedAtUtc);
    }

    [Fact]
    public void Resolution_observation_uses_a_later_local_change_as_its_causal_floor()
    {
        WorkspaceStaffOnboarding application = CreateProvisioningApplication();
        _ = BindStaffAnchor(application);
        application.ClearDomainEvents();
        Assert.True(application.Complete(Now.AddMinutes(3)).IsSuccess);
        long capturedVersion =
            application.IdentityAnchorResolutionApplicationVersion!.Value;
        DateTimeOffset laterLocalChange = Now.AddMinutes(8);
        SetPropertyForMaterialization(
            application,
            nameof(WorkspaceStaffOnboarding.LastChangedAtUtc),
            laterLocalChange);
        SetPropertyForMaterialization(
            application,
            nameof(WorkspaceStaffOnboarding.Version),
            capturedVersion + 1);

        Assert.True(application.ObserveResolution(
            application.IdentityAnchorResolutionEventId!.Value,
            StaffMemberId,
            capturedVersion,
            DomainResolutionDisposition.CompletedRedacted,
            Now.AddMinutes(4)).IsSuccess);

        Assert.Equal(
            laterLocalChange,
            application.IdentityAnchorResolutionObservedAtUtc);
        Assert.Equal(capturedVersion + 2, application.Version);
    }

    [Fact]
    public void Undefined_or_divergent_resolution_coordinates_fail_without_mutation()
    {
        WorkspaceStaffOnboarding application = CreateProvisioningApplication();
        _ = BindStaffAnchor(application);
        application.ClearDomainEvents();
        Assert.True(application.Complete(Now.AddMinutes(3)).IsSuccess);
        long capturedVersion =
            application.IdentityAnchorResolutionApplicationVersion!.Value;
        long version = application.Version;

        Assert.True(application.RecordResolutionIntent(
            StaffMemberId,
            (DomainResolutionDisposition)99,
            Now.AddMinutes(4)).IsFailure);
        Assert.True(application.ObserveResolution(
            application.IdentityAnchorResolutionEventId!.Value,
            StaffMemberId,
            capturedVersion + 1,
            DomainResolutionDisposition.CompletedRedacted,
            Now.AddMinutes(4)).IsFailure);
        Assert.True(application.ObserveResolution(
            application.IdentityAnchorResolutionEventId!.Value,
            StaffMemberId,
            capturedVersion,
            DomainResolutionDisposition.SupersededRedacted,
            Now.AddMinutes(4)).IsFailure);
        Assert.True(application.ObserveResolution(
            application.IdentityAnchorResolutionEventId!.Value,
            StaffMemberId,
            capturedVersion,
            (DomainResolutionDisposition)99,
            Now.AddMinutes(4)).IsFailure);

        Assert.Equal(version, application.Version);
        Assert.Null(application.IdentityAnchorResolutionObservedAtUtc);
    }

    [Fact]
    public void Completed_legacy_replay_does_not_manufacture_resolution_intent()
    {
        WorkspaceStaffOnboarding application = CreateProvisioningApplication();
        _ = BindStaffAnchor(application);
        application.ClearDomainEvents();
        SetStatusForLegacyMaterialization(
            application,
            WorkspaceStaffOnboardingState.Completed);
        long version = application.Version;

        Assert.True(application.Complete(Now.AddMinutes(20)).IsSuccess);

        Assert.Equal(version, application.Version);
        Assert.Null(application.IdentityAnchorResolutionEventId);
        Assert.Null(application.IdentityAnchorResolutionIntentAtUtc);
        Assert.Empty(application.DomainEvents);
    }

    [Fact]
    public async Task Resolution_event_projects_one_privacy_minimal_outbox_fact()
    {
        WorkspaceStaffOnboarding application = CreateProvisioningApplication();
        AnchorCoordinates coordinates = BindStaffAnchor(application);
        application.ClearDomainEvents();
        Assert.True(application.Complete(Now.AddMinutes(3)).IsSuccess);
        WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent domainEvent =
            Assert.IsType<
                WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent>(
                    Assert.Single(application.DomainEvents));
        RecordingOutbox outbox = new();

        await new
                WorkspaceStaffOnboardingIdentityAnchorResolutionOutboxProjector(
                    new RecordingOutboxRegistry(outbox))
            .HandleAsync(domainEvent, CancellationToken.None);

        WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent
            integrationEvent = Assert.IsType<
                WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent>(
                    Assert.Single(outbox.Events));
        Assert.Equal(
            coordinates.ResolutionEventId,
            integrationEvent.EventId);
        Assert.NotEqual(application.Id, integrationEvent.EventId);
        Assert.NotEqual(
            coordinates.ContinuationEventId,
            integrationEvent.EventId);
        Assert.Equal(application.Id, integrationEvent.ApplicationId);
        Assert.Equal(StaffMemberId, integrationEvent.StaffMemberId);
        Assert.Equal(
            application.IdentityAnchorResolutionApplicationVersion,
            integrationEvent.WorkspaceApplicationVersion);
        Assert.Equal(
            ContractResolutionDisposition.CompletedRedacted,
            integrationEvent.Disposition);
        string[] forbiddenProperties =
        [
            "SubjectId",
            "DisplayName",
            "LegalName",
            "WorkEmail",
            "WorkPhone",
            "EmployeeNumber",
            "JobTitle",
            "Department"
        ];
        Assert.DoesNotContain(
            integrationEvent.GetType().GetProperties(),
            property => forbiddenProperties.Contains(
                property.Name,
                StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Repeated_anchor_convergence_is_idempotent_for_the_persisted_supplied_coordinates()
    {
        Guid applicationId = Guid.NewGuid();
        WorkspaceStaffOnboarding application = CreateProvisioningApplication(
            applicationId);
        AnchorCoordinates coordinates = NewAnchorCoordinates(applicationId);

        Assert.True(application.ConvergeCommittedStaffAnchor(
            StaffMemberId,
            coordinates.ResolutionEventId,
            coordinates.ContinuationEventId,
            Now.AddMinutes(2)).IsSuccess);
        long convergedVersion = application.Version;
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedDomainEvent
            continuation = Assert.IsType<
                WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedDomainEvent>(
                    Assert.Single(application.DomainEvents));
        application.ClearDomainEvents();

        Assert.True(application.ConvergeCommittedStaffAnchor(
            StaffMemberId,
            coordinates.ResolutionEventId,
            coordinates.ContinuationEventId,
            Now.AddMinutes(20)).IsSuccess);

        Assert.Equal(convergedVersion, application.Version);
        Assert.Equal(
            coordinates.ResolutionEventId,
            application.IdentityAnchorExpectedResolutionEventId);
        Assert.Equal(
            coordinates.ContinuationEventId,
            application.IdentityAnchorContinuationEventId);
        Assert.Equal(coordinates.ContinuationEventId, continuation.EventId);
        Assert.Empty(application.DomainEvents);

        RecordingOutbox outbox = new();
        await new
                WorkspaceStaffOnboardingIdentityAnchorContinuationOutboxProjector(
                    new RecordingOutboxRegistry(outbox))
            .HandleAsync(continuation, CancellationToken.None);
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
            integrationEvent = Assert.IsType<
                WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent>(
                    Assert.Single(outbox.Events));
        Assert.Equal(continuation.EventId, integrationEvent.EventId);
        Assert.Equal(applicationId, integrationEvent.ApplicationId);
        Assert.Equal(StaffMemberId, integrationEvent.StaffMemberId);
    }

    private static AnchorCoordinates BindStaffAnchor(
        WorkspaceStaffOnboarding application)
    {
        AnchorCoordinates coordinates = NewAnchorCoordinates(application.Id);
        Assert.True(application.MarkStaffReady(
            StaffMemberId,
            coordinates.ResolutionEventId,
            coordinates.ContinuationEventId,
            Now.AddMinutes(2)).IsSuccess);
        return coordinates;
    }

    private static AnchorCoordinates NewAnchorCoordinates(
        Guid applicationId)
    {
        Guid resolutionEventId = NewDistinctId(applicationId);
        Guid continuationEventId = NewDistinctId(
            applicationId,
            resolutionEventId);
        return new(resolutionEventId, continuationEventId);
    }

    private static Guid NewDistinctId(params Guid[] excluded)
    {
        Guid candidate;
        do
        {
            candidate = Guid.NewGuid();
        }
        while (candidate == Guid.Empty || excluded.Contains(candidate));

        return candidate;
    }

    private static WorkspaceStaffOnboarding CreateProvisioningApplication(
        Guid? applicationId = null)
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboarding.Create(
            applicationId ?? Guid.NewGuid(),
            WorkspaceStaffOnboardingTests.OrganizationId.ToString("D"),
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            Guid.NewGuid(),
            WorkspaceStaffOnboardingTests.SubjectId,
            "verified@example.test",
            "Ada Operator",
            "Ada Lovelace",
            "ada@workspace.test",
            "+1 555 0100",
            "EMP-100",
            "Manager",
            "Operations",
            Now).Value;
        Assert.True(application.ObserveClaimAccepted(
            Guid.NewGuid(),
            1,
            Now.AddMinutes(1)).IsSuccess);
        return application;
    }

    private static void SetStatusForLegacyMaterialization(
        WorkspaceStaffOnboarding application,
        WorkspaceStaffOnboardingState status) =>
        SetPropertyForMaterialization(
            application,
            nameof(WorkspaceStaffOnboarding.Status),
            status);

    private static void SetPropertyForMaterialization(
        WorkspaceStaffOnboarding application,
        string propertyName,
        object value) =>
        typeof(WorkspaceStaffOnboarding)
            .GetProperty(
                propertyName,
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic)!
            .SetValue(application, value);

    private static void AssertApplicantDataRedacted(
        WorkspaceStaffOnboarding application)
    {
        Assert.Null(application.VerifiedAccountEmail);
        Assert.Null(application.DisplayName);
        Assert.Null(application.LegalName);
        Assert.Null(application.WorkEmail);
        Assert.Null(application.WorkPhone);
        Assert.Null(application.EmployeeNumber);
        Assert.Null(application.JobTitle);
        Assert.Null(application.Department);
    }

    private sealed class RecordingOutbox : IOutboxWriter
    {
        public string ModuleName => WorkspacesModuleMetadata.Name;
        public List<IIntegrationEvent> Events { get; } = [];

        public Task EnqueueAsync<TEvent>(
            TEvent integrationEvent,
            CancellationToken cancellationToken)
            where TEvent : IIntegrationEvent
        {
            this.Events.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOutboxRegistry(RecordingOutbox outbox)
        : IOutboxWriterRegistry
    {
        public IOutboxWriter GetRequired(string moduleName)
        {
            Assert.Equal(WorkspacesModuleMetadata.Name, moduleName);
            return outbox;
        }
    }

    private readonly record struct AnchorCoordinates(
        Guid ResolutionEventId,
        Guid ContinuationEventId);

    private static readonly Guid StaffMemberId =
        Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset Now =
        WorkspaceStaffOnboardingTests.Now;
}
