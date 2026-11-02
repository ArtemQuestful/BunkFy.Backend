namespace Integration.Tests;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BunkFy.Modules.Inventory.Application;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Properties.Contracts;
using DotNet.Testcontainers.Containers;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Messaging;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Tenancy;
using Gma.Modules.Auth.Contracts;
using Integration.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class InventoryManualBlockGroupReplacementApiIntegrationTests
{
    private const string TenantId =
        "a2100000-0000-0000-0000-000000000001";
    private const string TenantHeader = "X-Tenant-Id";
    private const string Arrival = "2026-10-01";
    private const string Departure = "2026-10-03";
    private const string InitialReason = "Property maintenance";
    private const string ReplacementReason = "Focused room maintenance";
    private static readonly Guid PropertyId =
        Guid.Parse("11000000-0000-0000-0000-00000000000a");
    private static readonly Guid FirstRoomId =
        Guid.Parse("21000000-0000-0000-0000-00000000000a");
    private static readonly Guid SecondRoomId =
        Guid.Parse("21000000-0000-0000-0000-00000000000b");
    private static readonly Guid WrongPropertyId =
        Guid.Parse("11000000-0000-0000-0000-00000000000b");
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Changed_and_no_op_replacements_are_atomic_recoverable_and_truthful()
    {
        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await nats.StartAsync().ConfigureAwait(false);
        string natsConnectionString =
            AuthTestContainers.GetNatsConnectionString(nats);

        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_inventory_block_group_replacement_api_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        string connectionString = postgreSql.GetConnectionString();
        await using AuthTestApplication api = new(
            "PostgreSql",
            connectionString,
            natsConnectionString);
        await api.MigrateInventoryAuthorizationDatabaseAsync()
            .ConfigureAwait(false);
        await SeedTopologyAsync(api).ConfigureAwait(false);

        await using AdminCliTestApplication admin = new(
            "PostgreSql",
            connectionString);
        await admin.MigrateAsync().ConfigureAwait(false);
        using HttpClient client = api.CreateClient();
        AuthTokensResponse tokens = await AuthApiClient.RegisterAsync(
            client,
            TenantId,
            "replacement-operator@inventory.test").ConfigureAwait(false);
        Guid operatorId = GetSubjectId(tokens.AccessToken);
        string actorId = $"user:{operatorId:D}";
        await api.SeedOrganizationMembershipAsync(TenantId, operatorId)
            .ConfigureAwait(false);
        await GrantInventoryAccessAsync(admin, operatorId)
            .ConfigureAwait(false);
        await ConfigureRoomAsync(client, tokens.AccessToken, FirstRoomId)
            .ConfigureAwait(false);
        await ConfigureRoomAsync(client, tokens.AccessToken, SecondRoomId)
            .ConfigureAwait(false);

        InventoryBlockTarget initialTarget = new(
            InventoryBlockTargetKind.Property);
        ManualInventoryBlockGroupSelectionPreviewDto initialPreview =
            await PreviewAsync(
                client,
                tokens.AccessToken,
                initialTarget,
                InitialReason).ConfigureAwait(false);
        Assert.Equal(
            ManualInventoryBlockGroupPreviewStatus.Ready,
            initialPreview.Status);
        Assert.Equal(2, initialPreview.AffectedBlockCount);
        Assert.False(initialPreview.IsNoOpReplacement);
        Assert.NotNull(initialPreview.SelectionDigest);

        Guid createOperationId = Guid.NewGuid();
        using (HttpResponseMessage unconfirmed = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/inventory/properties/{PropertyId:D}/block-groups",
                   tokens.AccessToken,
                   new
                   {
                       operationId = createOperationId,
                       target = initialTarget,
                       arrival = Arrival,
                       departure = Departure,
                       reason = InitialReason,
                       expectedSelectionDigest = initialPreview.SelectionDigest,
                       expectedAffectedBlockCount = initialPreview.AffectedBlockCount,
                       confirmed = false
                   }).ConfigureAwait(false))
        {
            await AssertProblemAsync(
                HttpStatusCode.BadRequest,
                InventoryApplicationErrors.BlockGroupConfirmationRequired.Code,
                unconfirmed).ConfigureAwait(false);
        }

        using (HttpResponseMessage unboundRecovery = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/inventory/properties/{PropertyId:D}/block-group-create-operations/{createOperationId:D}",
                   tokens.AccessToken).ConfigureAwait(false))
        {
            await AssertProblemAsync(
                HttpStatusCode.NotFound,
                InventoryApplicationErrors.BlockGroupOperationNotFound.Code,
                unboundRecovery).ConfigureAwait(false);
        }

        ManualInventoryBlockGroupMutationReceiptDto initial =
            await CreateAsync(
                client,
                tokens.AccessToken,
                createOperationId,
                initialTarget,
                InitialReason,
                initialPreview).ConfigureAwait(false);
        Assert.Equal(2, initial.AffectedBlockCount);
        Assert.Equal(2, initial.CreatedNowBlockCount);
        Assert.Equal(2, initial.TotalBlockCount);
        Assert.Equal(2, initial.ActiveBlockCount);
        Assert.Equal(ManualInventoryBlockGroupStatus.Active, initial.Status);
        Assert.Equal(1L, initial.Version);
        Assert.Null(initial.PreviousBlockGroupId);

        using (HttpResponseMessage wrongPropertyCreateRecovery = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/inventory/properties/{WrongPropertyId:D}/block-group-create-operations/{createOperationId:D}",
                   tokens.AccessToken).ConfigureAwait(false))
        {
            await AssertProblemAsync(
                HttpStatusCode.NotFound,
                InventoryApplicationErrors.BlockGroupOperationNotFound.Code,
                wrongPropertyCreateRecovery).ConfigureAwait(false);
        }

        ReplacementStateSnapshot afterCreate =
            await CaptureReplacementStateAsync(api).ConfigureAwait(false);
        _ = Assert.Single(afterCreate.Groups);
        Assert.Equal(2, afterCreate.Blocks.Length);
        Assert.Equal(2, afterCreate.OutboxMessages.Length);
        Assert.All(
            afterCreate.OutboxMessages,
            message => Assert.Equal(
                typeof(ManualInventoryBlockCreatedIntegrationEvent).FullName,
                message.EventType));

        InventoryBlockTarget changedTarget = new(
            InventoryBlockTargetKind.Room,
            RoomId: FirstRoomId);
        ManualInventoryBlockGroupSelectionPreviewDto changedPreview =
            await PreviewAsync(
                client,
                tokens.AccessToken,
                changedTarget,
                ReplacementReason,
                initial.BlockGroupId,
                initial.Version).ConfigureAwait(false);
        Assert.Equal(
            ManualInventoryBlockGroupPreviewStatus.Ready,
            changedPreview.Status);
        Assert.Equal(initial.BlockGroupId, changedPreview.BlockGroupId);
        Assert.Equal(initial.Version, changedPreview.BlockGroupVersion);
        Assert.Equal(1, changedPreview.AffectedBlockCount);
        Assert.False(changedPreview.IsNoOpReplacement);
        Assert.NotNull(changedPreview.SelectionDigest);

        Guid replaceOperationId = Guid.NewGuid();
        ReplacementRequest changedRequest = new(
            replaceOperationId,
            initial.Version!.Value,
            changedTarget,
            Arrival,
            Departure,
            ReplacementReason,
            changedPreview.SelectionDigest!,
            changedPreview.AffectedBlockCount!.Value,
            Confirmed: true);
        ManualInventoryBlockGroupMutationReceiptDto changed =
            await ReplaceAsync(
                client,
                tokens.AccessToken,
                initial.BlockGroupId,
                changedRequest).ConfigureAwait(false);
        Assert.NotEqual(initial.BlockGroupId, changed.ResultBlockGroupId);
        Assert.Equal(initial.BlockGroupId, changed.PreviousBlockGroupId);
        Assert.Equal(3, changed.AffectedBlockCount);
        Assert.Equal(2, changed.ReleasedNowBlockCount);
        Assert.Equal(0, changed.AlreadyReleasedBlockCount);
        Assert.Equal(1, changed.CreatedNowBlockCount);
        Assert.Equal(1, changed.TotalBlockCount);
        Assert.Equal(1, changed.ActiveBlockCount);
        Assert.Equal(ManualInventoryBlockGroupStatus.Active, changed.Status);
        Assert.Equal(1L, changed.Version);
        Assert.Equal(changedPreview.MembershipDigest, changed.MembershipDigest);

        ManualInventoryBlockGroupDto predecessor = await GetGroupAsync(
            client,
            tokens.AccessToken,
            initial.BlockGroupId).ConfigureAwait(false);
        Assert.Equal(ManualInventoryBlockGroupStatus.Replaced, predecessor.Status);
        Assert.Equal(2L, predecessor.Version);
        Assert.Equal(2, predecessor.InitialBlockCount);
        Assert.Equal(0, predecessor.ActiveBlockCount);
        Assert.Null(predecessor.ReplacesGroupId);
        Assert.Equal(changed.ResultBlockGroupId, predecessor.ReplacedByGroupId);
        Assert.Equal(actorId, predecessor.LastModifiedByActorId);
        Assert.NotNull(predecessor.ReleasedAtUtc);

        ManualInventoryBlockGroupDto successor = await GetGroupAsync(
            client,
            tokens.AccessToken,
            changed.ResultBlockGroupId).ConfigureAwait(false);
        Assert.Equal(ManualInventoryBlockGroupStatus.Active, successor.Status);
        Assert.Equal(1L, successor.Version);
        Assert.Equal(1, successor.InitialBlockCount);
        Assert.Equal(1, successor.ActiveBlockCount);
        Assert.Equal(initial.BlockGroupId, successor.ReplacesGroupId);
        Assert.Null(successor.ReplacedByGroupId);
        Assert.Equal(changedTarget, successor.Target);
        Assert.Equal(ReplacementReason, successor.Reason);
        Assert.Equal(actorId, successor.CreatedByActorId);
        Assert.Equal(actorId, successor.LastModifiedByActorId);

        ManualInventoryBlockGroupMemberListResponse predecessorMembers =
            await GetMembersAsync(
                client,
                tokens.AccessToken,
                initial.BlockGroupId).ConfigureAwait(false);
        Assert.Equal(2, predecessorMembers.Blocks.Count);
        Assert.All(
            predecessorMembers.Blocks,
            member =>
            {
                Assert.Equal(ManualInventoryBlockStatus.Released, member.Status);
                Assert.Equal(2L, member.Version);
                Assert.NotNull(member.ReleasedAtUtc);
            });
        ManualInventoryBlockGroupMemberListResponse successorMembers =
            await GetMembersAsync(
                client,
                tokens.AccessToken,
                changed.ResultBlockGroupId).ConfigureAwait(false);
        ManualInventoryBlockDto successorMember =
            Assert.Single(successorMembers.Blocks);
        Assert.Equal(FirstRoomId, successorMember.InventoryUnitId);
        Assert.Equal(ManualInventoryBlockStatus.Active, successorMember.Status);
        Assert.Equal(1L, successorMember.Version);

        InventoryAvailabilityResponse availability = await GetAvailabilityAsync(
            client,
            tokens.AccessToken).ConfigureAwait(false);
        InventoryUnitAvailabilityDto firstRoomAvailability =
            Assert.Single(
                availability.Units,
                item => item.Unit.InventoryUnitId == FirstRoomId);
        Assert.False(firstRoomAvailability.IsAvailable);
        Assert.Equal(
            successorMember.BlockId,
            Assert.Single(firstRoomAvailability.ActiveBlockIds));
        InventoryUnitAvailabilityDto secondRoomAvailability =
            Assert.Single(
                availability.Units,
                item => item.Unit.InventoryUnitId == SecondRoomId);
        Assert.True(secondRoomAvailability.IsAvailable);
        Assert.Empty(secondRoomAvailability.ActiveBlockIds);

        ManualInventoryBlockGroupOperationDto changedRecovery =
            await RecoverAsync(
                client,
                tokens.AccessToken,
                initial.BlockGroupId,
                replaceOperationId).ConfigureAwait(false);
        Assert.Equal(replaceOperationId, changedRecovery.OperationId);
        Assert.Equal(initial.BlockGroupId, changedRecovery.RequestedBlockGroupId);
        Assert.Equal(
            ManualInventoryBlockGroupOperationKind.Replace,
            changedRecovery.Kind);
        Assert.Equal(
            ManualInventoryBlockGroupOperationStatus.Applied,
            changedRecovery.Status);
        Assert.Equal(changed, changedRecovery.Receipt);

        using (HttpResponseMessage wrongPropertyReplaceRecovery = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/inventory/properties/{WrongPropertyId:D}/block-groups/{initial.BlockGroupId:D}/operations/{replaceOperationId:D}",
                   tokens.AccessToken).ConfigureAwait(false))
        {
            await AssertProblemAsync(
                HttpStatusCode.NotFound,
                InventoryApplicationErrors.BlockGroupOperationNotFound.Code,
                wrongPropertyReplaceRecovery).ConfigureAwait(false);
        }

        Guid legacyOperationId = Guid.NewGuid();
        Guid legacyResultGroupId = Guid.NewGuid();
        await SeedLegacyCreateOperationAsync(
            api,
            legacyOperationId,
            legacyResultGroupId).ConfigureAwait(false);
        using (HttpResponseMessage legacyRecoveryResponse = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/inventory/properties/{PropertyId:D}/block-group-create-operations/{legacyOperationId:D}",
                   tokens.AccessToken).ConfigureAwait(false))
        {
            ManualInventoryBlockGroupOperationDto legacyRecovery =
                await ReadSuccessAsync<ManualInventoryBlockGroupOperationDto>(
                    legacyRecoveryResponse).ConfigureAwait(false);
            Assert.Equal(legacyOperationId, legacyRecovery.OperationId);
            Assert.Equal(ManualInventoryBlockGroupOperationKind.Create, legacyRecovery.Kind);
            Assert.Equal(
                ManualInventoryBlockGroupOperationStatus.Applied,
                legacyRecovery.Status);
            Assert.Null(legacyRecovery.RequestedBlockGroupId);
            Assert.Equal(legacyResultGroupId, legacyRecovery.Receipt.ResultBlockGroupId);
            Assert.Equal(PropertyId, legacyRecovery.Receipt.PropertyId);
            Assert.Equal(7, legacyRecovery.Receipt.AffectedBlockCount);
            Assert.Null(legacyRecovery.Receipt.Status);
            Assert.Null(legacyRecovery.Receipt.Version);
            Assert.Null(legacyRecovery.Receipt.PreviousBlockGroupId);
            Assert.Null(legacyRecovery.Receipt.ReleasedNowBlockCount);
            Assert.Null(legacyRecovery.Receipt.AlreadyReleasedBlockCount);
            Assert.Null(legacyRecovery.Receipt.CreatedNowBlockCount);
            Assert.Null(legacyRecovery.Receipt.TotalBlockCount);
            Assert.Null(legacyRecovery.Receipt.ActiveBlockCount);
            Assert.Null(legacyRecovery.Receipt.MembershipDigest);
        }

        ReplacementStateSnapshot afterChangedReplace =
            await CaptureReplacementStateAsync(api).ConfigureAwait(false);
        Assert.Equal(2, afterChangedReplace.Groups.Length);
        Assert.Equal(3, afterChangedReplace.Blocks.Length);
        Assert.Equal(5, afterChangedReplace.OutboxMessages.Length);
        Assert.Equal(
            3,
            afterChangedReplace.OutboxMessages.Count(message =>
                message.EventType ==
                typeof(ManualInventoryBlockCreatedIntegrationEvent).FullName));
        Assert.Equal(
            2,
            afterChangedReplace.OutboxMessages.Count(message =>
                message.EventType ==
                typeof(ManualInventoryBlockReleasedIntegrationEvent).FullName));
        Assert.Single(
            afterChangedReplace.OutboxMessages,
            message =>
                message.EventType ==
                    typeof(ManualInventoryBlockCreatedIntegrationEvent).FullName &&
                message.Payload.Contains(
                    changed.ResultBlockGroupId.ToString("D"),
                    StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            2,
            afterChangedReplace.OutboxMessages.Count(message =>
                message.EventType ==
                    typeof(ManualInventoryBlockReleasedIntegrationEvent).FullName &&
                message.Payload.Contains(
                    initial.BlockGroupId.ToString("D"),
                    StringComparison.OrdinalIgnoreCase)));
        Assert.All(
            afterChangedReplace.OutboxMessages,
            message => Assert.Contains(
                actorId,
                message.Payload,
                StringComparison.Ordinal));

        ManualInventoryBlockGroupMutationReceiptDto exactReplay =
            await ReplaceAsync(
                client,
                tokens.AccessToken,
                initial.BlockGroupId,
                changedRequest).ConfigureAwait(false);
        Assert.Equal(changed, exactReplay);
        AssertReplacementStateEqual(
            afterChangedReplace,
            await CaptureReplacementStateAsync(api).ConfigureAwait(false));

        ReplacementRequest changedReuse = changedRequest with
        {
            Reason = "Changed operation reuse"
        };
        using (HttpResponseMessage conflict = await SendAsync(
                   client,
                   HttpMethod.Put,
                   GroupPath(initial.BlockGroupId),
                   tokens.AccessToken,
                   changedReuse).ConfigureAwait(false))
        {
            await AssertProblemAsync(
                HttpStatusCode.Conflict,
                InventoryApplicationErrors.ManagementOperationConflict.Code,
                conflict).ConfigureAwait(false);
        }

        AssertReplacementStateEqual(
            afterChangedReplace,
            await CaptureReplacementStateAsync(api).ConfigureAwait(false));
        ManualInventoryBlockGroupOperationDto unchangedRecovery =
            await RecoverAsync(
                client,
                tokens.AccessToken,
                initial.BlockGroupId,
                replaceOperationId).ConfigureAwait(false);
        Assert.Equal(changedRecovery, unchangedRecovery);

        ManualInventoryBlockGroupSelectionPreviewDto noOpPreview =
            await PreviewAsync(
                client,
                tokens.AccessToken,
                changedTarget,
                ReplacementReason,
                changed.ResultBlockGroupId,
                changed.Version).ConfigureAwait(false);
        Assert.Equal(
            ManualInventoryBlockGroupPreviewStatus.Ready,
            noOpPreview.Status);
        Assert.True(noOpPreview.IsNoOpReplacement);
        Assert.Equal(changed.ResultBlockGroupId, noOpPreview.BlockGroupId);
        Assert.Equal(changed.Version, noOpPreview.BlockGroupVersion);
        Assert.Equal(1, noOpPreview.AffectedBlockCount);
        Assert.Equal(changed.MembershipDigest, noOpPreview.MembershipDigest);
        Assert.NotNull(noOpPreview.SelectionDigest);

        Guid noOpOperationId = Guid.NewGuid();
        ReplacementRequest noOpRequest = new(
            noOpOperationId,
            changed.Version!.Value,
            changedTarget,
            Arrival,
            Departure,
            ReplacementReason,
            noOpPreview.SelectionDigest!,
            noOpPreview.AffectedBlockCount!.Value,
            Confirmed: true);
        ReplacementStateSnapshot beforeNoOp =
            await CaptureReplacementStateAsync(api).ConfigureAwait(false);
        ManualInventoryBlockGroupMutationReceiptDto noOp =
            await ReplaceAsync(
                client,
                tokens.AccessToken,
                changed.ResultBlockGroupId,
                noOpRequest).ConfigureAwait(false);
        Assert.Equal(changed.ResultBlockGroupId, noOp.ResultBlockGroupId);
        Assert.Equal(changed.ResultBlockGroupId, noOp.PreviousBlockGroupId);
        Assert.Equal(0, noOp.AffectedBlockCount);
        Assert.Equal(0, noOp.ReleasedNowBlockCount);
        Assert.Equal(0, noOp.CreatedNowBlockCount);
        Assert.Equal(0, noOp.AlreadyReleasedBlockCount);
        Assert.Equal(1, noOp.TotalBlockCount);
        Assert.Equal(1, noOp.ActiveBlockCount);
        Assert.Equal(ManualInventoryBlockGroupStatus.Active, noOp.Status);
        Assert.Equal(changed.Version, noOp.Version);
        Assert.Equal(changed.MembershipDigest, noOp.MembershipDigest);

        ReplacementStateSnapshot afterNoOp =
            await CaptureReplacementStateAsync(api).ConfigureAwait(false);
        AssertReplacementStateEqual(beforeNoOp, afterNoOp);
        ManualInventoryBlockGroupDto successorAfterNoOp = await GetGroupAsync(
            client,
            tokens.AccessToken,
            changed.ResultBlockGroupId).ConfigureAwait(false);
        Assert.Equal(successor, successorAfterNoOp);

        ManualInventoryBlockGroupOperationDto noOpRecovery =
            await RecoverAsync(
                client,
                tokens.AccessToken,
                changed.ResultBlockGroupId,
                noOpOperationId).ConfigureAwait(false);
        Assert.Equal(noOpOperationId, noOpRecovery.OperationId);
        Assert.Equal(
            changed.ResultBlockGroupId,
            noOpRecovery.RequestedBlockGroupId);
        Assert.Equal(
            ManualInventoryBlockGroupOperationKind.Replace,
            noOpRecovery.Kind);
        Assert.Equal(
            ManualInventoryBlockGroupOperationStatus.Applied,
            noOpRecovery.Status);
        Assert.Equal(noOp, noOpRecovery.Receipt);

        ManualInventoryBlockGroupMutationReceiptDto noOpReplay =
            await ReplaceAsync(
                client,
                tokens.AccessToken,
                changed.ResultBlockGroupId,
                noOpRequest).ConfigureAwait(false);
        Assert.Equal(noOp, noOpReplay);
        AssertReplacementStateEqual(
            afterNoOp,
            await CaptureReplacementStateAsync(api).ConfigureAwait(false));
    }

    private static async Task GrantInventoryAccessAsync(
        AdminCliTestApplication admin,
        Guid operatorId)
    {
        await AssertAdminSuccessAsync(
            admin.ExecuteAsync(
                "admin",
                "bootstrap",
                "--actor",
                "owner",
                "--yes")).ConfigureAwait(false);
        await AssertAdminSuccessAsync(
            admin.ExecuteAsync(
                "admin",
                "roles",
                "create",
                "--actor",
                "owner",
                "--name",
                "inventory-replacement-operator")).ConfigureAwait(false);
        string[] permissions =
        [
            InventoryAdminPermissionCodes.Read,
            InventoryAdminPermissionCodes.Configure,
            InventoryAdminPermissionCodes.BlockGroupsManage
        ];
        foreach (string permission in permissions)
        {
            await AssertAdminSuccessAsync(
                admin.ExecuteAsync(
                    "admin",
                    "roles",
                    "grant",
                    "--actor",
                    "owner",
                    "--role",
                    "inventory-replacement-operator",
                    "--permission",
                    permission)).ConfigureAwait(false);
        }

        foreach (Guid propertyId in new[] { PropertyId, WrongPropertyId })
        {
            await AssertAdminSuccessAsync(
                admin.ExecuteAsync(
                    "admin",
                    "roles",
                    "assign",
                    "--actor",
                    "owner",
                    "--target-kind",
                    "user",
                    "--target-id",
                    operatorId.ToString("D"),
                    "--role",
                    "inventory-replacement-operator",
                    "--scope",
                    $"tenant:{TenantId}/property:{propertyId:D}"))
                .ConfigureAwait(false);
        }
    }

    private static async Task SeedLegacyCreateOperationAsync(
        AuthTestApplication api,
        Guid operationId,
        Guid resultBlockGroupId)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        IInventoryManagementOperationRepository operations = scope
            .ServiceProvider
            .GetRequiredService<IInventoryManagementOperationRepository>();
        InventoryDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        await operations.AddAsync(
            InventoryManagementOperationRecord.ForBlockGroup(
                operationId,
                TenantId,
                InventoryManagementResourceKind.Property,
                PropertyId,
                InventoryManagementMutationKind.ManualBlockGroupCreate,
                new string('a', 64),
                new ManualInventoryBlockGroupMutationReceiptDto(
                    resultBlockGroupId,
                    PropertyId,
                    AffectedBlockCount: 7),
                DateTimeOffset.UtcNow),
            CancellationToken.None).ConfigureAwait(false);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task SeedTopologyAsync(AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        InventoryDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync()
            .ConfigureAwait(false);
        IIntegrationEventHandler<PropertyCreatedIntegrationEvent>
            propertyHandler = ResolveInventoryHandler<
                PropertyCreatedIntegrationEvent>(scope.ServiceProvider);
        IIntegrationEventHandler<RoomCreatedIntegrationEvent> roomHandler =
            ResolveInventoryHandler<RoomCreatedIntegrationEvent>(
                scope.ServiceProvider);
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        await propertyHandler.HandleAsync(
            new(
                Guid.NewGuid(),
                TenantId,
                nowUtc,
                PropertyId,
                "Replacement House",
                "replacement-house",
                "UTC",
                PropertyStatus.Active,
                propertyVersion: 1),
            CancellationToken.None).ConfigureAwait(false);
        await roomHandler.HandleAsync(
            new(
                Guid.NewGuid(),
                TenantId,
                nowUtc,
                PropertyId,
                FirstRoomId,
                "101",
                "Main",
                "1",
                RoomStatus.Active,
                roomVersion: 1),
            CancellationToken.None).ConfigureAwait(false);
        await roomHandler.HandleAsync(
            new(
                Guid.NewGuid(),
                TenantId,
                nowUtc,
                PropertyId,
                SecondRoomId,
                "102",
                "Main",
                "1",
                RoomStatus.Active,
                roomVersion: 1),
            CancellationToken.None).ConfigureAwait(false);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static IIntegrationEventHandler<TEvent>
        ResolveInventoryHandler<TEvent>(IServiceProvider services)
        where TEvent : IIntegrationEvent
    {
        IntegrationEventSubscription subscription = services
            .GetRequiredService<IIntegrationEventSubscriptionRegistry>()
            .Subscriptions
            .Single(item =>
                item.ConsumerModule == InventoryModuleMetadata.Name &&
                item.EventType == typeof(TEvent));
        return (IIntegrationEventHandler<TEvent>)services
            .GetRequiredService(subscription.HandlerType);
    }

    private static async Task ConfigureRoomAsync(
        HttpClient client,
        string accessToken,
        Guid roomId)
    {
        using HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Put,
            $"/api/inventory/properties/{PropertyId:D}/rooms/{roomId:D}/sales-mode",
            accessToken,
            new
            {
                operationId = Guid.NewGuid(),
                salesMode = InventorySalesMode.RoomLevel,
                expectedVersion = 1
            }).ConfigureAwait(false);
        RoomInventoryMutationReceiptDto receipt =
            await ReadSuccessAsync<RoomInventoryMutationReceiptDto>(response)
                .ConfigureAwait(false);
        Assert.Equal(roomId, receipt.RoomId);
        Assert.Equal(InventorySalesMode.RoomLevel, receipt.SalesMode);
        Assert.Equal(2L, receipt.Version);
    }

    private static async Task<ManualInventoryBlockGroupSelectionPreviewDto>
        PreviewAsync(
            HttpClient client,
            string accessToken,
            InventoryBlockTarget target,
            string reason,
            Guid? blockGroupId = null,
            long? expectedVersion = null)
    {
        using HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Post,
            $"/api/inventory/properties/{PropertyId:D}/block-groups/preview",
            accessToken,
            new
            {
                target,
                arrival = Arrival,
                departure = Departure,
                reason,
                blockGroupId,
                expectedVersion
            }).ConfigureAwait(false);
        return await ReadSuccessAsync<
                ManualInventoryBlockGroupSelectionPreviewDto>(response)
            .ConfigureAwait(false);
    }

    private static async Task<ManualInventoryBlockGroupMutationReceiptDto>
        CreateAsync(
            HttpClient client,
            string accessToken,
            Guid operationId,
            InventoryBlockTarget target,
            string reason,
            ManualInventoryBlockGroupSelectionPreviewDto preview)
    {
        using HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Post,
            $"/api/inventory/properties/{PropertyId:D}/block-groups",
            accessToken,
            new
            {
                operationId,
                target,
                arrival = Arrival,
                departure = Departure,
                reason,
                expectedSelectionDigest = preview.SelectionDigest,
                expectedAffectedBlockCount = preview.AffectedBlockCount,
                confirmed = true
            }).ConfigureAwait(false);
        return await ReadSuccessAsync<
                ManualInventoryBlockGroupMutationReceiptDto>(response)
            .ConfigureAwait(false);
    }

    private static async Task<ManualInventoryBlockGroupMutationReceiptDto>
        ReplaceAsync(
            HttpClient client,
            string accessToken,
            Guid blockGroupId,
            ReplacementRequest request)
    {
        using HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Put,
            GroupPath(blockGroupId),
            accessToken,
            request).ConfigureAwait(false);
        return await ReadSuccessAsync<
                ManualInventoryBlockGroupMutationReceiptDto>(response)
            .ConfigureAwait(false);
    }

    private static async Task<ManualInventoryBlockGroupDto> GetGroupAsync(
        HttpClient client,
        string accessToken,
        Guid blockGroupId)
    {
        using HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Get,
            GroupPath(blockGroupId),
            accessToken).ConfigureAwait(false);
        return await ReadSuccessAsync<ManualInventoryBlockGroupDto>(response)
            .ConfigureAwait(false);
    }

    private static async Task<ManualInventoryBlockGroupMemberListResponse>
        GetMembersAsync(
            HttpClient client,
            string accessToken,
            Guid blockGroupId)
    {
        using HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Get,
            $"{GroupPath(blockGroupId)}/members?pageSize=10",
            accessToken).ConfigureAwait(false);
        ManualInventoryBlockGroupMemberListResponse members =
            await ReadSuccessAsync<
                    ManualInventoryBlockGroupMemberListResponse>(response)
                .ConfigureAwait(false);
        Assert.Null(members.NextCursor);
        return members;
    }

    private static async Task<ManualInventoryBlockGroupOperationDto>
        RecoverAsync(
            HttpClient client,
            string accessToken,
            Guid blockGroupId,
            Guid operationId)
    {
        using HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Get,
            $"{GroupPath(blockGroupId)}/operations/{operationId:D}",
            accessToken).ConfigureAwait(false);
        return await ReadSuccessAsync<ManualInventoryBlockGroupOperationDto>(
                response)
            .ConfigureAwait(false);
    }

    private static async Task<InventoryAvailabilityResponse>
        GetAvailabilityAsync(HttpClient client, string accessToken)
    {
        using HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Get,
            $"/api/inventory/properties/{PropertyId:D}/availability?arrival={Arrival}&departure={Departure}",
            accessToken).ConfigureAwait(false);
        return await ReadSuccessAsync<InventoryAvailabilityResponse>(response)
            .ConfigureAwait(false);
    }

    private static async Task<ReplacementStateSnapshot>
        CaptureReplacementStateAsync(AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        InventoryDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        GroupStateSnapshot[] groups = await dbContext.ManualBlockGroups
            .AsNoTracking()
            .OrderBy(group => group.Id)
            .Select(group => new GroupStateSnapshot(
                group.Id,
                group.State,
                group.Version,
                group.InitialBlockCount,
                group.ActiveBlockCount,
                group.ReplacesGroupId,
                group.UpdatedAtUtc,
                group.ReleasedAtUtc,
                group.LastModifiedByActorId))
            .ToArrayAsync()
            .ConfigureAwait(false);
        BlockStateSnapshot[] blocks = await dbContext.ManualBlocks
            .AsNoTracking()
            .OrderBy(block => block.Id)
            .Select(block => new BlockStateSnapshot(
                block.Id,
                block.BlockGroupId,
                block.InventoryUnitId,
                block.Status,
                block.Version,
                block.ReleasedAtUtc))
            .ToArrayAsync()
            .ConfigureAwait(false);
        UnitAvailabilityVersionSnapshot[] unitVersions =
            await dbContext.InventoryUnits
                .AsNoTracking()
                .Where(unit => unit.PropertyId == PropertyId)
                .OrderBy(unit => unit.Id)
                .Select(unit => new UnitAvailabilityVersionSnapshot(
                    unit.Id,
                    unit.AvailabilityMutationVersion))
                .ToArrayAsync()
                .ConfigureAwait(false);
        RoomAvailabilityVersionSnapshot[] roomVersions =
            await dbContext.RoomConfigurations
                .AsNoTracking()
                .Where(room => room.PropertyId == PropertyId)
                .OrderBy(room => room.Id)
                .Select(room => new RoomAvailabilityVersionSnapshot(
                    room.Id,
                    room.AvailabilityMutationVersion))
                .ToArrayAsync()
                .ConfigureAwait(false);
        long selectionVersion = await dbContext.PropertyTopology
            .AsNoTracking()
            .Where(property => property.Id == PropertyId)
            .Select(property => property.AvailabilitySelectionVersion)
            .SingleAsync()
            .ConfigureAwait(false);
        OutboxStateSnapshot[] outboxMessages = await dbContext.OutboxMessages
            .AsNoTracking()
            .Where(message =>
                message.EventType ==
                    typeof(ManualInventoryBlockCreatedIntegrationEvent)
                        .FullName ||
                message.EventType ==
                    typeof(ManualInventoryBlockReleasedIntegrationEvent)
                        .FullName)
            .OrderBy(message => message.Id)
            .Select(message => new OutboxStateSnapshot(
                message.Id,
                message.EventType,
                message.ScopeId,
                message.Payload))
            .ToArrayAsync()
            .ConfigureAwait(false);
        return new(
            groups,
            blocks,
            unitVersions,
            roomVersions,
            selectionVersion,
            outboxMessages);
    }

    private static void AssertReplacementStateEqual(
        ReplacementStateSnapshot expected,
        ReplacementStateSnapshot actual)
    {
        Assert.Equal(expected.Groups, actual.Groups);
        Assert.Equal(expected.Blocks, actual.Blocks);
        Assert.Equal(expected.UnitVersions, actual.UnitVersions);
        Assert.Equal(expected.RoomVersions, actual.RoomVersions);
        Assert.Equal(expected.SelectionVersion, actual.SelectionVersion);
        Assert.Equal(expected.OutboxMessages, actual.OutboxMessages);
    }

    private static string GroupPath(Guid blockGroupId) =>
        $"/api/inventory/properties/{PropertyId:D}/block-groups/{blockGroupId:D}";

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? accessToken = null,
        object? body = null)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Add(TenantHeader, TenantId);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                accessToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task<T> ReadSuccessAsync<T>(
        HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync()
            .ConfigureAwait(false);
        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected success but received {(int)response.StatusCode}. Body: {body}");
        T? value = JsonSerializer.Deserialize<T>(
            body,
            JsonOptions);
        Assert.NotNull(value);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return value;
    }

    private static async Task AssertProblemAsync(
        HttpStatusCode expectedStatus,
        string expectedCode,
        HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync()
            .ConfigureAwait(false);
        Assert.True(
            response.StatusCode == expectedStatus,
            $"Expected {(int)expectedStatus} but received {(int)response.StatusCode}. Body: {body}");
        ProblemDetails? problem = JsonSerializer.Deserialize<ProblemDetails>(
            body,
            JsonOptions);
        Assert.NotNull(problem);
        Assert.Equal(expectedCode, problem.Title);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private static async Task<AdminCliResult> AssertAdminSuccessAsync(
        Task<AdminCliResult> resultTask)
    {
        AdminCliResult result = await resultTask.ConfigureAwait(false);
        Assert.True(
            result.ExitCode == AdminExitCodes.Success,
            $"ExitCode={result.ExitCode}{Environment.NewLine}Output:{Environment.NewLine}{result.Output}{Environment.NewLine}Error:{Environment.NewLine}{result.Error}");
        return result;
    }

    private static Guid GetSubjectId(string accessToken)
    {
        JwtSecurityToken token =
            new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        string? subjectId = token.Claims.FirstOrDefault(claim =>
            string.Equals(
                claim.Type,
                ClaimTypes.NameIdentifier,
                StringComparison.Ordinal) ||
            string.Equals(claim.Type, "nameid", StringComparison.Ordinal) ||
            string.Equals(claim.Type, "sub", StringComparison.Ordinal))?.Value;
        Assert.True(Guid.TryParse(subjectId, out Guid parsedSubjectId));
        return parsedSubjectId;
    }

    private sealed record ReplacementRequest(
        Guid OperationId,
        long ExpectedVersion,
        InventoryBlockTarget Target,
        string Arrival,
        string Departure,
        string Reason,
        string ExpectedSelectionDigest,
        int ExpectedAffectedBlockCount,
        bool Confirmed);

    private sealed record ReplacementStateSnapshot(
        GroupStateSnapshot[] Groups,
        BlockStateSnapshot[] Blocks,
        UnitAvailabilityVersionSnapshot[] UnitVersions,
        RoomAvailabilityVersionSnapshot[] RoomVersions,
        long SelectionVersion,
        OutboxStateSnapshot[] OutboxMessages);

    private sealed record GroupStateSnapshot(
        Guid BlockGroupId,
        ManualInventoryBlockGroupState State,
        long Version,
        int InitialBlockCount,
        int ActiveBlockCount,
        Guid? ReplacesGroupId,
        DateTimeOffset? UpdatedAtUtc,
        DateTimeOffset? ReleasedAtUtc,
        string? LastModifiedByActorId);

    private sealed record BlockStateSnapshot(
        Guid BlockId,
        Guid BlockGroupId,
        Guid InventoryUnitId,
        ManualInventoryBlockState State,
        long Version,
        DateTimeOffset? ReleasedAtUtc);

    private sealed record UnitAvailabilityVersionSnapshot(
        Guid InventoryUnitId,
        long AvailabilityMutationVersion);

    private sealed record RoomAvailabilityVersionSnapshot(
        Guid RoomId,
        long AvailabilityMutationVersion);

    private sealed record OutboxStateSnapshot(
        Guid MessageId,
        string EventType,
        string? ScopeId,
        string Payload);
}
