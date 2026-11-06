namespace BunkFy.Extensions.Operations.Notifications.Tests;

using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.Messaging;
using Gma.Framework.Notifications;
using Gma.Framework.Tenancy;
using Gma.Modules.Notifications.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using ContractNotificationSeverity =
    Gma.Modules.Notifications.Contracts.NotificationSeverity;

[Trait("Category", "Unit")]
public sealed class OperationalNotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid OrganizationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly string ScopeId = OrganizationId.ToString("D");

    [Fact]
    public void Registration_preserves_tenant_scope_metadata_for_every_operational_handler()
    {
        ServiceCollection services = [];

        services.AddBunkFyOperationsNotifications();
        services.AddBunkFyOperationsIngestionNotifications();

        IntegrationEventSubscription[] subscriptions = services
            .Where(descriptor => descriptor.ServiceType == typeof(IntegrationEventSubscription))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<IntegrationEventSubscription>()
            .ToArray();
        Assert.Equal(14, subscriptions.Length);
        Assert.All(subscriptions, subscription => Assert.True(subscription.IsTenantScoped()));
    }

    [Fact]
    public void Provider_bridge_is_registered_only_with_the_ingestion_extension()
    {
        ServiceCollection services = [];

        services.AddBunkFyOperationsNotifications();

        Assert.DoesNotContain(
            services,
            descriptor =>
                descriptor.ServiceType ==
                typeof(
                    ExternalReservationOperationAttentionNotificationHandler));

        services.AddBunkFyOperationsIngestionNotifications();

        Assert.Single(
            services,
            descriptor =>
                descriptor.ServiceType ==
                typeof(
                    ExternalReservationOperationAttentionNotificationHandler));
    }

    [Fact]
    public async Task Property_event_fans_out_to_active_staff_and_workspace_owners_with_stable_distinct_ids()
    {
        var audience = new TestAudienceReader(["user-a", "user-b"]);
        var workspaceOwners = new TestWorkspaceOwnerAudienceReader(["owner-a", "user-b"]);
        var access = new TestOrganizationAccessCandidateFilter();
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            audience,
            workspaceOwners,
            access,
            notifications);
        var handler = new ReservationCancelledNotificationHandler(projector);
        Guid sourceEventId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid reservationId =
            Guid.Parse("22222222-2222-2222-2222-222222222222");
        Guid propertyId =
            Guid.Parse("33333333-3333-3333-3333-333333333333");
        var integrationEvent = new ReservationCancelledIntegrationEvent(
            sourceEventId,
            ScopeId,
            Now,
            reservationId,
            propertyId,
            3);

        await handler.HandleAsync(integrationEvent, CancellationToken.None);

        Assert.Equal(3, notifications.Events.Count);
        Assert.Equal(["owner-a", "user-a", "user-b"], notifications.Events.Select(item => item.UserId).ToArray());
        Assert.Equal(3, notifications.Events.Select(item => item.EventId).Distinct().Count());
        Assert.All(notifications.Events, item =>
        {
            Assert.Equal(
                Gma.Modules.Notifications.Contracts.NotificationDeliveryPolicy.RespectPreferences,
                item.DeliveryPolicy);
            Assert.Contains(item.Tags, tag => tag.Key == NotificationTags.Web);
            Assert.Contains(item.Tags, tag => tag.Key == "domain:reservations");
            Assert.Equal(
                [
                    OperationsNotificationsDataRightsCoordinates
                        .ForReservation(
                            ScopeId,
                            propertyId,
                            reservationId),
                    OperationsNotificationsDataRightsCoordinates
                        .ForStaff(
                            ScopeId,
                            StaffMemberIdFor(item.UserId)),
                    OperationsNotificationsDataRightsCoordinates
                        .ForTenant(ScopeId)
                ],
                item.References);
        });
        Assert.Equal(
            notifications.Events[0].EventId,
            OperationalNotificationProjector.CreateNotificationId(
                sourceEventId,
                "owner-a",
                "reservation-cancelled"));
        Assert.Equal([["owner-a", "user-a", "user-b"]], access.Requests);
    }

    [Fact]
    public async Task Property_event_fanout_requires_the_destination_domain_read_permission()
    {
        TestAuthorizationService authorization = new(["user-a"]);
        CapturingProjector notifications = new();
        OperationalNotificationProjector projector = CreateProjector(
            new TestAudienceReader(["user-a", "user-b"]),
            new TestWorkspaceOwnerAudienceReader(["owner-a"]),
            new TestOrganizationAccessCandidateFilter(),
            notifications,
            authorization: authorization);
        Guid propertyId = Guid.NewGuid();

        await new ReservationCancelledNotificationHandler(projector)
            .HandleAsync(
                new ReservationCancelledIntegrationEvent(
                    Guid.NewGuid(),
                    ScopeId,
                    Now,
                    Guid.NewGuid(),
                    propertyId,
                    3),
                CancellationToken.None);

        UserNotificationRequestedIntegrationEventV3 projected =
            Assert.Single(notifications.Events);
        Assert.Equal("user-a", projected.UserId);
        AccessRequirement[] requirements = Assert.Single(
            authorization.Requests);
        Assert.Equal(3, requirements.Length);
        Assert.All(requirements, requirement =>
        {
            Assert.Equal(
                ReservationsAdminPermissionCodes.Read,
                requirement.Permission.Value);
            Assert.Equal(
                WorkspaceAccessScopes.CreateProperty(ScopeId, propertyId),
                requirement.Scope);
        });
    }

    [Fact]
    public void Audience_permission_catalog_matches_notification_destinations()
    {
        Assert.Equal(
            PropertiesAdminPermissionCodes.Read,
            OperationalNotificationAudiencePermissions.PropertiesRead.Value);
        Assert.Equal(
            InventoryAdminPermissionCodes.Read,
            OperationalNotificationAudiencePermissions.InventoryRead.Value);
        Assert.Equal(
            ReservationsAdminPermissionCodes.Read,
            OperationalNotificationAudiencePermissions.ReservationsRead.Value);
        Assert.Equal(
            IngestionAdminPermissionCodes.Read,
            OperationalNotificationAudiencePermissions.IngestionRead.Value);
        Assert.Equal(
            DataRightsAdminPermissionCodes.Read,
            OperationalNotificationAudiencePermissions.DataRightsRead.Value);
    }

    [Fact]
    public void Operational_delivery_catalog_remains_web_only_until_delivery_time_reauthorization_exists()
    {
        IReadOnlyList<NotificationTag>[] tagSets =
        [
            BunkFyNotificationTags.PropertyActivity,
            BunkFyNotificationTags.InventoryActivity,
            BunkFyNotificationTags.ReservationActivity,
            BunkFyNotificationTags.ProviderAttention,
            BunkFyNotificationTags.StaffActivity,
            BunkFyNotificationTags.DataRightsAttention
        ];

        Assert.All(tagSets, tags =>
            Assert.Equal(
                [NotificationTags.Web],
                tags
                    .Where(tag =>
                        tag.Kind == NotificationTagKind.Delivery)
                    .Select(tag => tag.Key)
                    .ToArray()));
    }

    [Fact]
    public async Task Successful_provider_operation_does_not_create_an_attention_notification()
    {
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader(["user-a"]),
            new TestWorkspaceOwnerAudienceReader(["owner-a"]),
            new TestOrganizationAccessCandidateFilter(),
            notifications);
        var sourceLinks = new TestIngestionSourceLinkResolver();
        var handler = new ExternalReservationOperationAttentionNotificationHandler(
            projector,
            sourceLinks);
        var integrationEvent = new ExternalReservationOperationCompletedIntegrationEvent(
            Guid.NewGuid(),
            ScopeId,
            Now,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            ExternalReservationOperationKind.Amend,
            ExternalReservationOperationOutcome.Applied,
            Guid.NewGuid(),
            2,
            3,
            null);

        await handler.HandleAsync(integrationEvent, CancellationToken.None);

        Assert.Empty(notifications.Events);
        Assert.Empty(sourceLinks.Requests);
    }

    [Fact]
    public async Task Arrival_reminder_omits_guest_identity_and_keeps_exact_reservation_navigation_payload()
    {
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader(["user-a"]),
            new TestWorkspaceOwnerAudienceReader([]),
            new TestOrganizationAccessCandidateFilter(),
            notifications);
        var handler = new ReservationArrivalReminderV2NotificationHandler(projector);
        Guid reservationId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();
        var integrationEvent = new ReservationArrivalReminderDueIntegrationEventV2(
            Guid.NewGuid(),
            ScopeId,
            Now,
            reservationId,
            propertyId,
            new DateOnly(2026, 7, 16),
            new TimeOnly(15, 30),
            "Europe/Moscow",
            3);

        await handler.HandleAsync(integrationEvent, CancellationToken.None);

        UserNotificationRequestedIntegrationEventV3 notification = Assert.Single(notifications.Events);
        Assert.Equal("reservation-arrival-soon", notification.NotificationName);
        Assert.DoesNotContain("Maya Chen", notification.Body, StringComparison.Ordinal);
        Assert.Contains("A reservation", notification.Body, StringComparison.Ordinal);
        Assert.Contains("15:30", notification.Body, StringComparison.Ordinal);
        Assert.Equal(["PropertyId", "ReservationId"], JsonProperties(notification.PayloadJson));
        Assert.Contains(reservationId.ToString(), notification.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(propertyId.ToString(), notification.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Legacy_arrival_reminder_projects_the_same_minimized_notification()
    {
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader(["user-a"]),
            new TestWorkspaceOwnerAudienceReader([]),
            new TestOrganizationAccessCandidateFilter(),
            notifications);
        var handler = new ReservationArrivalReminderNotificationHandler(projector);
        var integrationEvent = new ReservationArrivalReminderDueIntegrationEvent(
            Guid.NewGuid(),
            ScopeId,
            Now,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateOnly(2026, 7, 16),
            new TimeOnly(15, 30),
            "Europe/Moscow",
            3);

        await handler.HandleAsync(integrationEvent, CancellationToken.None);

        UserNotificationRequestedIntegrationEventV3 notification = Assert.Single(notifications.Events);
        Assert.Equal("A reservation is expected at 15:30 on Jul 16.", notification.Body);
    }

    [Theory]
    [InlineData(
        DataRightsResponseDeadlineAlertKind.DueSoon,
        DataRightsResponseDeadlineNotificationHandler.DueSoonNotificationName,
        ContractNotificationSeverity.Warning)]
    [InlineData(
        DataRightsResponseDeadlineAlertKind.Overdue,
        DataRightsResponseDeadlineNotificationHandler.OverdueNotificationName,
        ContractNotificationSeverity.Error)]
    public async Task Deadline_alert_is_mandatory_minimized_and_limited_to_data_rights_readers(
        DataRightsResponseDeadlineAlertKind alertKind,
        string expectedName,
        ContractNotificationSeverity expectedSeverity)
    {
        Guid propertyId = Guid.NewGuid();
        Guid caseId = Guid.NewGuid();
        TestAuthorizationService authorization = new(["privacy-reader"]);
        CapturingProjector notifications = new();
        OperationalNotificationProjector projector = CreateProjector(
            new TestAudienceReader(["privacy-reader", "front-desk"]),
            new TestWorkspaceOwnerAudienceReader(["owner-a"]),
            new TestOrganizationAccessCandidateFilter(),
            notifications,
            authorization: authorization);
        DataRightsResponseDeadlineNotificationHandler handler = new(projector);
        DataRightsResponseDeadlineAlertDueIntegrationEvent integrationEvent = new(
            Guid.NewGuid(),
            ScopeId,
            Now,
            caseId,
            propertyId,
            alertKind,
            alertKind == DataRightsResponseDeadlineAlertKind.DueSoon
                ? Now.AddHours(24)
                : Now.AddHours(-1));

        await handler.HandleAsync(integrationEvent, CancellationToken.None);

        UserNotificationRequestedIntegrationEventV3 notification =
            Assert.Single(notifications.Events);
        Assert.Equal("privacy-reader", notification.UserId);
        Assert.Equal(expectedName, notification.NotificationName);
        Assert.Equal(expectedSeverity, notification.Severity);
        Assert.Equal(
            Gma.Modules.Notifications.Contracts.NotificationDeliveryPolicy
                .Mandatory,
            notification.DeliveryPolicy);
        Assert.Equal(["CaseId", "PropertyId"], JsonProperties(notification.PayloadJson));
        Assert.DoesNotContain(
            "requester",
            notification.PayloadJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "rights",
            notification.PayloadJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            notification.Tags,
            tag => tag.Key == "domain:data-rights");
        AccessRequirement[] requirements = Assert.Single(
            authorization.Requests);
        Assert.Equal(3, requirements.Length);
        Assert.All(requirements, requirement =>
        {
            Assert.Equal(
                DataRightsAdminPermissionCodes.Read,
                requirement.Permission.Value);
            Assert.Equal(
                WorkspaceAccessScopes.CreateProperty(ScopeId, propertyId),
                requirement.Scope);
        });
    }

    [Fact]
    public async Task Property_event_excludes_the_initiating_user_without_suppressing_other_recipients()
    {
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader(["user-a", "user-b"]),
            new TestWorkspaceOwnerAudienceReader(["owner-a"]),
            new TestOrganizationAccessCandidateFilter(),
            notifications);
        var handler = new ReservationCancelledNotificationHandler(projector);
        var integrationEvent = new ReservationCancelledIntegrationEvent(
            Guid.NewGuid(),
            ScopeId,
            Now,
            Guid.NewGuid(),
            Guid.NewGuid(),
            3,
            "user:user-a");

        await handler.HandleAsync(integrationEvent, CancellationToken.None);

        Assert.Equal(["owner-a", "user-b"], notifications.Events.Select(item => item.UserId).ToArray());
    }

    [Fact]
    public async Task Property_event_filters_every_candidate_through_authoritative_active_membership()
    {
        var access = new TestOrganizationAccessCandidateFilter(["user-a"]);
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader(["user-a", "stale-staff"]),
            new TestWorkspaceOwnerAudienceReader(["stale-owner"]),
            access,
            notifications);
        var handler = new ReservationCancelledNotificationHandler(projector);

        await handler.HandleAsync(
            new ReservationCancelledIntegrationEvent(
                Guid.NewGuid(), ScopeId, Now, Guid.NewGuid(), Guid.NewGuid(), 3),
            CancellationToken.None);

        Assert.Equal(["user-a"], notifications.Events.Select(item => item.UserId).ToArray());
        Assert.Equal([["stale-owner", "stale-staff", "user-a"]], access.Requests);
    }

    [Fact]
    public async Task Membership_authority_queries_are_candidate_bounded()
    {
        string[] candidates = Enumerable.Range(0, 1001)
            .Select(index => $"user-{index:D4}")
            .ToArray();
        var access = new TestOrganizationAccessCandidateFilter();
        var recipientResolver = new TestRecipientResolver();
        var authorization = new TestAuthorizationService();
        var projector = CreateProjector(
            new TestAudienceReader(candidates),
            new TestWorkspaceOwnerAudienceReader([]),
            access,
            new CapturingProjector(),
            recipientResolver,
            authorization);

        await new ReservationCancelledNotificationHandler(projector).HandleAsync(
            new ReservationCancelledIntegrationEvent(
                Guid.NewGuid(), ScopeId, Now, Guid.NewGuid(), Guid.NewGuid(), 3),
            CancellationToken.None);

        Assert.Equal([500, 500, 1], access.Requests.Select(request => request.Count).ToArray());
        Assert.Equal(
            [500, 500, 1],
            authorization.Requests
                .Select(request => request.Length)
                .ToArray());
        Assert.Equal(
            [200, 200, 200, 200, 200, 1],
            recipientResolver.Requests.Select(request => request.Count).ToArray());
    }

    [Fact]
    public async Task Membership_authority_failure_propagates_without_projecting_notifications()
    {
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader(["user-a"]),
            new TestWorkspaceOwnerAudienceReader([]),
            new ThrowingOrganizationAccessCandidateFilter(),
            notifications);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ReservationCancelledNotificationHandler(projector).HandleAsync(
                new ReservationCancelledIntegrationEvent(
                    Guid.NewGuid(),
                    ScopeId,
                    Now,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    3),
                CancellationToken.None));

        Assert.Empty(notifications.Events);
    }

    [Fact]
    public async Task Authorization_authority_failure_propagates_without_projecting_notifications()
    {
        CapturingProjector notifications = new();
        OperationalNotificationProjector projector = CreateProjector(
            new TestAudienceReader(["user-a"]),
            new TestWorkspaceOwnerAudienceReader([]),
            new TestOrganizationAccessCandidateFilter(),
            notifications,
            authorization: new ThrowingAuthorizationService());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ReservationCancelledNotificationHandler(projector)
                .HandleAsync(
                    new ReservationCancelledIntegrationEvent(
                        Guid.NewGuid(),
                        ScopeId,
                        Now,
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        3),
                    CancellationToken.None));

        Assert.Empty(notifications.Events);
    }

    [Fact]
    public async Task Invalid_product_scope_fails_before_any_notification_is_projected()
    {
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader(["user-a"]),
            new TestWorkspaceOwnerAudienceReader([]),
            new TestOrganizationAccessCandidateFilter(),
            notifications);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ReservationCancelledNotificationHandler(projector).HandleAsync(
                new ReservationCancelledIntegrationEvent(
                    Guid.NewGuid(), "not-an-organization", Now, Guid.NewGuid(), Guid.NewGuid(), 3),
                CancellationToken.None));
        Assert.Empty(notifications.Events);
    }

    [Fact]
    public async Task Free_text_and_technical_source_values_do_not_enter_notification_content()
    {
        const string sensitive = "SENSITIVE provider or operator text";
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader(["user-a"]),
            new TestWorkspaceOwnerAudienceReader([]),
            new TestOrganizationAccessCandidateFilter(),
            notifications);
        Guid propertyId = Guid.NewGuid();

        await new ManualInventoryBlockCreatedNotificationHandler(projector).HandleAsync(
            new ManualInventoryBlockCreatedIntegrationEvent(
                Guid.NewGuid(), ScopeId, Now, Guid.NewGuid(), Guid.NewGuid(), propertyId,
                Guid.NewGuid(), new DateOnly(2026, 7, 14), new DateOnly(2026, 7, 16),
                2, "system:source-actor"),
            CancellationToken.None);
        await new ExternalReservationOperationAttentionNotificationHandler(
            projector,
            new TestIngestionSourceLinkResolver()).HandleAsync(
            new ExternalReservationOperationCompletedIntegrationEvent(
                Guid.NewGuid(), ScopeId, Now, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                propertyId, ExternalReservationOperationKind.Amend,
                ExternalReservationOperationOutcome.ValidationRejected, Guid.NewGuid(), 2, 3, sensitive),
            CancellationToken.None);

        Assert.Equal(2, notifications.Events.Count);
        Assert.All(notifications.Events, notification =>
        {
            Assert.DoesNotContain(sensitive, notification.Title, StringComparison.Ordinal);
            Assert.DoesNotContain(sensitive, notification.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(sensitive, notification.PayloadJson, StringComparison.Ordinal);
            Assert.DoesNotContain("source-actor", notification.PayloadJson, StringComparison.Ordinal);
        });
        Assert.Equal(
            ["Arrival", "BlockGroupId", "Departure", "PropertyId"],
            JsonProperties(notifications.Events[0].PayloadJson));
        Assert.Equal(
            ["ConnectionId", "PropertyId", "ReceiptId", "ReservationId"],
            JsonProperties(notifications.Events[1].PayloadJson));
    }

    [Fact]
    public async Task Provider_attention_resolves_source_link_once_before_fan_out()
    {
        Guid sourceLinkId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();
        Guid connectionId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        Guid receiptId = Guid.NewGuid();
        Guid reservationId = Guid.NewGuid();
        var sourceLinks =
            new TestIngestionSourceLinkResolver(sourceLinkId);
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader(["user-a", "user-b"]),
            new TestWorkspaceOwnerAudienceReader([]),
            new TestOrganizationAccessCandidateFilter(),
            notifications);

        await new ExternalReservationOperationAttentionNotificationHandler(
                projector,
                sourceLinks)
            .HandleAsync(
                new ExternalReservationOperationCompletedIntegrationEvent(
                    Guid.NewGuid(),
                    ScopeId,
                    Now,
                    operationId,
                    receiptId,
                    connectionId,
                    propertyId,
                    ExternalReservationOperationKind.Amend,
                    ExternalReservationOperationOutcome.OperationConflict,
                    reservationId,
                    2,
                    3,
                    null),
                CancellationToken.None);

        Assert.Equal(
            [
                new SourceLinkResolutionRequest(
                    ScopeId,
                    propertyId,
                    connectionId,
                    operationId,
                    receiptId)
            ],
            sourceLinks.Requests);
        Assert.Equal(2, notifications.Events.Count);
        Assert.All(notifications.Events, notification =>
        {
            Assert.Contains(
                OperationsNotificationsDataRightsCoordinates
                    .ForIngestionSourceLink(
                        ScopeId,
                        propertyId,
                        sourceLinkId),
                notification.References);
            Assert.Contains(
                OperationsNotificationsDataRightsCoordinates
                    .ForReservation(
                        ScopeId,
                        propertyId,
                        reservationId),
                notification.References);
        });
    }

    [Fact]
    public async Task Provider_attention_fails_before_projection_when_source_link_is_missing()
    {
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader(["user-a"]),
            new TestWorkspaceOwnerAudienceReader([]),
            new TestOrganizationAccessCandidateFilter(),
            notifications);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ExternalReservationOperationAttentionNotificationHandler(
                    projector,
                    new TestIngestionSourceLinkResolver(
                        returnMissing: true))
                .HandleAsync(
                    new ExternalReservationOperationCompletedIntegrationEvent(
                        Guid.NewGuid(),
                        ScopeId,
                        Now,
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        ExternalReservationOperationKind.Create,
                        ExternalReservationOperationOutcome.ValidationRejected,
                        reservationId: null,
                        detailsRevision: null,
                        reservationVersion: null,
                        errorCode: null),
                    CancellationToken.None));

        Assert.Empty(notifications.Events);
    }

    [Theory]
    [InlineData("service:user-a")]
    [InlineData("system:user-a")]
    [InlineData("admin-actor:user-a")]
    [InlineData(null)]
    public void Non_user_actors_are_not_treated_as_inbox_recipients(string? actorId)
    {
        Assert.False(OperationalNotificationProjector.IsInitiatingUser("user-a", actorId));
    }

    [Fact]
    public async Task Staff_event_is_quiet_when_the_profile_has_no_auth_subject()
    {
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader([], staffAuthSubjectId: null),
            new TestWorkspaceOwnerAudienceReader(["owner-a"]),
            new TestOrganizationAccessCandidateFilter(),
            notifications);
        var handler = new StaffMemberLifecycleChangedNotificationHandler(projector);
        var integrationEvent = new StaffMemberLifecycleChangedIntegrationEvent(
            Guid.NewGuid(),
            ScopeId,
            Now,
            Guid.NewGuid(),
            StaffStatus.Suspended,
            new DateOnly(2026, 7, 13),
            2);

        await handler.HandleAsync(integrationEvent, CancellationToken.None);

        Assert.Empty(notifications.Events);
    }

    [Fact]
    public async Task Staff_event_carries_the_known_staff_history_reference()
    {
        Guid staffMemberId = Guid.NewGuid();
        var recipientResolver = new TestRecipientResolver(
            staffMemberIds: new Dictionary<string, Guid>
            {
                ["user-a"] = staffMemberId
            });
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader([], staffAuthSubjectId: "user-a"),
            new TestWorkspaceOwnerAudienceReader([]),
            new TestOrganizationAccessCandidateFilter(),
            notifications,
            recipientResolver);
        var handler =
            new StaffMemberLifecycleChangedNotificationHandler(projector);

        await handler.HandleAsync(
            new StaffMemberLifecycleChangedIntegrationEvent(
                Guid.NewGuid(),
                ScopeId,
                Now,
                staffMemberId,
                StaffStatus.Active,
                new DateOnly(2026, 7, 13),
                2),
            CancellationToken.None);

        UserNotificationRequestedIntegrationEventV3 projected =
            Assert.Single(notifications.Events);
        Assert.Equal(
            [
                OperationsNotificationsDataRightsCoordinates.ForStaff(
                    ScopeId,
                    staffMemberId),
                OperationsNotificationsDataRightsCoordinates.ForTenant(
                    ScopeId)
            ],
            projected.References);
        Assert.Equal([["user-a"]], recipientResolver.Requests);
    }

    [Fact]
    public async Task Property_event_skips_a_missing_current_staff_correlation_without_suppressing_valid_recipients()
    {
        var logger = new CapturingLogger<OperationalNotificationProjector>();
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader(["user-a", "user-b"]),
            new TestWorkspaceOwnerAudienceReader([]),
            new TestOrganizationAccessCandidateFilter(),
            notifications,
            new TestRecipientResolver(["user-a"]),
            logger: logger);

        await new ReservationCancelledNotificationHandler(projector)
            .HandleAsync(
                new ReservationCancelledIntegrationEvent(
                    Guid.NewGuid(),
                    ScopeId,
                    Now,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    3),
                CancellationToken.None);

        Assert.Equal(["user-b"], notifications.Events.Select(item => item.UserId));
        CapturedLog warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(7101, warning.EventId.Id);
        Assert.Contains("reservation-cancelled", warning.Message, StringComparison.Ordinal);
        Assert.Contains("skipped 1 candidate", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("user-a", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ScopeId, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Property_event_is_quiet_when_every_authorized_candidate_lacks_a_current_staff_correlation()
    {
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader(["user-a"]),
            new TestWorkspaceOwnerAudienceReader([]),
            new TestOrganizationAccessCandidateFilter(),
            notifications,
            new TestRecipientResolver(["user-a"]));

        await new ReservationCancelledNotificationHandler(projector)
            .HandleAsync(
                new ReservationCancelledIntegrationEvent(
                    Guid.NewGuid(),
                    ScopeId,
                    Now,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    3),
                CancellationToken.None);

        Assert.Empty(notifications.Events);
    }

    [Fact]
    public async Task Property_event_fails_when_the_recipient_resolver_returns_an_unexpected_subject()
    {
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader(["user-a"]),
            new TestWorkspaceOwnerAudienceReader([]),
            new TestOrganizationAccessCandidateFilter(),
            notifications,
            new TestRecipientResolver(
                resolve: _ =>
                [
                    new StaffNotificationRecipient(
                        Guid.NewGuid(),
                        "unexpected-user")
                ]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HandleReservationCancellationAsync(projector));

        Assert.Empty(notifications.Events);
    }

    [Fact]
    public async Task Property_event_fails_when_the_recipient_resolver_returns_a_malformed_subject()
    {
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader(["user-a"]),
            new TestWorkspaceOwnerAudienceReader([]),
            new TestOrganizationAccessCandidateFilter(),
            notifications,
            new TestRecipientResolver(
                resolve: _ =>
                [new StaffNotificationRecipient(Guid.NewGuid(), " ")]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HandleReservationCancellationAsync(projector));

        Assert.Empty(notifications.Events);
    }

    [Fact]
    public async Task Property_event_fails_when_the_recipient_resolver_returns_a_duplicate_subject()
    {
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader(["user-a"]),
            new TestWorkspaceOwnerAudienceReader([]),
            new TestOrganizationAccessCandidateFilter(),
            notifications,
            new TestRecipientResolver(
                resolve: _ =>
                [
                    new StaffNotificationRecipient(Guid.NewGuid(), "user-a"),
                    new StaffNotificationRecipient(Guid.NewGuid(), "user-a")
                ]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HandleReservationCancellationAsync(projector));

        Assert.Empty(notifications.Events);
    }

    [Fact]
    public async Task Property_event_fails_when_the_recipient_resolver_returns_a_duplicate_staff_id()
    {
        Guid staffMemberId = Guid.NewGuid();
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader(["user-a", "user-b"]),
            new TestWorkspaceOwnerAudienceReader([]),
            new TestOrganizationAccessCandidateFilter(),
            notifications,
            new TestRecipientResolver(
                resolve: _ =>
                [
                    new StaffNotificationRecipient(staffMemberId, "user-a"),
                    new StaffNotificationRecipient(staffMemberId, "user-b")
                ]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HandleReservationCancellationAsync(projector));

        Assert.Empty(notifications.Events);
    }

    [Fact]
    public async Task Property_event_fails_when_the_recipient_resolver_returns_an_empty_staff_id()
    {
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader(["user-a"]),
            new TestWorkspaceOwnerAudienceReader([]),
            new TestOrganizationAccessCandidateFilter(),
            notifications,
            new TestRecipientResolver(
                resolve: _ =>
                [new StaffNotificationRecipient(Guid.Empty, "user-a")]));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HandleReservationCancellationAsync(projector));

        Assert.Empty(notifications.Events);
    }

    [Fact]
    public async Task Recipient_resolver_failure_propagates_without_projecting_notifications()
    {
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader(["user-a"]),
            new TestWorkspaceOwnerAudienceReader([]),
            new TestOrganizationAccessCandidateFilter(),
            notifications,
            new TestRecipientResolver(
                failure: new InvalidOperationException(
                    "recipient authority unavailable")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HandleReservationCancellationAsync(projector));

        Assert.Empty(notifications.Events);
    }

    [Fact]
    public async Task Staff_event_is_quiet_when_the_current_recipient_correlation_disappears()
    {
        Guid staffMemberId = Guid.NewGuid();
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader([], staffAuthSubjectId: "user-a"),
            new TestWorkspaceOwnerAudienceReader([]),
            new TestOrganizationAccessCandidateFilter(),
            notifications,
            new TestRecipientResolver(["user-a"]));

        await new StaffMemberLifecycleChangedNotificationHandler(projector)
            .HandleAsync(
                new StaffMemberLifecycleChangedIntegrationEvent(
                    Guid.NewGuid(),
                    ScopeId,
                    Now,
                    staffMemberId,
                    StaffStatus.Suspended,
                    new DateOnly(2026, 7, 13),
                    2),
                CancellationToken.None);

        Assert.Empty(notifications.Events);
    }

    [Fact]
    public async Task Staff_event_fails_when_the_recipient_correlation_points_to_another_staff_member()
    {
        var notifications = new CapturingProjector();
        var projector = CreateProjector(
            new TestAudienceReader([], staffAuthSubjectId: "user-a"),
            new TestWorkspaceOwnerAudienceReader([]),
            new TestOrganizationAccessCandidateFilter(),
            notifications);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new StaffMemberLifecycleChangedNotificationHandler(projector)
                .HandleAsync(
                    new StaffMemberLifecycleChangedIntegrationEvent(
                        Guid.NewGuid(),
                        ScopeId,
                        Now,
                        Guid.NewGuid(),
                        StaffStatus.Active,
                        new DateOnly(2026, 7, 13),
                        2),
                    CancellationToken.None));

        Assert.Empty(notifications.Events);
    }

    private static Task HandleReservationCancellationAsync(
        OperationalNotificationProjector projector) =>
        new ReservationCancelledNotificationHandler(projector)
            .HandleAsync(
                new ReservationCancelledIntegrationEvent(
                    Guid.NewGuid(),
                    ScopeId,
                    Now,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    3),
                CancellationToken.None);

    private static OperationalNotificationProjector CreateProjector(
        IStaffPropertyAudienceReader audience,
        IWorkspaceOwnerNotificationAudienceReader workspaceOwners,
        IOrganizationAccessCandidateFilter access,
        IUserNotificationRequestProjectorV3 notifications,
        IStaffNotificationRecipientResolver? recipientResolver = null,
        IAccessAuthorizationService? authorization = null,
        ILogger<OperationalNotificationProjector>? logger = null) =>
        new(
            audience,
            recipientResolver ?? new TestRecipientResolver(),
            workspaceOwners,
            access,
            authorization ?? new TestAuthorizationService(),
            notifications,
            logger ?? new CapturingLogger<OperationalNotificationProjector>());

    private static Guid StaffMemberIdFor(string authSubjectId) =>
        OperationalNotificationProjector.CreateNotificationId(
            Guid.Empty,
            authSubjectId,
            "staff-member");

    private static string[] JsonProperties(string payloadJson)
    {
        using JsonDocument document = JsonDocument.Parse(payloadJson);
        return document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private sealed class TestAudienceReader(
        IReadOnlyList<string> propertyRecipients,
        string? staffAuthSubjectId = "user-a") : IStaffPropertyAudienceReader
    {
        public Task<IReadOnlyList<string>> ListActiveAuthSubjectIdsAsync(
            string scopeId,
            Guid propertyId,
            CancellationToken cancellationToken) =>
            Task.FromResult(propertyRecipients);

        public Task<string?> GetAuthSubjectIdAsync(
            string scopeId,
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            Task.FromResult(staffAuthSubjectId);
    }

    private sealed class TestWorkspaceOwnerAudienceReader(IReadOnlyList<string> recipients)
        : IWorkspaceOwnerNotificationAudienceReader
    {
        public Task<IReadOnlyList<string>> ListAuthSubjectIdsAsync(
            string scopeId,
            CancellationToken cancellationToken) =>
            Task.FromResult(recipients);
    }

    private sealed class TestOrganizationAccessCandidateFilter(
        IReadOnlyCollection<string>? allowedSubjects = null) : IOrganizationAccessCandidateFilter
    {
        private readonly HashSet<string>? allowed = allowedSubjects?.ToHashSet(StringComparer.Ordinal);

        public List<IReadOnlyList<string>> Requests { get; } = [];

        public Task<IReadOnlyList<string>> FilterAllowedAsync(
            Guid organizationId,
            IReadOnlyCollection<string> candidateSubjectIds,
            CancellationToken cancellationToken)
        {
            Assert.Equal(OrganizationId, organizationId);
            string[] candidates = candidateSubjectIds.ToArray();
            this.Requests.Add(candidates);
            IReadOnlyList<string> result = this.allowed is null
                ? candidates
                : candidates.Where(this.allowed.Contains).ToArray();
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingOrganizationAccessCandidateFilter
        : IOrganizationAccessCandidateFilter
    {
        public Task<IReadOnlyList<string>> FilterAllowedAsync(
            Guid organizationId,
            IReadOnlyCollection<string> candidateSubjectIds,
            CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<string>>(
                new InvalidOperationException("membership authority unavailable"));
    }

    private sealed class TestRecipientResolver(
        IReadOnlyCollection<string>? missingSubjects = null,
        IReadOnlyDictionary<string, Guid>? staffMemberIds = null,
        Func<IReadOnlyList<string>, IReadOnlyList<StaffNotificationRecipient>>?
            resolve = null,
        Exception? failure = null)
        : IStaffNotificationRecipientResolver
    {
        private readonly HashSet<string> missing =
            missingSubjects?.ToHashSet(StringComparer.Ordinal) ?? [];

        public List<IReadOnlyList<string>> Requests { get; } = [];

        public Task<IReadOnlyList<StaffNotificationRecipient>>
            ResolveActiveAsync(
                string scopeId,
                IReadOnlyCollection<string> authSubjectIds,
                CancellationToken cancellationToken)
        {
            Assert.Equal(ScopeId, scopeId);
            string[] subjects = authSubjectIds.ToArray();
            this.Requests.Add(subjects);
            if (failure is not null)
            {
                return Task.FromException<
                    IReadOnlyList<StaffNotificationRecipient>>(failure);
            }

            IReadOnlyList<StaffNotificationRecipient> result = resolve is null
                ? subjects
                    .Where(subject => !this.missing.Contains(subject))
                    .Select(subject => new StaffNotificationRecipient(
                        staffMemberIds is not null &&
                        staffMemberIds.TryGetValue(
                            subject,
                            out Guid staffMemberId)
                                ? staffMemberId
                                : StaffMemberIdFor(subject),
                        subject))
                    .ToArray()
                : resolve(subjects);
            return Task.FromResult(result);
        }
    }

    private sealed record CapturedLog(
        LogLevel Level,
        EventId EventId,
        string Message);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<CapturedLog> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            this.Entries.Add(
                new CapturedLog(
                    logLevel,
                    eventId,
                    formatter(state, exception)));
    }

    private sealed class TestAuthorizationService(
        IReadOnlyCollection<string>? allowedSubjects = null)
        : IAccessAuthorizationService
    {
        private readonly HashSet<string>? allowed =
            allowedSubjects?.ToHashSet(StringComparer.Ordinal);

        public List<AccessRequirement[]> Requests { get; } = [];

        public Task<AccessDecision> AuthorizeAsync(
            AccessRequirement requirement,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Decide(requirement));

        public Task<IReadOnlyList<AccessDecision>> AuthorizeManyAsync(
            IReadOnlyList<AccessRequirement> requirements,
            CancellationToken cancellationToken)
        {
            AccessRequirement[] request = requirements.ToArray();
            this.Requests.Add(request);
            return Task.FromResult<IReadOnlyList<AccessDecision>>(
                request.Select(this.Decide).ToArray());
        }

        private AccessDecision Decide(AccessRequirement requirement) =>
            this.allowed is null || this.allowed.Contains(requirement.Subject.Id)
                ? AccessDecision.Allowed()
                : AccessDecision.Denied("test.denied");
    }

    private sealed class ThrowingAuthorizationService
        : IAccessAuthorizationService
    {
        public Task<AccessDecision> AuthorizeAsync(
            AccessRequirement requirement,
            CancellationToken cancellationToken) =>
            Task.FromException<AccessDecision>(
                new InvalidOperationException(
                    "authorization authority unavailable"));

        public Task<IReadOnlyList<AccessDecision>> AuthorizeManyAsync(
            IReadOnlyList<AccessRequirement> requirements,
            CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<AccessDecision>>(
                new InvalidOperationException(
                    "authorization authority unavailable"));
    }

    private sealed class TestIngestionSourceLinkResolver(
        Guid? sourceLinkId = null,
        bool returnMissing = false)
        : IIngestionNotificationSourceLinkResolver
    {
        private readonly Guid? sourceLinkId =
            returnMissing
                ? null
                : sourceLinkId ?? Guid.Parse(
                    "dddddddd-dddd-dddd-dddd-dddddddddddd");

        public List<SourceLinkResolutionRequest> Requests { get; } = [];

        public Task<IngestionNotificationSourceLink?> ResolveAsync(
            string scopeId,
            Guid propertyId,
            Guid connectionId,
            Guid operationId,
            Guid receiptId,
            CancellationToken cancellationToken)
        {
            this.Requests.Add(
                new SourceLinkResolutionRequest(
                    scopeId,
                    propertyId,
                    connectionId,
                    operationId,
                    receiptId));
            return Task.FromResult(
                this.sourceLinkId is Guid value
                    ? new IngestionNotificationSourceLink(value)
                    : null);
        }
    }

    private sealed record SourceLinkResolutionRequest(
        string ScopeId,
        Guid PropertyId,
        Guid ConnectionId,
        Guid OperationId,
        Guid ReceiptId);

    private sealed class CapturingProjector : IUserNotificationRequestProjectorV3
    {
        public List<UserNotificationRequestedIntegrationEventV3> Events { get; } = [];

        public Task ProjectAsync(
            UserNotificationRequestedIntegrationEventV3 integrationEvent,
            CancellationToken cancellationToken)
        {
            this.Events.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }
}
