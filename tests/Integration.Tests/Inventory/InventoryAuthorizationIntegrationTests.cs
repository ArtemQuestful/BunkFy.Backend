namespace Integration.Tests;

using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BunkFy.Modules.Inventory.Admin.Contracts;
using BunkFy.Modules.Inventory.Application;
using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Entities;
using BunkFy.Modules.Properties.Persistence;
using DotNet.Testcontainers.Containers;
using Gma.Framework.Administration;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Cqrs;
using Gma.Framework.Messaging;
using Gma.Framework.ProjectionRebuild;
using Gma.Framework.Results;
using Gma.Framework.Tenancy;
using Gma.Modules.Auth.Contracts;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class InventoryAuthorizationIntegrationTests
{
    private const string TenantA = "a2000000-0000-0000-0000-000000000001";
    private const string TenantHeader = "X-Tenant-Id";
    private static readonly Guid PropertyA = Guid.Parse("10000000-0000-0000-0000-00000000000a");
    private static readonly Guid PropertyB = Guid.Parse("10000000-0000-0000-0000-00000000000b");
    private static readonly Guid RoomA = Guid.Parse("20000000-0000-0000-0000-00000000000a");
    private static readonly Guid RoomA2 = Guid.Parse("20000000-0000-0000-0000-00000000001a");
    private static readonly Guid RoomB = Guid.Parse("20000000-0000-0000-0000-00000000000b");
    private static readonly Guid BedB = Guid.Parse("30000000-0000-0000-0000-00000000000b");
    private static readonly Guid BedB2 = Guid.Parse("30000000-0000-0000-0000-00000000002b");
    private static readonly Guid RoomModeRaceReservation = Guid.Parse("50000000-0000-0000-0000-00000000012b");
    private static readonly Guid PropertyC = Guid.Parse("10000000-0000-0000-0000-00000000000c");
    private static readonly Guid RoomC = Guid.Parse("20000000-0000-0000-0000-00000000000c");
    private static readonly Guid BedC = Guid.Parse("30000000-0000-0000-0000-00000000000c");
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Real_tokens_and_persisted_grants_protect_manual_inventory_availability()
    {
        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await nats.StartAsync();
        string natsConnectionString = AuthTestContainers.GetNatsConnectionString(nats);

        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_inventory_authorization_tests")
            .Build();
        await postgreSql.StartAsync();

        string connectionString = postgreSql.GetConnectionString();
        InventorySelectCounter queryCounter = new();
        await using AuthTestApplication api = new(
            "PostgreSql",
            connectionString,
            natsConnectionString,
            inventoryCommandInterceptor: queryCounter);
        await api.MigrateInventoryAuthorizationDatabaseAsync().ConfigureAwait(false);
        await SeedInventoryAsync(api).ConfigureAwait(false);
        await ExerciseRoomModeSafetyAsync(api, queryCounter).ConfigureAwait(false);
        await ExerciseBedRetirementWorkflowAsync(api).ConfigureAwait(false);
        await ExerciseRoomRetirementWorkflowAsync(api).ConfigureAwait(false);

        await using AdminCliTestApplication admin = new(
            "PostgreSql",
            connectionString,
            includeInventory: true);
        await admin.MigrateAsync().ConfigureAwait(false);
        using HttpClient client = api.CreateClient();

        using (HttpResponseMessage unauthenticated = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/inventory/properties/{PropertyA:D}/rooms").ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.Unauthorized, unauthenticated).ConfigureAwait(false);
            AssertNoStore(unauthenticated);
        }

        AuthTokensResponse operatorTokens = await AuthApiClient.RegisterAsync(
            client,
            TenantA,
            "operator@inventory.test").ConfigureAwait(false);
        Guid operatorId = GetSubjectId(operatorTokens.AccessToken);
        await api.SeedOrganizationMembershipAsync(TenantA, operatorId).ConfigureAwait(false);

        using (HttpResponseMessage noGrant = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/inventory/properties/{PropertyA:D}/rooms",
                   operatorTokens.AccessToken).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.Forbidden, noGrant).ConfigureAwait(false);
            AssertNoStore(noGrant);
        }

        await AssertAdminSuccessAsync(admin.ExecuteAsync("admin", "bootstrap", "--actor", "owner", "--yes"));
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "create",
            "--actor", "owner",
            "--name", "inventory-operator"));
        foreach (string permission in new[]
                 {
                     InventoryAdminPermissionCodes.Read,
                     InventoryAdminPermissionCodes.Configure,
                     InventoryAdminPermissionCodes.BlocksManage,
                     InventoryAdminPermissionCodes.BlockGroupsManage
                 })
        {
            await AssertAdminSuccessAsync(admin.ExecuteAsync(
                "admin", "roles", "grant",
                "--actor", "owner",
                "--role", "inventory-operator",
                "--permission", permission));
        }

        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "assign",
            "--actor", "owner",
            "--target-kind", "user",
            "--target-id", operatorId.ToString("D"),
            "--role", "inventory-operator",
            "--scope", $"tenant:{TenantA}/property:{PropertyA:D}"));
        Guid cliOperatorId = Guid.NewGuid();
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "assign",
            "--actor", "owner",
            "--target-kind", "admin-actor",
            "--target-id", cliOperatorId.ToString("D"),
            "--role", "inventory-operator",
            "--scope", $"tenant:{TenantA}/property:{PropertyA:D}"));
        Guid adminOperatorId = Guid.NewGuid();
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "assign",
            "--actor", "owner",
            "--target-kind", "admin-actor",
            "--target-id", adminOperatorId.ToString("D"),
            "--role", "inventory-operator",
            "--scope", $"tenant:{TenantA}/property:{PropertyA:D}"));

        using (HttpResponseMessage malformedGroupStatus = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/inventory/properties/{PropertyA:D}/block-groups?status=not-a-status",
                   operatorTokens.AccessToken).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.BadRequest, malformedGroupStatus).ConfigureAwait(false);
            AssertNoStore(malformedGroupStatus);
        }

        using (HttpResponseMessage roomsResponse = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/inventory/properties/{PropertyA:D}/rooms",
                   operatorTokens.AccessToken).ConfigureAwait(false))
        {
            RoomInventoryListResponse rooms = await ReadSuccessAsync<RoomInventoryListResponse>(roomsResponse).ConfigureAwait(false);
            RoomInventoryDto room = Assert.Single(rooms.Rooms);
            Assert.Equal(InventorySalesMode.Unconfigured, room.SalesMode);
            Assert.False(Assert.Single(room.Units).IsSellable);
        }

        using (HttpResponseMessage configureBedWithoutBeds = await SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/inventory/properties/{PropertyA:D}/rooms/{RoomA:D}/sales-mode",
                   operatorTokens.AccessToken,
                   new
                   {
                       operationId = Guid.NewGuid(),
                       salesMode = InventorySalesMode.BedLevel,
                       expectedVersion = 1
                   }).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.Conflict, configureBedWithoutBeds).ConfigureAwait(false);
        }

        using (HttpResponseMessage configureRoom = await SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/inventory/properties/{PropertyA:D}/rooms/{RoomA:D}/sales-mode",
                   operatorTokens.AccessToken,
                   new
                   {
                       operationId = Guid.NewGuid(),
                       salesMode = InventorySalesMode.RoomLevel,
                       expectedVersion = 1
                   }).ConfigureAwait(false))
        {
            RoomInventoryMutationReceiptDto receipt =
                await ReadSuccessAsync<RoomInventoryMutationReceiptDto>(configureRoom).ConfigureAwait(false);
            Assert.Equal(PropertyA, receipt.PropertyId);
            Assert.Equal(RoomA, receipt.RoomId);
            Assert.Equal(InventorySalesMode.RoomLevel, receipt.SalesMode);
            Assert.Equal(2, receipt.Version);
        }

        await ExerciseAllocationAuthorityAsync(api).ConfigureAwait(false);
        await ExerciseAllocationMutationSerializationAsync(api)
            .ConfigureAwait(false);

        ManualInventoryBlockMutationReceiptDto block;
        using (HttpResponseMessage createBlock = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/inventory/properties/{PropertyA:D}/blocks",
                   operatorTokens.AccessToken,
                   new
                   {
                       operationId = Guid.NewGuid(),
                       inventoryUnitId = RoomA,
                       arrival = "2026-08-01",
                       departure = "2026-08-03",
                       reason = "Maintenance"
                   }).ConfigureAwait(false))
        {
            block = await ReadSuccessAsync<ManualInventoryBlockMutationReceiptDto>(createBlock).ConfigureAwait(false);
            Assert.NotEqual(Guid.Empty, block.BlockGroupId);
            Assert.Equal(PropertyA, block.PropertyId);
            Assert.Equal(1, block.Version);
            Assert.Equal(ManualInventoryBlockStatus.Active, block.Status);
        }

        InventoryAvailabilityResponse blocked = await GetAvailabilityAsync(
            client,
            operatorTokens.AccessToken,
            "2026-08-02",
            "2026-08-03").ConfigureAwait(false);
        InventoryUnitAvailabilityDto blockedUnit = Assert.Single(blocked.Units);
        Assert.False(blockedUnit.IsAvailable);
        Assert.Equal(block.BlockId, Assert.Single(blockedUnit.ActiveBlockIds));

        InventoryAvailabilityResponse adjacent = await GetAvailabilityAsync(
            client,
            operatorTokens.AccessToken,
            "2026-08-03",
            "2026-08-04").ConfigureAwait(false);
        Assert.True(Assert.Single(adjacent.Units).IsAvailable);

        using (HttpResponseMessage overlap = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/inventory/properties/{PropertyA:D}/blocks",
                   operatorTokens.AccessToken,
                   new
                   {
                       operationId = Guid.NewGuid(),
                       inventoryUnitId = RoomA,
                       arrival = "2026-08-02",
                       departure = "2026-08-04",
                       reason = "Duplicate"
                   }).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.Conflict, overlap).ConfigureAwait(false);
            AssertNoStore(overlap);
        }

        using (HttpResponseMessage otherProperty = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/inventory/properties/{PropertyB:D}/rooms",
                   operatorTokens.AccessToken).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.Forbidden, otherProperty).ConfigureAwait(false);
        }

        using (HttpResponseMessage staleRelease = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/inventory/properties/{PropertyA:D}/blocks/{block.BlockId:D}/release",
                   operatorTokens.AccessToken,
                   new
                   {
                       operationId = Guid.NewGuid(),
                       expectedVersion = 99
                   }).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.Conflict, staleRelease).ConfigureAwait(false);
        }

        using (HttpResponseMessage release = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/inventory/properties/{PropertyA:D}/blocks/{block.BlockId:D}/release",
                   operatorTokens.AccessToken,
                   new
                   {
                       operationId = Guid.NewGuid(),
                       expectedVersion = 1
                   }).ConfigureAwait(false))
        {
            ManualInventoryBlockMutationReceiptDto released =
                await ReadSuccessAsync<ManualInventoryBlockMutationReceiptDto>(release).ConfigureAwait(false);
            Assert.Equal(block.BlockId, released.BlockId);
            Assert.Equal(block.BlockGroupId, released.BlockGroupId);
            Assert.Equal(PropertyA, released.PropertyId);
            Assert.Equal(ManualInventoryBlockStatus.Released, released.Status);
            Assert.Equal(2, released.Version);
        }

        await AddSecondPropertyARoomAsync(api).ConfigureAwait(false);
        using (HttpResponseMessage configureSecondRoom = await SendAsync(
                   client,
                   HttpMethod.Put,
                   $"/api/inventory/properties/{PropertyA:D}/rooms/{RoomA2:D}/sales-mode",
                   operatorTokens.AccessToken,
                   new
                   {
                       operationId = Guid.NewGuid(),
                       salesMode = InventorySalesMode.RoomLevel,
                       expectedVersion = 1
                   }).ConfigureAwait(false))
        {
            RoomInventoryMutationReceiptDto receipt =
                await ReadSuccessAsync<RoomInventoryMutationReceiptDto>(configureSecondRoom)
                    .ConfigureAwait(false);
            Assert.Equal(RoomA2, receipt.RoomId);
            Assert.Equal(InventorySalesMode.RoomLevel, receipt.SalesMode);
        }

        ManualInventoryBlockGroupSelectionPreviewDto blockGroupPreview;
        using (HttpResponseMessage previewBlockGroup = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/inventory/properties/{PropertyA:D}/block-groups/preview",
                   operatorTokens.AccessToken,
                   new
                   {
                       target = new { kind = InventoryBlockTargetKind.Property },
                       arrival = "2026-08-05",
                       departure = "2026-08-07",
                       reason = "Property maintenance"
                   }).ConfigureAwait(false))
        {
            blockGroupPreview =
                await ReadSuccessAsync<ManualInventoryBlockGroupSelectionPreviewDto>(previewBlockGroup)
                    .ConfigureAwait(false);
            Assert.Equal(ManualInventoryBlockGroupPreviewStatus.Ready, blockGroupPreview.Status);
            Assert.Equal(2, blockGroupPreview.AffectedBlockCount);
            Assert.Equal(500, blockGroupPreview.MaximumAffectedBlockCount);
            Assert.False(blockGroupPreview.ExceedsMaximumAffectedBlockCount);
            Assert.NotNull(blockGroupPreview.SelectionDigest);
            Assert.False(blockGroupPreview.HasMoreMembers);
            Assert.Equal(2, blockGroupPreview.Members.Count);
            AssertNoStore(previewBlockGroup);
        }

        Guid blockGroupCreateOperationId = Guid.NewGuid();
        ManualInventoryBlockGroupMutationReceiptDto blockGroup;
        using (HttpResponseMessage createBlockGroup = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/inventory/properties/{PropertyA:D}/block-groups",
                   operatorTokens.AccessToken,
                   new
                   {
                       operationId = blockGroupCreateOperationId,
                       target = new { kind = InventoryBlockTargetKind.Property },
                       arrival = "2026-08-05",
                       departure = "2026-08-07",
                       reason = "Property maintenance",
                       expectedSelectionDigest = blockGroupPreview.SelectionDigest,
                       expectedAffectedBlockCount = blockGroupPreview.AffectedBlockCount,
                       confirmed = true
                   }).ConfigureAwait(false))
        {
            blockGroup =
                await ReadSuccessAsync<ManualInventoryBlockGroupMutationReceiptDto>(createBlockGroup).ConfigureAwait(false);
            Assert.NotEqual(Guid.Empty, blockGroup.BlockGroupId);
            Assert.Equal(PropertyA, blockGroup.PropertyId);
            Assert.Equal(2, blockGroup.AffectedBlockCount);
            Assert.Equal(ManualInventoryBlockGroupStatus.Active, blockGroup.Status);
            Assert.Equal(1, blockGroup.Version);
            Assert.Equal(2, blockGroup.TotalBlockCount);
            Assert.Equal(2, blockGroup.ActiveBlockCount);
            Assert.Equal(2, blockGroup.CreatedNowBlockCount);
            Assert.Equal(blockGroupPreview.MembershipDigest, blockGroup.MembershipDigest);
        }

        using (HttpResponseMessage getBlockGroup = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/inventory/properties/{PropertyA:D}/block-groups/{blockGroup.BlockGroupId:D}",
                   operatorTokens.AccessToken).ConfigureAwait(false))
        {
            ManualInventoryBlockGroupDto group =
                await ReadSuccessAsync<ManualInventoryBlockGroupDto>(getBlockGroup).ConfigureAwait(false);
            Assert.Equal(blockGroup.BlockGroupId, group.BlockGroupId);
            Assert.Equal(2, group.ActiveBlockCount);
            Assert.Equal($"user:{operatorId:D}", group.CreatedByActorId);
        }

        ManualInventoryBlockGroupDto firstListedGroup;
        string groupCursor;
        using (HttpResponseMessage listBlockGroups = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/inventory/properties/{PropertyA:D}/block-groups?pageSize=1",
                   operatorTokens.AccessToken).ConfigureAwait(false))
        {
            ManualInventoryBlockGroupListResponse groups =
                await ReadSuccessAsync<ManualInventoryBlockGroupListResponse>(listBlockGroups).ConfigureAwait(false);
            firstListedGroup = Assert.Single(groups.BlockGroups);
            Assert.Equal(blockGroup.BlockGroupId, firstListedGroup.BlockGroupId);
            groupCursor = Assert.IsType<string>(groups.NextCursor);
            Assert.False(Guid.TryParse(groupCursor, out _));
        }

        using (HttpResponseMessage listNextBlockGroup = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/inventory/properties/{PropertyA:D}/block-groups?pageSize=1&cursor={Uri.EscapeDataString(groupCursor)}",
                   operatorTokens.AccessToken).ConfigureAwait(false))
        {
            ManualInventoryBlockGroupListResponse groups =
                await ReadSuccessAsync<ManualInventoryBlockGroupListResponse>(listNextBlockGroup)
                    .ConfigureAwait(false);
            ManualInventoryBlockGroupDto historicalGroup = Assert.Single(groups.BlockGroups);
            Assert.Equal(block.BlockGroupId, historicalGroup.BlockGroupId);
            Assert.NotEqual(firstListedGroup.BlockGroupId, historicalGroup.BlockGroupId);
            Assert.True(firstListedGroup.CreatedAtUtc >= historicalGroup.CreatedAtUtc);
            Assert.Null(groups.NextCursor);
        }

        ManualInventoryBlockDto firstMember;
        string memberCursor;
        using (HttpResponseMessage listBlockGroupMembers = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/inventory/properties/{PropertyA:D}/block-groups/{blockGroup.BlockGroupId:D}/members?pageSize=1",
                   operatorTokens.AccessToken).ConfigureAwait(false))
        {
            ManualInventoryBlockGroupMemberListResponse members =
                await ReadSuccessAsync<ManualInventoryBlockGroupMemberListResponse>(listBlockGroupMembers)
                    .ConfigureAwait(false);
            firstMember = Assert.Single(members.Blocks);
            memberCursor = Assert.IsType<string>(members.NextCursor);
            Assert.False(Guid.TryParse(memberCursor, out _));
        }

        using (HttpResponseMessage listNextBlockGroupMember = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/inventory/properties/{PropertyA:D}/block-groups/{blockGroup.BlockGroupId:D}/members?pageSize=1&cursor={Uri.EscapeDataString(memberCursor)}",
                   operatorTokens.AccessToken).ConfigureAwait(false))
        {
            ManualInventoryBlockGroupMemberListResponse members =
                await ReadSuccessAsync<ManualInventoryBlockGroupMemberListResponse>(listNextBlockGroupMember)
                    .ConfigureAwait(false);
            ManualInventoryBlockDto secondMember = Assert.Single(members.Blocks);
            Assert.NotEqual(firstMember.BlockId, secondMember.BlockId);
            Assert.Null(members.NextCursor);
        }

        using (HttpResponseMessage recoverCreate = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/inventory/properties/{PropertyA:D}/block-group-create-operations/{blockGroupCreateOperationId:D}",
                   operatorTokens.AccessToken).ConfigureAwait(false))
        {
            ManualInventoryBlockGroupOperationDto operation =
                await ReadSuccessAsync<ManualInventoryBlockGroupOperationDto>(recoverCreate).ConfigureAwait(false);
            Assert.Equal(ManualInventoryBlockGroupOperationKind.Create, operation.Kind);
            Assert.Equal(ManualInventoryBlockGroupOperationStatus.Applied, operation.Status);
            Assert.Equal(blockGroup.BlockGroupId, operation.Receipt.ResultBlockGroupId);
        }

        InventoryAvailabilityResponse groupBlocked = await GetAvailabilityAsync(
            client,
            operatorTokens.AccessToken,
            "2026-08-05",
            "2026-08-06").ConfigureAwait(false);
        Assert.Equal(2, groupBlocked.Units.Count);
        Assert.All(groupBlocked.Units, unit => Assert.False(unit.IsAvailable));

        using (HttpResponseMessage partialRelease = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/inventory/properties/{PropertyA:D}/blocks/{firstMember.BlockId:D}/release",
                   operatorTokens.AccessToken,
                   new
                   {
                       operationId = Guid.NewGuid(),
                       expectedVersion = firstMember.Version
                   }).ConfigureAwait(false))
        {
            ManualInventoryBlockMutationReceiptDto receipt =
                await ReadSuccessAsync<ManualInventoryBlockMutationReceiptDto>(partialRelease)
                    .ConfigureAwait(false);
            Assert.Equal(ManualInventoryBlockStatus.Released, receipt.Status);
            Assert.Equal(2, receipt.Version);
        }

        ManualInventoryBlockGroupDto partiallyReleasedGroup;
        using (HttpResponseMessage getPartiallyReleasedGroup = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/inventory/properties/{PropertyA:D}/block-groups/{blockGroup.BlockGroupId:D}",
                   operatorTokens.AccessToken).ConfigureAwait(false))
        {
            partiallyReleasedGroup =
                await ReadSuccessAsync<ManualInventoryBlockGroupDto>(getPartiallyReleasedGroup)
                    .ConfigureAwait(false);
            Assert.Equal(ManualInventoryBlockGroupStatus.PartiallyReleased, partiallyReleasedGroup.Status);
            Assert.Equal(2, partiallyReleasedGroup.InitialBlockCount);
            Assert.Equal(1, partiallyReleasedGroup.ActiveBlockCount);
            Assert.True(partiallyReleasedGroup.Version > blockGroup.Version);
        }

        Guid blockGroupReleaseOperationId = Guid.NewGuid();
        using (HttpResponseMessage releaseBlockGroup = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/inventory/properties/{PropertyA:D}/block-groups/{blockGroup.BlockGroupId:D}/release",
                   operatorTokens.AccessToken,
                   new
                   {
                       operationId = blockGroupReleaseOperationId,
                       expectedVersion = partiallyReleasedGroup.Version,
                       confirmed = true
                   }).ConfigureAwait(false))
        {
            ManualInventoryBlockGroupMutationReceiptDto releasedGroup =
                await ReadSuccessAsync<ManualInventoryBlockGroupMutationReceiptDto>(releaseBlockGroup).ConfigureAwait(false);
            Assert.Equal(blockGroup.BlockGroupId, releasedGroup.BlockGroupId);
            Assert.Equal(PropertyA, releasedGroup.PropertyId);
            Assert.Equal(1, releasedGroup.AffectedBlockCount);
            Assert.Equal(ManualInventoryBlockGroupStatus.Released, releasedGroup.Status);
            Assert.True(releasedGroup.Version > partiallyReleasedGroup.Version);
            Assert.Equal(2, releasedGroup.TotalBlockCount);
            Assert.Equal(1, releasedGroup.ReleasedNowBlockCount);
            Assert.Equal(1, releasedGroup.AlreadyReleasedBlockCount);
            Assert.Equal(0, releasedGroup.ActiveBlockCount);
        }

        using (HttpResponseMessage recoverRelease = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/inventory/properties/{PropertyA:D}/block-groups/{blockGroup.BlockGroupId:D}/operations/{blockGroupReleaseOperationId:D}",
                   operatorTokens.AccessToken).ConfigureAwait(false))
        {
            ManualInventoryBlockGroupOperationDto operation =
                await ReadSuccessAsync<ManualInventoryBlockGroupOperationDto>(recoverRelease).ConfigureAwait(false);
            Assert.Equal(ManualInventoryBlockGroupOperationKind.Release, operation.Kind);
            Assert.Equal(ManualInventoryBlockGroupOperationStatus.Applied, operation.Status);
            Assert.Equal(2, operation.Receipt.TotalBlockCount);
            Assert.Equal(1, operation.Receipt.ReleasedNowBlockCount);
            Assert.Equal(1, operation.Receipt.AlreadyReleasedBlockCount);
            Assert.Equal(0, operation.Receipt.ActiveBlockCount);
        }

        InventoryAvailabilityResponse available = await GetAvailabilityAsync(
            client,
            operatorTokens.AccessToken,
            "2026-08-02",
            "2026-08-03").ConfigureAwait(false);
        Assert.Equal(2, available.Units.Count);
        Assert.All(available.Units, unit => Assert.True(unit.IsAvailable));

        using IServiceScope verificationScope = api.Services.CreateScope();
        ITenantContextAccessor tenantContext = verificationScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>();
        tenantContext.SetTenant(TenantA);
        InventoryDbContext dbContext = verificationScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var blockMessages = await dbContext.OutboxMessages
            .AsNoTracking()
            .Where(message =>
                message.EventType == typeof(ManualInventoryBlockCreatedIntegrationEvent).FullName ||
                message.EventType == typeof(ManualInventoryBlockReleasedIntegrationEvent).FullName)
            .OrderBy(message => message.OccurredAtUtc)
            .ToListAsync()
            .ConfigureAwait(false);
        Assert.Equal(6, blockMessages.Count);
        Assert.All(blockMessages, message => Assert.Equal(TenantA, message.ScopeId));

        IInventoryAvailabilityProjectionExportSource exportSource = verificationScope.ServiceProvider
            .GetRequiredService<IInventoryAvailabilityProjectionExportSource>();
        ProjectionReadBatch<InventoryAvailabilityProjectionExport> export = await exportSource.ReadAsync(
            new ProjectionRebuildRequest("reservations-availability", projectionVersion: 1, batchSize: 10),
            cursor: null,
            CancellationToken.None).ConfigureAwait(false);
        InventoryAvailabilityProjectionExport propertyExport = Assert.Single(
            export.Snapshots,
            snapshot => snapshot.PropertyId == PropertyA);
        Assert.Equal(2, propertyExport.Units.Count);
        Assert.All(propertyExport.Units, unit => Assert.True(unit.IsSellable));
        Assert.Equal(3, propertyExport.Units.Sum(unit => unit.Blocks.Count));
        Assert.All(
            propertyExport.Units.SelectMany(unit => unit.Blocks),
            exportedBlock => Assert.Equal(ManualInventoryBlockStatus.Released, exportedBlock.Status));

        await ExerciseAdminNoStoreBoundaryAsync(
            connectionString,
            natsConnectionString,
            adminOperatorId).ConfigureAwait(false);
        await ExerciseInventoryAdminCliBlockGroupsAsync(
            admin,
            cliOperatorId).ConfigureAwait(false);
    }

    private static async Task ExerciseAdminNoStoreBoundaryAsync(
        string connectionString,
        string natsConnectionString,
        Guid adminOperatorId)
    {
        await using AdminApiTestApplication adminApi = new(
            "PostgreSql",
            connectionString,
            natsConnectionString);
        string groupsPath =
            $"/api/admin/inventory/properties/{PropertyA:D}/block-groups";

        using HttpClient anonymous = adminApi.CreateClient();
        using (HttpResponseMessage response = await anonymous.GetAsync(groupsPath)
                   .ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.Unauthorized, response).ConfigureAwait(false);
            AssertNoStore(response);
        }

        using HttpClient denied = adminApi.CreateClient();
        denied.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            AdminApiTestApplication.CreateAccessTokenWithTenantClaim(
                Guid.NewGuid(),
                TenantA));
        denied.DefaultRequestHeaders.Add(TenantHeader, TenantA);
        using (HttpResponseMessage response = await denied.GetAsync(groupsPath)
                   .ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.Forbidden, response).ConfigureAwait(false);
            AssertNoStore(response);
        }

        using HttpClient authorized = adminApi.CreateClient();
        authorized.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            AdminApiTestApplication.CreateAccessTokenWithTenantClaim(
                adminOperatorId,
                TenantA));
        authorized.DefaultRequestHeaders.Add(TenantHeader, TenantA);
        using (HttpResponseMessage response = await authorized.GetAsync(
                   $"{groupsPath}?status=not-a-status").ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.BadRequest, response).ConfigureAwait(false);
            AssertNoStore(response);
        }

        using (HttpResponseMessage response = await authorized.GetAsync(groupsPath)
                   .ConfigureAwait(false))
        {
            _ = await ReadSuccessAsync<ManualInventoryBlockGroupListResponse>(response)
                .ConfigureAwait(false);
            AssertNoStore(response);
        }

        using (HttpResponseMessage response = await authorized.GetAsync(
                   $"{groupsPath}/{Guid.NewGuid():D}").ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.NotFound, response).ConfigureAwait(false);
            AssertNoStore(response);
        }

        var adminCreateDefinition = new
        {
            target = new { kind = InventoryBlockTargetKind.Property },
            arrival = "2026-09-01",
            departure = "2026-09-03",
            reason = "Admin property maintenance"
        };
        ManualInventoryBlockGroupSelectionPreviewDto createPreview;
        using (HttpResponseMessage response = await authorized.PostAsJsonAsync(
                   $"{groupsPath}/preview",
                   adminCreateDefinition).ConfigureAwait(false))
        {
            createPreview = await ReadSuccessAsync<
                    ManualInventoryBlockGroupSelectionPreviewDto>(response)
                .ConfigureAwait(false);
            Assert.Equal(ManualInventoryBlockGroupPreviewStatus.Ready, createPreview.Status);
            Assert.Equal(2, createPreview.AffectedBlockCount);
            AssertNoStore(response);
        }

        using (HttpResponseMessage response = await authorized.PostAsJsonAsync(
                   $"/api/admin/inventory/properties/{PropertyB:D}/block-groups/preview",
                   adminCreateDefinition).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.Forbidden, response)
                .ConfigureAwait(false);
            AssertNoStore(response);
        }

        Guid createOperationId = Guid.NewGuid();
        var createRequest = new
        {
            operationId = createOperationId,
            adminCreateDefinition.target,
            adminCreateDefinition.arrival,
            adminCreateDefinition.departure,
            adminCreateDefinition.reason,
            expectedSelectionDigest = createPreview.SelectionDigest,
            expectedAffectedBlockCount = createPreview.AffectedBlockCount,
            confirmed = false
        };
        using (HttpResponseMessage response = await authorized.PostAsJsonAsync(
                   groupsPath,
                   createRequest).ConfigureAwait(false))
        {
            await AssertProblemAsync(
                HttpStatusCode.BadRequest,
                InventoryApplicationErrors.BlockGroupConfirmationRequired.Code,
                response).ConfigureAwait(false);
            AssertNoStore(response);
        }

        using (HttpResponseMessage response = await authorized.GetAsync(
                   $"/api/admin/inventory/properties/{PropertyA:D}/block-group-create-operations/{createOperationId:D}")
                   .ConfigureAwait(false))
        {
            await AssertProblemAsync(
                HttpStatusCode.NotFound,
                InventoryApplicationErrors.BlockGroupOperationNotFound.Code,
                response).ConfigureAwait(false);
            AssertNoStore(response);
        }

        ManualInventoryBlockGroupMutationReceiptDto created;
        using (HttpResponseMessage response = await authorized.PostAsJsonAsync(
                   groupsPath,
                   createRequest with { confirmed = true }).ConfigureAwait(false))
        {
            created = await ReadSuccessAsync<
                    ManualInventoryBlockGroupMutationReceiptDto>(response)
                .ConfigureAwait(false);
            Assert.Equal(2, created.CreatedNowBlockCount);
            Assert.Equal(2, created.ActiveBlockCount);
            Assert.Equal(ManualInventoryBlockGroupStatus.Active, created.Status);
            Assert.Equal(1L, created.Version);
            AssertNoStore(response);
        }

        using (HttpResponseMessage response = await authorized.GetAsync(
                   $"{groupsPath}/{created.ResultBlockGroupId:D}").ConfigureAwait(false))
        {
            ManualInventoryBlockGroupDto group =
                await ReadSuccessAsync<ManualInventoryBlockGroupDto>(response)
                    .ConfigureAwait(false);
            Assert.Equal(
                $"admin-api:{adminOperatorId:D}",
                group.CreatedByActorId);
            Assert.Equal(group.CreatedByActorId, group.LastModifiedByActorId);
            AssertNoStore(response);
        }

        using (HttpResponseMessage response = await authorized.GetAsync(
                   $"/api/admin/inventory/properties/{PropertyA:D}/block-group-create-operations/{createOperationId:D}")
                   .ConfigureAwait(false))
        {
            ManualInventoryBlockGroupOperationDto recovery =
                await ReadSuccessAsync<ManualInventoryBlockGroupOperationDto>(response)
                    .ConfigureAwait(false);
            Assert.Equal(ManualInventoryBlockGroupOperationKind.Create, recovery.Kind);
            Assert.Equal(created, recovery.Receipt);
            AssertNoStore(response);
        }

        var adminReplacementDefinition = new
        {
            target = new
            {
                kind = InventoryBlockTargetKind.Room,
                roomId = RoomA
            },
            adminCreateDefinition.arrival,
            adminCreateDefinition.departure,
            reason = "Admin room maintenance",
            blockGroupId = created.ResultBlockGroupId,
            expectedVersion = created.Version
        };
        ManualInventoryBlockGroupSelectionPreviewDto replacementPreview;
        using (HttpResponseMessage response = await authorized.PostAsJsonAsync(
                   $"{groupsPath}/preview",
                   adminReplacementDefinition).ConfigureAwait(false))
        {
            replacementPreview = await ReadSuccessAsync<
                    ManualInventoryBlockGroupSelectionPreviewDto>(response)
                .ConfigureAwait(false);
            Assert.Equal(1, replacementPreview.AffectedBlockCount);
            Assert.False(replacementPreview.IsNoOpReplacement);
            AssertNoStore(response);
        }

        Guid replaceOperationId = Guid.NewGuid();
        ManualInventoryBlockGroupMutationReceiptDto replaced;
        using (HttpResponseMessage response = await authorized.PutAsJsonAsync(
                   $"{groupsPath}/{created.ResultBlockGroupId:D}",
                   new
                   {
                       operationId = replaceOperationId,
                       expectedVersion = created.Version,
                       adminReplacementDefinition.target,
                       adminReplacementDefinition.arrival,
                       adminReplacementDefinition.departure,
                       adminReplacementDefinition.reason,
                       expectedSelectionDigest = replacementPreview.SelectionDigest,
                       expectedAffectedBlockCount = replacementPreview.AffectedBlockCount,
                       confirmed = true
                   }).ConfigureAwait(false))
        {
            replaced = await ReadSuccessAsync<
                    ManualInventoryBlockGroupMutationReceiptDto>(response)
                .ConfigureAwait(false);
            Assert.Equal(created.ResultBlockGroupId, replaced.PreviousBlockGroupId);
            Assert.NotEqual(created.ResultBlockGroupId, replaced.ResultBlockGroupId);
            Assert.Equal(2, replaced.ReleasedNowBlockCount);
            Assert.Equal(1, replaced.CreatedNowBlockCount);
            Assert.Equal(1, replaced.ActiveBlockCount);
            AssertNoStore(response);
        }

        using (HttpResponseMessage response = await authorized.GetAsync(
                   $"{groupsPath}/{created.ResultBlockGroupId:D}/operations/{replaceOperationId:D}")
                   .ConfigureAwait(false))
        {
            ManualInventoryBlockGroupOperationDto recovery =
                await ReadSuccessAsync<ManualInventoryBlockGroupOperationDto>(response)
                    .ConfigureAwait(false);
            Assert.Equal(ManualInventoryBlockGroupOperationKind.Replace, recovery.Kind);
            Assert.Equal(replaced, recovery.Receipt);
            AssertNoStore(response);
        }

        Guid releaseOperationId = Guid.NewGuid();
        var releaseRequest = new
        {
            operationId = releaseOperationId,
            expectedVersion = replaced.Version,
            confirmed = false
        };
        string successorPath = $"{groupsPath}/{replaced.ResultBlockGroupId:D}";
        using (HttpResponseMessage response = await authorized.PostAsJsonAsync(
                   $"{successorPath}/release",
                   releaseRequest).ConfigureAwait(false))
        {
            await AssertProblemAsync(
                HttpStatusCode.BadRequest,
                InventoryApplicationErrors.BlockGroupConfirmationRequired.Code,
                response).ConfigureAwait(false);
            AssertNoStore(response);
        }

        ManualInventoryBlockGroupMutationReceiptDto released;
        using (HttpResponseMessage response = await authorized.PostAsJsonAsync(
                   $"{successorPath}/release",
                   releaseRequest with { confirmed = true }).ConfigureAwait(false))
        {
            released = await ReadSuccessAsync<
                    ManualInventoryBlockGroupMutationReceiptDto>(response)
                .ConfigureAwait(false);
            Assert.Equal(ManualInventoryBlockGroupStatus.Released, released.Status);
            Assert.Equal(1, released.ReleasedNowBlockCount);
            Assert.Equal(0, released.ActiveBlockCount);
            AssertNoStore(response);
        }

        using (HttpResponseMessage response = await authorized.GetAsync(
                   $"{successorPath}/operations/{releaseOperationId:D}")
                   .ConfigureAwait(false))
        {
            ManualInventoryBlockGroupOperationDto recovery =
                await ReadSuccessAsync<ManualInventoryBlockGroupOperationDto>(response)
                    .ConfigureAwait(false);
            Assert.Equal(ManualInventoryBlockGroupOperationKind.Release, recovery.Kind);
            Assert.Equal(released, recovery.Receipt);
            AssertNoStore(response);
        }

        using (HttpResponseMessage response = await authorized.GetAsync(successorPath)
                   .ConfigureAwait(false))
        {
            ManualInventoryBlockGroupDto group =
                await ReadSuccessAsync<ManualInventoryBlockGroupDto>(response)
                    .ConfigureAwait(false);
            Assert.Equal(ManualInventoryBlockGroupStatus.Released, group.Status);
            Assert.Equal($"admin-api:{adminOperatorId:D}", group.CreatedByActorId);
            Assert.Equal($"admin-api:{adminOperatorId:D}", group.LastModifiedByActorId);
            AssertNoStore(response);
        }

        Assert.Equal(
            2,
            await adminApi.CountAuditEntriesAsync(
                InventoryAdminOperationNames.BlockGroupsCreate).ConfigureAwait(false));
        Assert.Equal(
            1,
            await adminApi.CountAuditEntriesAsync(
                InventoryAdminOperationNames.BlockGroupsCreate,
                InventoryApplicationErrors.BlockGroupConfirmationRequired.Code)
                .ConfigureAwait(false));
        Assert.Equal(
            1,
            await adminApi.CountAuditEntriesAsync(
                InventoryAdminOperationNames.BlockGroupsReplace).ConfigureAwait(false));
        Assert.Equal(
            2,
            await adminApi.CountAuditEntriesAsync(
                InventoryAdminOperationNames.BlockGroupsRelease).ConfigureAwait(false));
        Assert.Equal(
            1,
            await adminApi.CountAuditEntriesAsync(
                InventoryAdminOperationNames.BlockGroupsRelease,
                InventoryApplicationErrors.BlockGroupConfirmationRequired.Code)
                .ConfigureAwait(false));
        Assert.True(
            await adminApi.CountAuditEntriesContainingAsync(
                adminOperatorId.ToString("D")).ConfigureAwait(false) >= 10);
    }

    private static async Task ExerciseInventoryAdminCliBlockGroupsAsync(
        AdminCliTestApplication admin,
        Guid cliOperatorId)
    {
        const string arrival = "2028-10-01";
        const string departure = "2028-10-03";
        const string createReason = "CLI room maintenance";
        string actorId = cliOperatorId.ToString("D");

        AdminCliResult forbidden = await ExecuteInventoryBlockGroupCliAsync(
            admin,
            actorId,
            "preview",
            "--property-id", PropertyB.ToString("D"),
            "--target-kind", InventoryBlockTargetKind.Property.ToString(),
            "--arrival", arrival,
            "--departure", departure,
            "--reason", "Out of scope").ConfigureAwait(false);
        Assert.Equal(AdminExitCodes.Unauthorized, forbidden.ExitCode);
        Assert.Contains(
            AdminErrors.Unauthorized.Message,
            forbidden.Error,
            StringComparison.Ordinal);

        ManualInventoryBlockGroupSelectionPreviewDto createPreview =
            ReadAdminCliJson<ManualInventoryBlockGroupSelectionPreviewDto>(
                await AssertAdminSuccessAsync(
                    ExecuteInventoryBlockGroupCliAsync(
                        admin,
                        actorId,
                        "preview",
                        "--property-id", PropertyA.ToString("D"),
                        "--target-kind", InventoryBlockTargetKind.Room.ToString(),
                        "--room-id", RoomA.ToString("D"),
                        "--arrival", arrival,
                        "--departure", departure,
                        "--reason", createReason)).ConfigureAwait(false));
        Assert.Equal(ManualInventoryBlockGroupPreviewStatus.Ready, createPreview.Status);
        Assert.Equal(1, createPreview.AffectedBlockCount);
        Assert.NotNull(createPreview.SelectionDigest);

        Guid createOperationId = Guid.NewGuid();
        string[] createArguments =
        [
            "create",
            "--operation-id", createOperationId.ToString("D"),
            "--property-id", PropertyA.ToString("D"),
            "--target-kind", InventoryBlockTargetKind.Room.ToString(),
            "--room-id", RoomA.ToString("D"),
            "--arrival", arrival,
            "--departure", departure,
            "--reason", createReason,
            "--selection-digest", createPreview.SelectionDigest!,
            "--expected-affected-count", createPreview.AffectedBlockCount!.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
        ];
        AdminCliResult unconfirmedCreate =
            await ExecuteInventoryBlockGroupCliAsync(
                admin,
                actorId,
                createArguments).ConfigureAwait(false);
        Assert.Equal(AdminExitCodes.Failed, unconfirmedCreate.ExitCode);
        Assert.Contains(
            InventoryApplicationErrors.BlockGroupConfirmationRequired.Code,
            unconfirmedCreate.Error,
            StringComparison.Ordinal);

        AdminCliResult unboundCreateRecovery =
            await ExecuteInventoryBlockGroupCliAsync(
                admin,
                actorId,
                "get-create-operation",
                "--property-id", PropertyA.ToString("D"),
                "--operation-id", createOperationId.ToString("D"))
                .ConfigureAwait(false);
        Assert.Equal(AdminExitCodes.Failed, unboundCreateRecovery.ExitCode);
        Assert.Contains(
            InventoryApplicationErrors.BlockGroupOperationNotFound.Code,
            unboundCreateRecovery.Error,
            StringComparison.Ordinal);

        ManualInventoryBlockGroupMutationReceiptDto created =
            ReadAdminCliJson<ManualInventoryBlockGroupMutationReceiptDto>(
                await AssertAdminSuccessAsync(
                    ExecuteInventoryBlockGroupCliAsync(
                        admin,
                        actorId,
                        [.. createArguments, "--yes"])).ConfigureAwait(false));
        Assert.Equal(1, created.CreatedNowBlockCount);
        Assert.Equal(1, created.ActiveBlockCount);
        Assert.Equal(ManualInventoryBlockGroupStatus.Active, created.Status);
        Assert.Equal(1L, created.Version);

        ManualInventoryBlockGroupOperationDto createRecovery =
            ReadAdminCliJson<ManualInventoryBlockGroupOperationDto>(
                await AssertAdminSuccessAsync(
                    ExecuteInventoryBlockGroupCliAsync(
                        admin,
                        actorId,
                        "get-create-operation",
                        "--property-id", PropertyA.ToString("D"),
                        "--operation-id", createOperationId.ToString("D")))
                    .ConfigureAwait(false));
        Assert.Equal(ManualInventoryBlockGroupOperationKind.Create, createRecovery.Kind);
        Assert.Equal(created, createRecovery.Receipt);

        const string replacementReason = "CLI replacement maintenance";
        ManualInventoryBlockGroupSelectionPreviewDto replacementPreview =
            ReadAdminCliJson<ManualInventoryBlockGroupSelectionPreviewDto>(
                await AssertAdminSuccessAsync(
                    ExecuteInventoryBlockGroupCliAsync(
                        admin,
                        actorId,
                        "preview",
                        "--property-id", PropertyA.ToString("D"),
                        "--block-group-id", created.ResultBlockGroupId.ToString("D"),
                        "--expected-version", created.Version!.Value.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        "--target-kind", InventoryBlockTargetKind.Room.ToString(),
                        "--room-id", RoomA2.ToString("D"),
                        "--arrival", arrival,
                        "--departure", departure,
                        "--reason", replacementReason)).ConfigureAwait(false));
        Assert.Equal(1, replacementPreview.AffectedBlockCount);
        Assert.False(replacementPreview.IsNoOpReplacement);

        Guid replaceOperationId = Guid.NewGuid();
        ManualInventoryBlockGroupMutationReceiptDto replaced =
            ReadAdminCliJson<ManualInventoryBlockGroupMutationReceiptDto>(
                await AssertAdminSuccessAsync(
                    ExecuteInventoryBlockGroupCliAsync(
                        admin,
                        actorId,
                        "replace",
                        "--operation-id", replaceOperationId.ToString("D"),
                        "--property-id", PropertyA.ToString("D"),
                        "--block-group-id", created.ResultBlockGroupId.ToString("D"),
                        "--expected-version", created.Version.Value.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        "--target-kind", InventoryBlockTargetKind.Room.ToString(),
                        "--room-id", RoomA2.ToString("D"),
                        "--arrival", arrival,
                        "--departure", departure,
                        "--reason", replacementReason,
                        "--selection-digest", replacementPreview.SelectionDigest!,
                        "--expected-affected-count", replacementPreview.AffectedBlockCount!.Value.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        "--yes")).ConfigureAwait(false));
        Assert.Equal(created.ResultBlockGroupId, replaced.PreviousBlockGroupId);
        Assert.NotEqual(created.ResultBlockGroupId, replaced.ResultBlockGroupId);
        Assert.Equal(1, replaced.ReleasedNowBlockCount);
        Assert.Equal(1, replaced.CreatedNowBlockCount);

        ManualInventoryBlockGroupOperationDto replaceRecovery =
            ReadAdminCliJson<ManualInventoryBlockGroupOperationDto>(
                await AssertAdminSuccessAsync(
                    ExecuteInventoryBlockGroupCliAsync(
                        admin,
                        actorId,
                        "get-operation",
                        "--property-id", PropertyA.ToString("D"),
                        "--block-group-id", created.ResultBlockGroupId.ToString("D"),
                        "--operation-id", replaceOperationId.ToString("D")))
                    .ConfigureAwait(false));
        Assert.Equal(ManualInventoryBlockGroupOperationKind.Replace, replaceRecovery.Kind);
        Assert.Equal(replaced, replaceRecovery.Receipt);

        Guid releaseOperationId = Guid.NewGuid();
        string[] releaseArguments =
        [
            "release",
            "--operation-id", releaseOperationId.ToString("D"),
            "--property-id", PropertyA.ToString("D"),
            "--block-group-id", replaced.ResultBlockGroupId.ToString("D"),
            "--expected-version", replaced.Version!.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
        ];
        AdminCliResult unconfirmedRelease =
            await ExecuteInventoryBlockGroupCliAsync(
                admin,
                actorId,
                releaseArguments).ConfigureAwait(false);
        Assert.Equal(AdminExitCodes.Failed, unconfirmedRelease.ExitCode);
        Assert.Contains(
            InventoryApplicationErrors.BlockGroupConfirmationRequired.Code,
            unconfirmedRelease.Error,
            StringComparison.Ordinal);

        ManualInventoryBlockGroupMutationReceiptDto released =
            ReadAdminCliJson<ManualInventoryBlockGroupMutationReceiptDto>(
                await AssertAdminSuccessAsync(
                    ExecuteInventoryBlockGroupCliAsync(
                        admin,
                        actorId,
                        [.. releaseArguments, "--yes"])).ConfigureAwait(false));
        Assert.Equal(ManualInventoryBlockGroupStatus.Released, released.Status);
        Assert.Equal(1, released.ReleasedNowBlockCount);
        Assert.Equal(0, released.ActiveBlockCount);

        ManualInventoryBlockGroupOperationDto releaseRecovery =
            ReadAdminCliJson<ManualInventoryBlockGroupOperationDto>(
                await AssertAdminSuccessAsync(
                    ExecuteInventoryBlockGroupCliAsync(
                        admin,
                        actorId,
                        "get-operation",
                        "--property-id", PropertyA.ToString("D"),
                        "--block-group-id", replaced.ResultBlockGroupId.ToString("D"),
                        "--operation-id", releaseOperationId.ToString("D")))
                    .ConfigureAwait(false));
        Assert.Equal(ManualInventoryBlockGroupOperationKind.Release, releaseRecovery.Kind);
        Assert.Equal(released, releaseRecovery.Receipt);

        ManualInventoryBlockGroupDto finalGroup =
            ReadAdminCliJson<ManualInventoryBlockGroupDto>(
                await AssertAdminSuccessAsync(
                    ExecuteInventoryBlockGroupCliAsync(
                        admin,
                        actorId,
                        "get",
                        "--property-id", PropertyA.ToString("D"),
                        "--block-group-id", replaced.ResultBlockGroupId.ToString("D")))
                    .ConfigureAwait(false));
        Assert.Equal($"admin-cli:{actorId}", finalGroup.CreatedByActorId);
        Assert.Equal($"admin-cli:{actorId}", finalGroup.LastModifiedByActorId);

        Assert.Equal(
            2,
            await admin.CountAuditEntriesAsync(
                InventoryAdminOperationNames.BlockGroupsCreate,
                actorId: actorId).ConfigureAwait(false));
        Assert.Equal(
            1,
            await admin.CountAuditEntriesAsync(
                InventoryAdminOperationNames.BlockGroupsCreate,
                InventoryApplicationErrors.BlockGroupConfirmationRequired.Code,
                actorId)
                .ConfigureAwait(false));
        Assert.Equal(
            1,
            await admin.CountAuditEntriesAsync(
                InventoryAdminOperationNames.BlockGroupsReplace,
                actorId: actorId).ConfigureAwait(false));
        Assert.Equal(
            2,
            await admin.CountAuditEntriesAsync(
                InventoryAdminOperationNames.BlockGroupsRelease,
                actorId: actorId).ConfigureAwait(false));
        Assert.Equal(
            1,
            await admin.CountAuditEntriesAsync(
                InventoryAdminOperationNames.BlockGroupsRelease,
                InventoryApplicationErrors.BlockGroupConfirmationRequired.Code,
                actorId)
                .ConfigureAwait(false));
        Assert.True(
            await admin.CountAuditEntriesContainingAsync(actorId)
                .ConfigureAwait(false) >= 10);
    }

    private static Task<AdminCliResult> ExecuteInventoryBlockGroupCliAsync(
        AdminCliTestApplication admin,
        string actorId,
        params string[] arguments) => admin.ExecuteAsync(
            [
                "inventory",
                "block-groups",
                .. arguments,
                "--tenant", TenantA,
                "--actor", actorId,
                "--output", "json"
            ]);

    private static T ReadAdminCliJson<T>(AdminCliResult result)
    {
        T? value = JsonSerializer.Deserialize<T>(result.Output, JsonOptions);
        Assert.NotNull(value);
        return value;
    }

    private static async Task AddSecondPropertyARoomAsync(AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantA);
        IIntegrationEventHandler<RoomCreatedIntegrationEvent> roomHandler =
            ResolveInventoryHandler<RoomCreatedIntegrationEvent>(scope.ServiceProvider);
        InventoryDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync()
            .ConfigureAwait(false);
        await roomHandler.HandleAsync(
            new(
                Guid.NewGuid(),
                TenantA,
                DateTimeOffset.UtcNow,
                PropertyA,
                RoomA2,
                "102",
                "Main",
                "1",
                RoomStatus.Active,
                1),
            CancellationToken.None).ConfigureAwait(false);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Retirement_context_gets_use_retire_only_and_preserve_exact_target_history_without_writes()
    {
        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await nats.StartAsync();
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_retirement_context_tests").Build();
        await postgres.StartAsync();
        await using AuthTestApplication api = new("PostgreSql", postgres.GetConnectionString(),
            AuthTestContainers.GetNatsConnectionString(nats));
        await api.MigrateInventoryAuthorizationDatabaseAsync();
        await SeedInventoryAsync(api);
        await using AdminCliTestApplication admin = new("PostgreSql", postgres.GetConnectionString());
        await admin.MigrateAsync();
        using HttpClient client = api.CreateClient();
        AuthTokensResponse tokens = await AuthApiClient.RegisterAsync(client, TenantA, "retirement-context@inventory.test");
        Guid subjectId = GetSubjectId(tokens.AccessToken);
        await api.SeedOrganizationMembershipAsync(TenantA, subjectId);
        string roomPath = $"/api/inventory/properties/{PropertyA:D}/rooms/{RoomA:D}/retirement";
        string bedPath = $"/api/inventory/properties/{PropertyB:D}/rooms/{RoomB:D}/beds/{BedB:D}/retirement";
        using (HttpResponseMessage denied = await SendAsync(client, HttpMethod.Get, roomPath, tokens.AccessToken))
        {
            await AssertStatusAsync(HttpStatusCode.Forbidden, denied);
        }
        await AssertAdminSuccessAsync(admin.ExecuteAsync("admin", "bootstrap", "--actor", "owner", "--yes"));
        await AssertAdminSuccessAsync(admin.ExecuteAsync("admin", "roles", "create", "--actor", "owner", "--name", "retirement-reader"));
        await AssertAdminSuccessAsync(admin.ExecuteAsync("admin", "roles", "grant", "--actor", "owner", "--role", "retirement-reader",
            "--permission", InventoryAdminPermissionCodes.Retire));
        foreach (Guid propertyId in new[] { PropertyA, PropertyB, PropertyC })
        {
            await AssertAdminSuccessAsync(admin.ExecuteAsync("admin", "roles", "assign", "--actor", "owner",
                "--target-kind", "user", "--target-id", subjectId.ToString("D"), "--role", "retirement-reader",
                "--scope", $"tenant:{TenantA}/property:{propertyId:D}"));
        }
        foreach (string path in new[] { roomPath, bedPath })
        {
            using HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, path, tokens.AccessToken);
            InventoryRetirementContextDto context = await ReadSuccessAsync<InventoryRetirementContextDto>(response);
            Assert.Null(context.Process);
            Assert.True(context.CanRequest);
            Assert.NotNull(context.Impact);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }
        using (HttpResponseMessage configureDenied = await SendAsync(client, HttpMethod.Get,
                   $"/api/inventory/properties/{PropertyA:D}/rooms/{RoomA:D}/change-impact", tokens.AccessToken))
        {
            await AssertStatusAsync(HttpStatusCode.Forbidden, configureDenied);
        }
        Guid activeRoomId = Guid.NewGuid();
        Guid latestBedId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long beforeOutbox;
        long beforeUnitVersions;
        long beforePropertyEpochs;
        using (IServiceScope scope = api.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
            InventoryDbContext db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            IInventoryAvailabilitySelectionFence selectionFence = scope.ServiceProvider
                .GetRequiredService<IInventoryAvailabilitySelectionFence>();
            foreach (Guid propertyId in new[] { PropertyA, PropertyB })
            {
                await selectionFence.AcquireAsync(propertyId, CancellationToken.None);
                await selectionFence.AdvanceAsync(propertyId, CancellationToken.None);
            }

            await db.SaveChangesAsync();
            foreach (Guid processId in new[] { Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"), latestBedId })
            {
                BedRetirementProcess process = BedRetirementProcess.Create(processId, TenantA, PropertyB, RoomB, BedB,
                    "Dedicated context fixture", "qa-reader", now).Value;
                Assert.True(process.Cancel(1, "Keep the physical bed", "qa-reader", now).IsSuccess);
                db.Add(process);
            }
            RoomRetirementProcess historical = RoomRetirementProcess.Create(Guid.NewGuid(), TenantA, PropertyA, RoomA,
                "Earlier attempt", "qa-reader", now).Value;
            Assert.True(historical.Cancel(1, "Stop earlier attempt", "qa-reader", now).IsSuccess);
            db.Add(historical);
            db.Add(RoomRetirementProcess.Create(activeRoomId, TenantA, PropertyA, RoomA,
                "Active attempt wins timestamp skew", "qa-reader", now.AddDays(-1)).Value);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            beforePropertyEpochs = await db.PropertyTopology.SumAsync(property => property.AvailabilitySelectionVersion);
            beforeOutbox = await db.OutboxMessages.LongCountAsync();
            beforeUnitVersions = await db.InventoryUnits.SumAsync(unit => unit.AvailabilityMutationVersion);
        }
        using (HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, roomPath, tokens.AccessToken))
        {
            InventoryRetirementContextDto context = await ReadSuccessAsync<InventoryRetirementContextDto>(response);
            Assert.Equal(activeRoomId, context.Process?.TopologyChangeId);
            Assert.Equal(InventoryRetirementStatus.Draining, context.Process?.Status);
            Assert.True(context.CanCancel);
            Assert.False(context.CanRequest);
        }
        using (HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, bedPath, tokens.AccessToken))
        {
            InventoryRetirementContextDto context = await ReadSuccessAsync<InventoryRetirementContextDto>(response);
            Assert.Equal(latestBedId, context.Process?.TopologyChangeId);
            Assert.Equal(InventoryRetirementStatus.Canceled, context.Process?.Status);
            Assert.True(context.CanRequest);
        }
        foreach (string path in new[]
                 {
                     $"/api/inventory/properties/{PropertyA:D}/rooms/{RoomB:D}/retirement",
                     $"/api/inventory/properties/{PropertyB:D}/rooms/{RoomA:D}/beds/{BedB:D}/retirement",
                     $"/api/inventory/properties/{PropertyB:D}/rooms/{RoomB:D}/beds/{Guid.NewGuid():D}/retirement"
                 })
        {
            using HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, path, tokens.AccessToken);
            await AssertStatusAsync(HttpStatusCode.NotFound, response);
        }
        using IServiceScope verify = api.Services.CreateScope();
        verify.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
        InventoryDbContext verifiedDb = verify.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Equal(beforeOutbox, await verifiedDb.OutboxMessages.LongCountAsync());
        Assert.Equal(beforePropertyEpochs, await verifiedDb.PropertyTopology.SumAsync(property => property.AvailabilitySelectionVersion));
        Assert.Equal(beforeUnitVersions, await verifiedDb.InventoryUnits.SumAsync(unit => unit.AvailabilityMutationVersion));
        Assert.Equal(2, await verifiedDb.RoomRetirements.CountAsync());
        Assert.Equal(2, await verifiedDb.BedRetirements.CountAsync());
        Assert.Equal(1, await verifiedDb.RoomRetirements.Where(process => process.Id == activeRoomId).Select(process => process.Version).SingleAsync());
    }

    private static async Task SeedInventoryAsync(AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
        InventoryDbContext dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IIntegrationEventHandler<PropertyCreatedIntegrationEvent> propertyHandler =
            ResolveInventoryHandler<PropertyCreatedIntegrationEvent>(scope.ServiceProvider);
        IIntegrationEventHandler<RoomCreatedIntegrationEvent> roomHandler =
            ResolveInventoryHandler<RoomCreatedIntegrationEvent>(scope.ServiceProvider);
        IIntegrationEventHandler<BedAddedIntegrationEvent> bedHandler =
            ResolveInventoryHandler<BedAddedIntegrationEvent>(scope.ServiceProvider);
        await ModuleTransactionIntegrationTestData.ExecuteAsync(
            dbContext,
            async token =>
            {
                await propertyHandler.HandleAsync(
                    new(Guid.NewGuid(), TenantA, now, PropertyA, "Alpha House", "alpha", "UTC", PropertyStatus.Active, 1),
                    token).ConfigureAwait(false);
                await roomHandler.HandleAsync(
                    new(Guid.NewGuid(), TenantA, now, PropertyA, RoomA, "101", "Main", "1", RoomStatus.Active, 1),
                    token).ConfigureAwait(false);
                await propertyHandler.HandleAsync(
                    new(Guid.NewGuid(), TenantA, now, PropertyC, "Out of Order House", "out-of-order", "UTC", PropertyStatus.Active, 1),
                    token).ConfigureAwait(false);
                await bedHandler.HandleAsync(
                    new(Guid.NewGuid(), TenantA, now, PropertyC, RoomC, BedC, "A", BedStatus.Active, roomVersion: 2, bedVersion: 1),
                    token).ConfigureAwait(false);
                await roomHandler.HandleAsync(
                    new(Guid.NewGuid(), TenantA, now, PropertyC, RoomC, "301", "Main", "3", RoomStatus.Active, 1),
                    token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        IProjectionRebuildWriter<PropertyTopologyProjectionExport> rebuildWriter = scope.ServiceProvider
            .GetRequiredService<IProjectionRebuildWriter<PropertyTopologyProjectionExport>>();
        ProjectionWriteResult rebuild = await ModuleTransactionIntegrationTestData.ExecuteAsync(
            dbContext,
            token => rebuildWriter.WriteAsync(
                new ProjectionRebuildRequest(
                    InventoryModuleMetadata.TopologyProjectionName,
                    InventoryModuleMetadata.TopologyProjectionVersion,
                    batchSize: 10),
                [
                    new PropertyTopologyProjectionExport(
                        TenantA,
                        PropertyB,
                        "Beta House",
                        "beta",
                        "UTC",
                        PropertyStatus.Active,
                        1,
                        [
                            new RoomTopologyProjectionExport(
                                PropertyB,
                                RoomB,
                                "201",
                                "Main",
                                "2",
                                RoomStatus.Active,
                                3,
                                [
                                    new BedTopologyProjectionExport(PropertyB, RoomB, BedB, "A", BedStatus.Active, 1),
                                    new BedTopologyProjectionExport(PropertyB, RoomB, BedB2, "B", BedStatus.Active, 1)
                                ])
                        ])
                ],
                token)).ConfigureAwait(false);
        Assert.Equal(1, rebuild.WrittenCount);
        Assert.Equal(6, await dbContext.InventoryUnits.CountAsync().ConfigureAwait(false));
        InventoryRoomTopology outOfOrderRoom = await dbContext.RoomTopology
            .AsNoTracking()
            .SingleAsync(room => room.Id == RoomC)
            .ConfigureAwait(false);
        Assert.Equal(RoomStatus.Active, outOfOrderRoom.Status);
        Assert.Equal(1, outOfOrderRoom.SourceVersion);

        Result<PropertyMutationReceiptDto> propertyCreated = await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .SendAsync(
                new CreatePropertyCommand(
                    PropertyB,
                    "Beta House",
                    "beta",
                    "UTC",
                    "system:inventory-authorization-test"),
                CancellationToken.None)
            .ConfigureAwait(false);
        Assert.True(propertyCreated.IsSuccess, propertyCreated.Error.Code);
        Room room = Room.Create(
            RoomB,
            TenantA,
            PropertyB,
            "201",
            "Main",
            "2",
            Guid.NewGuid(),
            now).Value;
        Assert.True(room.AddBed(BedB, "A", room.Version, Guid.NewGuid(), now).IsSuccess);
        Assert.True(room.AddBed(BedB2, "B", room.Version, Guid.NewGuid(), now).IsSuccess);
        room.ClearDomainEvents();
        PropertiesDbContext propertiesDb = scope.ServiceProvider.GetRequiredService<PropertiesDbContext>();
        await scope.ServiceProvider.GetRequiredService<IRoomRepository>()
            .AddAsync(room, CancellationToken.None).ConfigureAwait(false);
        await propertiesDb.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task ExerciseRoomModeSafetyAsync(
        AuthTestApplication api,
        InventorySelectCounter queryCounter)
    {
        Guid existingReservationId = Guid.Parse("50000000-0000-0000-0000-00000000002b");
        using (IServiceScope setupScope = api.Services.CreateScope())
        {
            setupScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
            IRequestDispatcher dispatcher = setupScope.ServiceProvider
                .GetRequiredService<IRequestDispatcher>();
            Result<RoomInventoryMutationReceiptDto> configured = await dispatcher.SendAsync(
                new ConfigureRoomSalesModeCommand(
                    Guid.NewGuid(),
                    PropertyB,
                    RoomB,
                    InventorySalesMode.BedLevel,
                    ExpectedVersion: 1),
                CancellationToken.None).ConfigureAwait(false);
            Assert.True(configured.IsSuccess);

            InventoryDbContext dbContext = setupScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            InventoryAllocation allocation = InventoryAllocation.CreateAccepted(
                Guid.NewGuid(),
                TenantA,
                Guid.NewGuid(),
                existingReservationId,
                PropertyB,
                new DateOnly(2026, 11, 1),
                new DateOnly(2026, 11, 3),
                [BedB],
                DateTimeOffset.UtcNow).Value;
            dbContext.Allocations.Add(allocation);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);

            IInventoryAvailabilityRepository availability = setupScope.ServiceProvider
                .GetRequiredService<IInventoryAvailabilityRepository>();
            InventoryAvailabilityContextSnapshot roomContext;
            InventoryAvailabilityConflictSnapshot roomConflicts;
            queryCounter.Start();
            roomContext = await availability.GetContextAsync(
                PropertyB, [RoomB], CancellationToken.None);
            queryCounter.Stop();
            Assert.Equal(2, queryCounter.SelectCount);

            queryCounter.Start();
            roomConflicts = await availability.GetConflictsAsync(
                PropertyB,
                roomContext.ConflictUnitIds,
                new(2026, 11, 2),
                new(2026, 11, 4),
                null,
                [],
                CancellationToken.None);
            queryCounter.Stop();
            Assert.Equal(1, queryCounter.SelectCount);

            InventoryAvailabilityContextSnapshot bedContext = await availability.GetContextAsync(
                PropertyB, [BedB], CancellationToken.None);
            InventoryAvailabilityContextSnapshot otherBedContext = await availability.GetContextAsync(
                PropertyB, [BedB2], CancellationToken.None);
            Assert.True(roomConflicts.HasActiveAllocationConflict);
            Assert.True((await availability.GetConflictsAsync(
                PropertyB, bedContext.ConflictUnitIds, new(2026, 11, 2), new(2026, 11, 4), null, [], CancellationToken.None))
                .HasActiveAllocationConflict);
            Assert.False((await availability.GetConflictsAsync(
                PropertyB, otherBedContext.ConflictUnitIds, new(2026, 11, 2), new(2026, 11, 4), null, [], CancellationToken.None))
                .HasActiveAllocationConflict);

            Result<RoomInventoryMutationReceiptDto> blockedModeChange = await dispatcher.SendAsync(
                new ConfigureRoomSalesModeCommand(
                    Guid.NewGuid(),
                    PropertyB,
                    RoomB,
                    InventorySalesMode.RoomLevel,
                    ExpectedVersion: 2),
                CancellationToken.None).ConfigureAwait(false);
            Assert.True(blockedModeChange.IsFailure);
            Assert.Equal(InventoryApplicationErrors.RoomHasActiveClaims.Code, blockedModeChange.Error.Code);

            dbContext.Allocations.Remove(allocation);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        using IServiceScope allocationScope = api.Services.CreateScope();
        using IServiceScope modeScope = api.Services.CreateScope();
        allocationScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
        modeScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
        InventoryDbContext allocationDb = allocationScope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        InventoryDbContext modeDb = modeScope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        await using var allocationTransaction = await allocationDb.Database
            .BeginTransactionAsync()
            .ConfigureAwait(false);
        await using var modeTransaction = await modeDb.Database
            .BeginTransactionAsync()
            .ConfigureAwait(false);

        IIntegrationEventHandler<InventoryAllocationRequestedIntegrationEvent> allocationHandler =
            ResolveInventoryHandler<InventoryAllocationRequestedIntegrationEvent>(allocationScope.ServiceProvider);
        await allocationHandler.HandleAsync(
            new(
                Guid.NewGuid(),
                TenantA,
                DateTimeOffset.UtcNow,
                RoomModeRaceReservation,
                Guid.NewGuid(),
                PropertyB,
                new DateOnly(2026, 12, 1),
                new DateOnly(2026, 12, 3),
                [BedB]),
            CancellationToken.None).ConfigureAwait(false);

        ICommandHandler<ConfigureRoomSalesModeCommand, RoomInventoryMutationReceiptDto> raceConfigure = modeScope.ServiceProvider
            .GetRequiredService<ICommandHandler<ConfigureRoomSalesModeCommand, RoomInventoryMutationReceiptDto>>();
        Result<RoomInventoryMutationReceiptDto> concurrentModeChange = await raceConfigure.HandleAsync(
            new(
                Guid.NewGuid(),
                PropertyB,
                RoomB,
                InventorySalesMode.RoomLevel,
                ExpectedVersion: 2),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(concurrentModeChange.IsSuccess);

        await allocationDb.SaveChangesAsync().ConfigureAwait(false);
        await allocationTransaction.CommitAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            modeDb.SaveChangesAsync());
        await modeTransaction.RollbackAsync().ConfigureAwait(false);

        using IServiceScope verificationScope = api.Services.CreateScope();
        verificationScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
        InventoryDbContext verificationDb = verificationScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        RoomInventoryConfiguration configuration = await verificationDb.RoomConfigurations
            .AsNoTracking()
            .SingleAsync(item => item.PropertyId == PropertyB && item.Id == RoomB)
            .ConfigureAwait(false);
        Assert.Equal(RoomSalesMode.BedLevel, configuration.SalesMode);
        Assert.Equal(
            InventoryAllocationState.Active,
            (await verificationDb.Allocations.AsNoTracking()
                .SingleAsync(item => item.ReservationId == RoomModeRaceReservation)
                .ConfigureAwait(false)).Status);
    }

    private static async Task ExerciseBedRetirementWorkflowAsync(AuthTestApplication api)
    {
        Guid operationId = Guid.NewGuid();
        Result<BedRetirementDto>[] concurrentResults = await Task.WhenAll(
            RequestBedRetirementAsync(api, operationId),
            RequestBedRetirementAsync(api, operationId)).ConfigureAwait(false);
        Assert.All(concurrentResults, result => Assert.True(result.IsSuccess));
        BedRetirementDto result = concurrentResults[0].Value;
        Assert.All(
            concurrentResults,
            concurrent => Assert.Equal(
                result.TopologyChangeId,
                concurrent.Value.TopologyChangeId));
        Assert.Equal(InventoryRetirementStatus.Draining, result.Status);
        Assert.Equal(1, result.ActiveAllocationCount);
        Assert.Equal(
            RoomModeRaceReservation,
            Assert.Single(result.AffectedReservationIds));
        Guid topologyChangeId = result.TopologyChangeId;

        using (IServiceScope verificationScope = api.Services.CreateScope())
        {
            verificationScope.ServiceProvider
                .GetRequiredService<ITenantContextAccessor>()
                .SetTenant(TenantA);
            InventoryDbContext inventoryDb = verificationScope.ServiceProvider
                .GetRequiredService<InventoryDbContext>();
            Assert.Equal(
                1,
                await inventoryDb.BedRetirements
                    .CountAsync(item => item.Id == topologyChangeId)
                    .ConfigureAwait(false));
            IInventoryManagementOperationRepository operations =
                verificationScope.ServiceProvider.GetRequiredService<
                    IInventoryManagementOperationRepository>();
            InventoryManagementOperationRecord? operation = await operations
                .GetAsync(
                    InventoryManagementResourceKind.InventoryUnit,
                    BedB,
                    operationId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            Assert.NotNull(operation);
            Assert.Equal(topologyChangeId, operation.ResultTopologyChangeId);
        }

        using (IServiceScope releaseScope = api.Services.CreateScope())
        {
            releaseScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
            InventoryDbContext inventoryDb = releaseScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            await using var transaction = await inventoryDb.Database
                .BeginTransactionAsync()
                .ConfigureAwait(false);
            InventoryAllocation allocation = await inventoryDb.Allocations
                .SingleAsync(item => item.ReservationId == RoomModeRaceReservation)
                .ConfigureAwait(false);
            IIntegrationEventHandler<InventoryAllocationReleaseRequestedIntegrationEvent> release =
                ResolveInventoryHandler<InventoryAllocationReleaseRequestedIntegrationEvent>(releaseScope.ServiceProvider);
            await release.HandleAsync(
                new(
                    Guid.NewGuid(),
                    TenantA,
                    DateTimeOffset.UtcNow,
                    RoomModeRaceReservation,
                    allocation.Id,
                    Guid.NewGuid(),
                    allocation.Version),
                CancellationToken.None).ConfigureAwait(false);
            await inventoryDb.SaveChangesAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);

            BedRetirementProcess process = await inventoryDb.BedRetirements
                .SingleAsync(item => item.Id == topologyChangeId)
                .ConfigureAwait(false);
            Assert.Equal(InventoryRetirementProcessState.FinalizationRequested, process.State);
        }

        long roomVersion;
        long bedVersion;
        using (IServiceScope propertiesScope = api.Services.CreateScope())
        {
            propertiesScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
            PropertiesDbContext propertiesDb = propertiesScope.ServiceProvider.GetRequiredService<PropertiesDbContext>();
            await using var transaction = await propertiesDb.Database
                .BeginTransactionAsync()
                .ConfigureAwait(false);
            IIntegrationEventHandler<BedRetirementFinalizationRequestedIntegrationEvent> finalize =
                ResolveHandler<BedRetirementFinalizationRequestedIntegrationEvent>(
                    propertiesScope.ServiceProvider,
                    PropertiesModuleMetadata.Name);
            await finalize.HandleAsync(
                new(
                    Guid.NewGuid(),
                    TenantA,
                    DateTimeOffset.UtcNow,
                    topologyChangeId,
                    PropertyB,
                    RoomB,
                    BedB),
                CancellationToken.None).ConfigureAwait(false);

            await propertiesDb.SaveChangesAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
            Room room = await propertiesDb.Rooms
                .AsNoTracking()
                .Include(item => item.Beds)
                .SingleAsync(item => item.Id == RoomB)
                .ConfigureAwait(false);
            Bed bed = room.Beds.Single(item => item.Id == BedB);
            Assert.Equal(BedState.Retired, bed.Status);
            Assert.Equal(BedState.Active, room.Beds.Single(item => item.Id == BedB2).Status);
            roomVersion = room.Version;
            bedVersion = bed.Version;
        }

        using (IServiceScope topologyScope = api.Services.CreateScope())
        using (IServiceScope finalizationScope = api.Services.CreateScope())
        {
            topologyScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
            finalizationScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
            InventoryDbContext topologyDb = topologyScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            InventoryDbContext finalizationDb = finalizationScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            await using var topologyTransaction = await topologyDb.Database
                .BeginTransactionAsync()
                .ConfigureAwait(false);
            await using var finalizationTransaction = await finalizationDb.Database
                .BeginTransactionAsync()
                .ConfigureAwait(false);
            IIntegrationEventHandler<BedRetiredIntegrationEvent> topology =
                ResolveInventoryHandler<BedRetiredIntegrationEvent>(topologyScope.ServiceProvider);
            IIntegrationEventHandler<BedRetirementFinalizedIntegrationEvent> finalized =
                ResolveInventoryHandler<BedRetirementFinalizedIntegrationEvent>(finalizationScope.ServiceProvider);

            await topology.HandleAsync(
                new(
                    Guid.NewGuid(),
                    TenantA,
                    DateTimeOffset.UtcNow,
                    PropertyB,
                    RoomB,
                    BedB,
                    roomVersion,
                    bedVersion),
                CancellationToken.None).ConfigureAwait(false);
            Task finalizationHandling = finalized.HandleAsync(
                new(
                    Guid.NewGuid(),
                    TenantA,
                    DateTimeOffset.UtcNow,
                    topologyChangeId,
                    PropertyB,
                    RoomB,
                    BedB,
                    roomVersion,
                    bedVersion),
                CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            Assert.False(finalizationHandling.IsCompleted);

            await topologyDb.SaveChangesAsync().ConfigureAwait(false);
            await topologyTransaction.CommitAsync().ConfigureAwait(false);
            await finalizationHandling.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await finalizationDb.SaveChangesAsync().ConfigureAwait(false);
            await finalizationTransaction.CommitAsync().ConfigureAwait(false);
        }

        using (IServiceScope verificationScope = api.Services.CreateScope())
        {
            verificationScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
            InventoryDbContext inventoryDb = verificationScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            BedRetirementProcess process = await inventoryDb.BedRetirements
                .AsNoTracking()
                .SingleAsync(item => item.Id == topologyChangeId)
                .ConfigureAwait(false);
            Assert.Equal(InventoryRetirementProcessState.Completed, process.State);
            Assert.NotNull(process.CompletedAtUtc);
            Assert.Equal(
                BedStatus.Retired,
                (await inventoryDb.BedTopology.AsNoTracking().SingleAsync(item => item.Id == BedB)).Status);
            Assert.False(
                (await inventoryDb.InventoryUnits.AsNoTracking().SingleAsync(item => item.Id == BedB)).IsTopologyActive);
        }
    }

    private static async Task<Result<BedRetirementDto>>
        RequestBedRetirementAsync(
        AuthTestApplication api,
        Guid operationId)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantA);
        InventoryDbContext inventoryDb = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        await using var transaction = await inventoryDb.Database
            .BeginTransactionAsync()
            .ConfigureAwait(false);
        ICommandHandler<RequestBedRetirementCommand, BedRetirementDto> handler =
            scope.ServiceProvider.GetRequiredService<
                ICommandHandler<RequestBedRetirementCommand, BedRetirementDto>>();
        Result<BedRetirementDto> result = await handler.HandleAsync(
            new(
                operationId,
                PropertyB,
                RoomB,
                BedB,
                true,
                "Replace damaged bed frame",
                "integration-test"),
            CancellationToken.None).ConfigureAwait(false);
        await inventoryDb.SaveChangesAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        return result;
    }

    private static async Task ExerciseRoomRetirementWorkflowAsync(AuthTestApplication api)
    {
        Guid topologyChangeId;
        using (IServiceScope requestScope = api.Services.CreateScope())
        {
            requestScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
            InventoryDbContext requestDb = requestScope.ServiceProvider
                .GetRequiredService<InventoryDbContext>();
            await using var transaction = await requestDb.Database
                .BeginTransactionAsync()
                .ConfigureAwait(false);
            ICommandHandler<RetireRoomCommand, Unit> directRetirement = requestScope.ServiceProvider
                .GetRequiredService<ICommandHandler<RetireRoomCommand, Unit>>();
            Result<Unit> bypass = await directRetirement.HandleAsync(
                new RetireRoomCommand(PropertyB, RoomB, ExpectedVersion: 4, CascadeBeds: true),
                CancellationToken.None).ConfigureAwait(false);
            Assert.True(bypass.IsFailure);
            Assert.Equal(PropertiesApplicationErrors.RoomRetirementRequiresInventory.Code, bypass.Error.Code);

            ICommandHandler<RequestRoomRetirementCommand, RoomRetirementDto> request = requestScope.ServiceProvider
                .GetRequiredService<ICommandHandler<RequestRoomRetirementCommand, RoomRetirementDto>>();
            Result<RoomRetirementDto> result = await request.HandleAsync(
                new(
                    Guid.NewGuid(),
                    PropertyB,
                    RoomB,
                    true,
                    "Permanently repurpose room",
                    "integration-test"),
                CancellationToken.None).ConfigureAwait(false);

            Assert.True(result.IsSuccess);
            Assert.Equal(InventoryRetirementStatus.FinalizationRequested, result.Value.Status);
            Assert.Equal(0, result.Value.ActiveAllocationCount);
            Assert.Equal(0, result.Value.ActiveManualBlockCount);
            Assert.Equal(0, result.Value.ActiveBedRetirementCount);
            topologyChangeId = result.Value.TopologyChangeId;

            IInventoryAvailabilityRepository availability = requestScope.ServiceProvider
                .GetRequiredService<IInventoryAvailabilityRepository>();
            InventoryAvailabilityContextSnapshot context = await availability.GetContextAsync(
                PropertyB,
                [RoomB, BedB2],
                CancellationToken.None).ConfigureAwait(false);
            Assert.All(context.Units, unit => Assert.False(unit.IsSellable));
            await requestDb.SaveChangesAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }

        using (IServiceScope nestedScope = api.Services.CreateScope())
        {
            nestedScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
            InventoryDbContext nestedDb = nestedScope.ServiceProvider
                .GetRequiredService<InventoryDbContext>();
            await using var transaction = await nestedDb.Database
                .BeginTransactionAsync()
                .ConfigureAwait(false);
            ICommandHandler<RequestBedRetirementCommand, BedRetirementDto> requestBed = nestedScope.ServiceProvider
                .GetRequiredService<ICommandHandler<RequestBedRetirementCommand, BedRetirementDto>>();
            Result<BedRetirementDto> nested = await requestBed.HandleAsync(
                new(
                    Guid.NewGuid(),
                    PropertyB,
                    RoomB,
                    BedB2,
                    true,
                    "Should be covered by room retirement",
                    "integration-test"),
                CancellationToken.None).ConfigureAwait(false);
            Assert.True(nested.IsFailure);
            Assert.Equal(InventoryApplicationErrors.RoomRetirementInProgress.Code, nested.Error.Code);
        }

        long roomVersion;
        long bedVersion;
        using (IServiceScope propertiesScope = api.Services.CreateScope())
        {
            propertiesScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
            PropertiesDbContext propertiesDb = propertiesScope.ServiceProvider.GetRequiredService<PropertiesDbContext>();
            IIntegrationEventHandler<RoomRetirementFinalizationRequestedIntegrationEvent> finalize =
                ResolveHandler<RoomRetirementFinalizationRequestedIntegrationEvent>(
                    propertiesScope.ServiceProvider,
                    PropertiesModuleMetadata.Name);
            RoomRetirementFinalizationRequestedIntegrationEvent finalization = new(
                Guid.NewGuid(),
                TenantA,
                DateTimeOffset.UtcNow,
                topologyChangeId,
                PropertyB,
                RoomB);
            IInboxStore inbox = propertiesScope.ServiceProvider
                .GetServices<IInboxStore>()
                .Single(store => store.ModuleName == PropertiesModuleMetadata.Name);
            InboxProcessResult inboxResult = await inbox.ProcessAsync(
                new InboxMessageRecord(
                    finalization.EventId,
                    PropertiesModuleMetadata.RoomRetirementFinalizationHandlerName,
                    IntegrationEventNaming.CreateSubject(
                        IntegrationEventNaming.DefaultSubjectPrefix,
                        InventoryModuleMetadata.Name,
                        finalization.EventName,
                        finalization.Version),
                    finalization.EventName,
                    finalization.Version,
                    finalization.ScopeId,
                    finalization.OccurredAtUtc),
                cancellationToken => finalize.HandleAsync(finalization, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);

            Assert.Equal(InboxProcessStatus.Processed, inboxResult.Status);
            Room room = await propertiesDb.Rooms
                .AsNoTracking()
                .Include(item => item.Beds)
                .SingleAsync(item => item.Id == RoomB)
                .ConfigureAwait(false);
            Assert.Equal(RoomState.Retired, room.Status);
            Assert.All(room.Beds, bed => Assert.Equal(BedState.Retired, bed.Status));
            roomVersion = room.Version;
            bedVersion = room.Beds.Single(bed => bed.Id == BedB2).Version;
            Assert.Single(propertiesDb.OutboxMessages.Where(message =>
                message.EventType == typeof(RoomRetiredIntegrationEvent).FullName));
            Assert.Single(propertiesDb.OutboxMessages.Where(message =>
                message.EventType == typeof(RoomRetirementFinalizedIntegrationEvent).FullName));
        }

        using (IServiceScope finalizationScope = api.Services.CreateScope())
        using (IServiceScope topologyScope = api.Services.CreateScope())
        {
            finalizationScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
            topologyScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
            InventoryDbContext finalizationDb = finalizationScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            InventoryDbContext topologyDb = topologyScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            await using var finalizationTransaction = await finalizationDb.Database
                .BeginTransactionAsync()
                .ConfigureAwait(false);
            await using var topologyTransaction = await topologyDb.Database
                .BeginTransactionAsync()
                .ConfigureAwait(false);
            IIntegrationEventHandler<RoomRetirementFinalizedIntegrationEvent> finalized =
                ResolveInventoryHandler<RoomRetirementFinalizedIntegrationEvent>(finalizationScope.ServiceProvider);
            IIntegrationEventHandler<RoomRetiredIntegrationEvent> topology =
                ResolveInventoryHandler<RoomRetiredIntegrationEvent>(topologyScope.ServiceProvider);

            await finalized.HandleAsync(
                new(
                    Guid.NewGuid(),
                    TenantA,
                    DateTimeOffset.UtcNow,
                    topologyChangeId,
                    PropertyB,
                    RoomB,
                    roomVersion),
                CancellationToken.None).ConfigureAwait(false);
            Task topologyHandling = topology.HandleAsync(
                new(
                    Guid.NewGuid(),
                    TenantA,
                    DateTimeOffset.UtcNow,
                    PropertyB,
                    RoomB,
                    roomVersion),
                CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
            Assert.False(topologyHandling.IsCompleted);

            await finalizationDb.SaveChangesAsync().ConfigureAwait(false);
            await finalizationTransaction.CommitAsync().ConfigureAwait(false);
            await topologyHandling.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            await topologyDb.SaveChangesAsync().ConfigureAwait(false);
            await topologyTransaction.CommitAsync().ConfigureAwait(false);
        }

        using (IServiceScope verificationScope = api.Services.CreateScope())
        {
            verificationScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
            InventoryDbContext inventoryDb = verificationScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            RoomRetirementProcess process = await inventoryDb.RoomRetirements
                .AsNoTracking()
                .SingleAsync(item => item.Id == topologyChangeId)
                .ConfigureAwait(false);
            Assert.Equal(InventoryRetirementProcessState.Completed, process.State);
            Assert.NotNull(process.CompletedAtUtc);
            Assert.Equal(
                RoomStatus.Retired,
                (await inventoryDb.RoomTopology.AsNoTracking().SingleAsync(item => item.Id == RoomB)).Status);

            IInventoryAvailabilityRepository availability = verificationScope.ServiceProvider
                .GetRequiredService<IInventoryAvailabilityRepository>();
            InventoryAvailabilityContextSnapshot context = await availability.GetContextAsync(
                PropertyB,
                [RoomB, BedB2],
                CancellationToken.None).ConfigureAwait(false);
            Assert.All(context.Units, unit =>
            {
                Assert.False(unit.IsTopologyActive);
                Assert.False(unit.IsSellable);
            });
        }

        using (IServiceScope bedOutcomeScope = api.Services.CreateScope())
        {
            bedOutcomeScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
            InventoryDbContext inventoryDb = bedOutcomeScope.ServiceProvider
                .GetRequiredService<InventoryDbContext>();
            IIntegrationEventHandler<BedRetiredIntegrationEvent> bedTopology =
                ResolveInventoryHandler<BedRetiredIntegrationEvent>(bedOutcomeScope.ServiceProvider);
            await ModuleTransactionIntegrationTestData.ExecuteAsync(
                inventoryDb,
                token => bedTopology.HandleAsync(
                    new(
                        Guid.NewGuid(),
                        TenantA,
                        DateTimeOffset.UtcNow,
                        PropertyB,
                        RoomB,
                        BedB2,
                        roomVersion,
                        bedVersion),
                    token)).ConfigureAwait(false);
            Assert.All(
                await inventoryDb.InventoryUnits.AsNoTracking().Where(item => item.RoomId == RoomB).ToArrayAsync(),
                unit => Assert.False(unit.IsTopologyActive));
        }
    }

    private static async Task ExerciseAllocationAuthorityAsync(AuthTestApplication api)
    {
        Guid reservationOne = Guid.Parse("50000000-0000-0000-0000-000000000001");
        Guid requestOne = Guid.Parse("51000000-0000-0000-0000-000000000001");
        Guid reservationTwo = Guid.Parse("50000000-0000-0000-0000-000000000002");
        Guid requestTwo = Guid.Parse("51000000-0000-0000-0000-000000000002");
        Guid reservationThree = Guid.Parse("50000000-0000-0000-0000-000000000003");
        Guid requestThree = Guid.Parse("51000000-0000-0000-0000-000000000003");

        await HandleAllocationRequestAsync(
            api,
            reservationOne,
            requestOne,
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 3)).ConfigureAwait(false);
        await HandleAllocationRequestAsync(
            api,
            reservationTwo,
            requestTwo,
            new DateOnly(2026, 9, 2),
            new DateOnly(2026, 9, 4)).ConfigureAwait(false);
        await HandleAllocationRequestAsync(
            api,
            reservationThree,
            requestThree,
            new DateOnly(2026, 9, 3),
            new DateOnly(2026, 9, 4)).ConfigureAwait(false);

        Guid allocationOne;
        using (IServiceScope verificationScope = api.Services.CreateScope())
        {
            verificationScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
            InventoryDbContext dbContext = verificationScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            InventoryAllocation first = await dbContext.Allocations
                .AsNoTracking()
                .SingleAsync(allocation => allocation.AllocationRequestId == requestOne)
                .ConfigureAwait(false);
            InventoryAllocation overlapping = await dbContext.Allocations
                .AsNoTracking()
                .SingleAsync(allocation => allocation.AllocationRequestId == requestTwo)
                .ConfigureAwait(false);
            InventoryAllocation adjacent = await dbContext.Allocations
                .AsNoTracking()
                .SingleAsync(allocation => allocation.AllocationRequestId == requestThree)
                .ConfigureAwait(false);
            Assert.Equal(InventoryAllocationState.Active, first.Status);
            Assert.Equal(InventoryAllocationState.Rejected, overlapping.Status);
            Assert.Equal(InventoryAllocationRejection.AllocationConflict, overlapping.Rejection);
            Assert.Equal(InventoryAllocationState.Active, adjacent.Status);
            allocationOne = first.Id;
        }

        using (IServiceScope releaseScope = api.Services.CreateScope())
        {
            releaseScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
            InventoryDbContext inventoryDb = releaseScope.ServiceProvider
                .GetRequiredService<InventoryDbContext>();
            await using var transaction = await inventoryDb.Database
                .BeginTransactionAsync()
                .ConfigureAwait(false);
            IIntegrationEventHandler<InventoryAllocationReleaseRequestedIntegrationEvent> releaseHandler =
                ResolveInventoryHandler<InventoryAllocationReleaseRequestedIntegrationEvent>(releaseScope.ServiceProvider);
            await releaseHandler.HandleAsync(
                new(
                    Guid.NewGuid(),
                    TenantA,
                    DateTimeOffset.UtcNow,
                    reservationOne,
                    allocationOne,
                    Guid.NewGuid(),
                    expectedAllocationVersion: 1),
                CancellationToken.None).ConfigureAwait(false);
            await inventoryDb.SaveChangesAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }

        Guid raceReservationOne = Guid.Parse("50000000-0000-0000-0000-000000000011");
        Guid raceRequestOne = Guid.Parse("51000000-0000-0000-0000-000000000011");
        Guid raceReservationTwo = Guid.Parse("50000000-0000-0000-0000-000000000012");
        Guid raceRequestTwo = Guid.Parse("51000000-0000-0000-0000-000000000012");
        using IServiceScope raceScopeOne = api.Services.CreateScope();
        using IServiceScope raceScopeTwo = api.Services.CreateScope();
        raceScopeOne.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
        raceScopeTwo.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
        InventoryDbContext raceDbOne = raceScopeOne.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        InventoryDbContext raceDbTwo = raceScopeTwo.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        await using var raceTransactionOne = await raceDbOne.Database
            .BeginTransactionAsync()
            .ConfigureAwait(false);
        await using var raceTransactionTwo = await raceDbTwo.Database
            .BeginTransactionAsync()
            .ConfigureAwait(false);
        IIntegrationEventHandler<InventoryAllocationRequestedIntegrationEvent> raceHandlerOne =
            ResolveInventoryHandler<InventoryAllocationRequestedIntegrationEvent>(raceScopeOne.ServiceProvider);
        IIntegrationEventHandler<InventoryAllocationRequestedIntegrationEvent> raceHandlerTwo =
            ResolveInventoryHandler<InventoryAllocationRequestedIntegrationEvent>(raceScopeTwo.ServiceProvider);
        InventoryAllocationRequestedIntegrationEvent raceOne = CreateAllocationRequest(
            raceReservationOne,
            raceRequestOne,
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 3));
        InventoryAllocationRequestedIntegrationEvent raceTwo = CreateAllocationRequest(
            raceReservationTwo,
            raceRequestTwo,
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 3));
        await raceHandlerOne.HandleAsync(raceOne, CancellationToken.None).ConfigureAwait(false);
        await raceHandlerTwo.HandleAsync(raceTwo, CancellationToken.None).ConfigureAwait(false);
        await raceDbOne.SaveChangesAsync().ConfigureAwait(false);
        await raceTransactionOne.CommitAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            raceDbTwo.SaveChangesAsync());
        await raceTransactionTwo.RollbackAsync().ConfigureAwait(false);

        await HandleAllocationRequestAsync(
            api,
            raceReservationTwo,
            raceRequestTwo,
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 3)).ConfigureAwait(false);
        using IServiceScope raceVerificationScope = api.Services.CreateScope();
        raceVerificationScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
        InventoryDbContext raceDbContext = raceVerificationScope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Assert.Equal(
            InventoryAllocationState.Active,
            (await raceDbContext.Allocations.AsNoTracking()
                .SingleAsync(allocation => allocation.AllocationRequestId == raceRequestOne)
                .ConfigureAwait(false)).Status);
        Assert.Equal(
            InventoryAllocationState.Rejected,
            (await raceDbContext.Allocations.AsNoTracking()
                .SingleAsync(allocation => allocation.AllocationRequestId == raceRequestTwo)
                .ConfigureAwait(false)).Status);
    }

    private static async Task ExerciseAllocationMutationSerializationAsync(
        AuthTestApplication api)
    {
        Guid reservationId = Guid.NewGuid();
        Guid firstRequestId = Guid.NewGuid();
        Guid secondRequestId = Guid.NewGuid();
        using IServiceScope firstScope = api.Services.CreateScope();
        using IServiceScope secondScope = api.Services.CreateScope();
        firstScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantA);
        secondScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantA);
        InventoryDbContext firstDb = firstScope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        InventoryDbContext secondDb = secondScope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        await using var firstTransaction = await firstDb.Database
            .BeginTransactionAsync()
            .ConfigureAwait(false);
        await using var secondTransaction = await secondDb.Database
            .BeginTransactionAsync()
            .ConfigureAwait(false);
        IIntegrationEventHandler<InventoryAllocationRequestedIntegrationEvent>
            firstHandler = ResolveInventoryHandler<
                InventoryAllocationRequestedIntegrationEvent>(
                    firstScope.ServiceProvider);
        IIntegrationEventHandler<InventoryAllocationRequestedIntegrationEvent>
            secondHandler = ResolveInventoryHandler<
                InventoryAllocationRequestedIntegrationEvent>(
                    secondScope.ServiceProvider);
        InventoryAllocationRequestedIntegrationEvent firstRequest =
            CreateAllocationRequest(
                reservationId,
                firstRequestId,
                new DateOnly(2027, 1, 1),
                new DateOnly(2027, 1, 3));
        InventoryAllocationRequestedIntegrationEvent secondRequest =
            CreateAllocationRequest(
                reservationId,
                secondRequestId,
                new DateOnly(2027, 2, 1),
                new DateOnly(2027, 2, 3));

        await firstHandler.HandleAsync(
            firstRequest,
            CancellationToken.None).ConfigureAwait(false);
        Task secondHandling = secondHandler.HandleAsync(
            secondRequest,
            CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(250))
            .ConfigureAwait(false);
        Assert.False(secondHandling.IsCompleted);

        await firstDb.SaveChangesAsync().ConfigureAwait(false);
        await firstTransaction.CommitAsync().ConfigureAwait(false);
        await secondHandling.WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        await secondDb.SaveChangesAsync().ConfigureAwait(false);
        await secondTransaction.CommitAsync().ConfigureAwait(false);

        Guid allocationId;
        using (IServiceScope verificationScope = api.Services.CreateScope())
        {
            verificationScope.ServiceProvider
                .GetRequiredService<ITenantContextAccessor>()
                .SetTenant(TenantA);
            InventoryDbContext verificationDb = verificationScope
                .ServiceProvider.GetRequiredService<InventoryDbContext>();
            InventoryAllocation allocation = Assert.Single(
                await verificationDb.Allocations
                    .AsNoTracking()
                    .Where(item => item.ReservationId == reservationId)
                    .ToArrayAsync()
                    .ConfigureAwait(false));
            Assert.Equal(firstRequestId, allocation.AllocationRequestId);
            Assert.Empty(
                await verificationDb.Allocations
                    .AsNoTracking()
                    .Where(item =>
                        item.AllocationRequestId == secondRequestId)
                    .ToArrayAsync()
                    .ConfigureAwait(false));
            allocationId = allocation.Id;
        }

        using IServiceScope firstLockScope = api.Services.CreateScope();
        using IServiceScope secondLockScope = api.Services.CreateScope();
        firstLockScope.ServiceProvider
            .GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantA);
        secondLockScope.ServiceProvider
            .GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantA);
        InventoryDbContext firstLockDb = firstLockScope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        InventoryDbContext secondLockDb = secondLockScope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        await using var firstLockTransaction = await firstLockDb.Database
            .BeginTransactionAsync()
            .ConfigureAwait(false);
        await using var secondLockTransaction = await secondLockDb.Database
            .BeginTransactionAsync()
            .ConfigureAwait(false);
        IInventoryAllocationOperationLock firstLock = firstLockScope
            .ServiceProvider.GetRequiredService<
                IInventoryAllocationOperationLock>();
        IInventoryAllocationOperationLock secondLock = secondLockScope
            .ServiceProvider.GetRequiredService<
                IInventoryAllocationOperationLock>();

        Assert.True(await firstLock.TryAcquireExistingAsync(
            TenantA,
            allocationId,
            CancellationToken.None));
        Task<bool> secondAcquisition = secondLock.TryAcquireExistingAsync(
            TenantA,
            allocationId,
            CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(250))
            .ConfigureAwait(false);
        Assert.False(secondAcquisition.IsCompleted);

        await firstLockTransaction.CommitAsync().ConfigureAwait(false);
        Assert.True(await secondAcquisition
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false));
        await secondLockTransaction.CommitAsync().ConfigureAwait(false);

        using IServiceScope revisionScope = api.Services.CreateScope();
        revisionScope.ServiceProvider
            .GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantA);
        InventoryDbContext revisionDb = revisionScope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        long revision = await revisionDb.Database.SqlQuery<long>(
                $"""
                SELECT "Revision" AS "Value"
                FROM inventory.allocation_operation_locks
                WHERE "AllocationId" = {allocationId}
                """)
            .SingleAsync()
            .ConfigureAwait(false);
        Assert.Equal(3, revision);
    }

    private static async Task HandleAllocationRequestAsync(
        AuthTestApplication api,
        Guid reservationId,
        Guid requestId,
        DateOnly arrival,
        DateOnly departure)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantA);
        InventoryDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync()
            .ConfigureAwait(false);
        IIntegrationEventHandler<InventoryAllocationRequestedIntegrationEvent> handler =
            ResolveInventoryHandler<InventoryAllocationRequestedIntegrationEvent>(scope.ServiceProvider);
        await handler.HandleAsync(
            CreateAllocationRequest(reservationId, requestId, arrival, departure),
            CancellationToken.None).ConfigureAwait(false);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static InventoryAllocationRequestedIntegrationEvent CreateAllocationRequest(
        Guid reservationId,
        Guid requestId,
        DateOnly arrival,
        DateOnly departure) =>
        new(
            Guid.NewGuid(),
            TenantA,
            DateTimeOffset.UtcNow,
            reservationId,
            requestId,
            PropertyA,
            arrival,
            departure,
            [RoomA]);

    private static IIntegrationEventHandler<TEvent> ResolveInventoryHandler<TEvent>(IServiceProvider services)
        where TEvent : IIntegrationEvent
        => ResolveHandler<TEvent>(services, InventoryModuleMetadata.Name);

    private static IIntegrationEventHandler<TEvent> ResolveHandler<TEvent>(
        IServiceProvider services,
        string consumerModule)
        where TEvent : IIntegrationEvent
    {
        IntegrationEventSubscription subscription = services
            .GetRequiredService<IIntegrationEventSubscriptionRegistry>()
            .Subscriptions
            .Single(item =>
                item.ConsumerModule == consumerModule &&
                item.EventType == typeof(TEvent));
        return (IIntegrationEventHandler<TEvent>)services.GetRequiredService(subscription.HandlerType);
    }

    private static async Task<InventoryAvailabilityResponse> GetAvailabilityAsync(
        HttpClient client,
        string accessToken,
        string arrival,
        string departure)
    {
        using HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Get,
            $"/api/inventory/properties/{PropertyA:D}/availability?arrival={arrival}&departure={departure}",
            accessToken).ConfigureAwait(false);
        return await ReadSuccessAsync<InventoryAvailabilityResponse>(response).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? accessToken = null,
        object? body = null)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Add(TenantHeader, TenantA);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task<T> ReadSuccessAsync<T>(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected success but received {(int)response.StatusCode}. Body: {body}");
        T? value = await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false);
        Assert.NotNull(value);
        return value;
    }

    private static async Task AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(
            response.StatusCode == expected,
            $"Expected {(int)expected} but received {(int)response.StatusCode}. Body: {body}");
    }

    private static async Task AssertProblemAsync(
        HttpStatusCode expected,
        string expectedCode,
        HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(
            response.StatusCode == expected,
            $"Expected {(int)expected} but received {(int)response.StatusCode}. Body: {body}");
        Microsoft.AspNetCore.Mvc.ProblemDetails? problem =
            JsonSerializer.Deserialize<Microsoft.AspNetCore.Mvc.ProblemDetails>(
                body,
                JsonOptions);
        Assert.NotNull(problem);
        Assert.Equal(expectedCode, problem.Title);
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains(
            response.Headers.Pragma,
            directive => string.Equals(
                directive.Name,
                "no-cache",
                StringComparison.OrdinalIgnoreCase));
        Assert.True(response.Content.Headers.TryGetValues("Expires", out IEnumerable<string>? expires));
        Assert.Equal("0", Assert.Single(expires));
    }

    private static async Task<AdminCliResult> AssertAdminSuccessAsync(Task<AdminCliResult> resultTask)
    {
        AdminCliResult result = await resultTask.ConfigureAwait(false);
        Assert.True(
            result.ExitCode == AdminExitCodes.Success,
            $"ExitCode={result.ExitCode}{Environment.NewLine}Output:{Environment.NewLine}{result.Output}{Environment.NewLine}Error:{Environment.NewLine}{result.Error}");
        return result;
    }

    private sealed class InventorySelectCounter : DbCommandInterceptor
    {
        private bool recording;

        public int SelectCount { get; private set; }

        public void Start()
        {
            this.SelectCount = 0;
            this.recording = true;
        }

        public void Stop() => this.recording = false;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (this.recording &&
                eventData.Context is InventoryDbContext &&
                command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                this.SelectCount++;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static Guid GetSubjectId(string accessToken)
    {
        JwtSecurityToken token = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        string? subjectId = token.Claims.FirstOrDefault(claim =>
            string.Equals(claim.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal) ||
            string.Equals(claim.Type, "nameid", StringComparison.Ordinal) ||
            string.Equals(claim.Type, "sub", StringComparison.Ordinal))?.Value;
        Assert.True(Guid.TryParse(subjectId, out Guid parsedSubjectId));
        return parsedSubjectId;
    }
}
