namespace Integration.Tests;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using BunkFy.Modules.DataRights.Persistence;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Models;
using BunkFy.Modules.Staff.Persistence;
using DotNet.Testcontainers.Containers;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Scoping;
using Gma.Framework.Tenancy;
using Gma.Modules.Auth.Contracts;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class StaffProcessingRestrictionIntegrationTests
{
    private const string TenantId = "a8000000-0000-0000-0000-000000000001";
    private const string PreviousStaffMigration =
        "20260729101543_AddStaffDataRightsCorrectionReceipts";
    private static readonly Guid PropertyId =
        Guid.Parse("78000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset SeededAtUtc =
        new(2026, 7, 29, 13, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Restriction_backfills_legacy_staff_and_enforces_the_full_lifecycle()
    {
        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await nats.StartAsync();
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_staff_processing_restriction_tests")
                .Build();
        await postgreSql.StartAsync();

        string connectionString = postgreSql.GetConnectionString();
        StaffMember legacyMember = await SeedLegacyStaffAsync(connectionString)
            .ConfigureAwait(false);
        await AssertLegacyProjectionBackfilledAsync(
            connectionString,
            legacyMember.Id).ConfigureAwait(false);

        await using AuthTestApplication api = new(
            "PostgreSql",
            connectionString,
            AuthTestContainers.GetNatsConnectionString(nats),
            disableOutboxPublisher: false);
        await api.MigrateStaffAuthorizationDatabaseAsync().ConfigureAwait(false);
        await api.MigrateGuestDataRightsAuthorizationDatabaseAsync()
            .ConfigureAwait(false);

        await using AdminCliTestApplication admin =
            new("PostgreSql", connectionString);
        await admin.MigrateAsync().ConfigureAwait(false);
        using HttpClient client = api.CreateClient();

        AuthTokensResponse tokens = await AuthApiClient.RegisterAsync(
            client,
            TenantId,
            "privacy-restriction-operator@staff.test").ConfigureAwait(false);
        Guid operatorId = GetSubjectId(tokens.AccessToken);
        await api.SeedOrganizationMembershipAsync(TenantId, operatorId)
            .ConfigureAwait(false);
        await GrantRestrictionAccessAsync(admin, operatorId)
            .ConfigureAwait(false);

        await AssertAudienceAsync(
            api,
            legacyMember.Id,
            expectedPropertyAudience: [legacyMember.AuthSubjectId!],
            expectedDirectRecipient: legacyMember.AuthSubjectId)
            .ConfigureAwait(false);

        DataRightsCase applyCase = await SeedApprovedCaseAsync(
            api,
            legacyMember.Id,
            legacyMember.Version,
            DataRightsRestrictionAction.Apply,
            SeededAtUtc.AddMinutes(10)).ConfigureAwait(false);
        Guid applyIdempotencyKey = Guid.NewGuid();
        DataRightsRestrictionExecutionDto applied =
            await ExecuteRestrictionAsync(
                client,
                tokens.AccessToken,
                applyCase,
                applyIdempotencyKey).ConfigureAwait(false);

        Assert.Equal(DataRightsCaseStatus.Completed, applied.Case.Status);
        Assert.Equal(DataRightsRestrictionDirective.Apply, applied.Proof.Directive);
        Assert.True(applied.Proof.EffectiveRestricted);
        Assert.Equal(1, applied.Proof.ResultingOwnerRevision);
        Assert.Equal(1, applied.Proof.ResultingProjectionRevision);

        DataRightsRestrictionExecutionDto replay = await ExecuteRestrictionAsync(
            client,
            tokens.AccessToken,
            applyCase,
            applyIdempotencyKey).ConfigureAwait(false);
        Assert.Equal(applied.Proof, replay.Proof);

        using (HttpResponseMessage hidden = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/staff/members/{legacyMember.Id:D}",
                   tokens.AccessToken).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.NotFound, hidden)
                .ConfigureAwait(false);
        }

        using (HttpResponseMessage hiddenList = await SendAsync(
                   client,
                   HttpMethod.Get,
                   "/api/staff/members",
                   tokens.AccessToken).ConfigureAwait(false))
        {
            StaffDirectoryListResponse directory =
                await ReadSuccessAsync<StaffDirectoryListResponse>(hiddenList)
                    .ConfigureAwait(false);
            Assert.Empty(directory.Items);
        }

        await AssertRestrictedOwnerReadsAsync(api, legacyMember.Id)
            .ConfigureAwait(false);
        await AssertAudienceAsync(
            api,
            legacyMember.Id,
            expectedPropertyAudience: [],
            expectedDirectRecipient: null).ConfigureAwait(false);

        StaffDirectoryMemberDto suspended;
        using (HttpResponseMessage response = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/staff/members/{legacyMember.Id:D}/suspend",
                   tokens.AccessToken,
                   new
                   {
                       operationId = Guid.NewGuid(),
                       reason = "Privacy-safe access reduction",
                       expectedVersion = legacyMember.Version
                   }).ConfigureAwait(false))
        {
            suspended = await ReadSuccessAsync<StaffDirectoryMemberDto>(response)
                .ConfigureAwait(false);
            Assert.Equal(StaffStatus.Suspended, suspended.Status);
        }

        using (HttpResponseMessage blockedResume = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/staff/members/{legacyMember.Id:D}/resume",
                   tokens.AccessToken,
                   new
                   {
                       operationId = Guid.NewGuid(),
                       reason = "Must remain blocked while restricted",
                       expectedVersion = suspended.Version
                   }).ConfigureAwait(false))
        {
            await AssertStatusAsync(HttpStatusCode.NotFound, blockedResume)
                .ConfigureAwait(false);
        }

        DataRightsCase releaseCase = await SeedApprovedCaseAsync(
            api,
            legacyMember.Id,
            suspended.Version,
            DataRightsRestrictionAction.Release,
            SeededAtUtc.AddMinutes(20)).ConfigureAwait(false);
        DataRightsRestrictionExecutionDto released =
            await ExecuteRestrictionAsync(
                client,
                tokens.AccessToken,
                releaseCase,
                Guid.NewGuid()).ConfigureAwait(false);

        Assert.Equal(DataRightsCaseStatus.Completed, released.Case.Status);
        Assert.Equal(DataRightsRestrictionDirective.Release, released.Proof.Directive);
        Assert.False(released.Proof.EffectiveRestricted);
        Assert.Equal(2, released.Proof.ResultingOwnerRevision);
        Assert.Equal(2, released.Proof.ResultingProjectionRevision);

        using (HttpResponseMessage visible = await SendAsync(
                   client,
                   HttpMethod.Get,
                   $"/api/staff/members/{legacyMember.Id:D}",
                   tokens.AccessToken).ConfigureAwait(false))
        {
            StaffDirectoryMemberDto restored =
                await ReadSuccessAsync<StaffDirectoryMemberDto>(visible)
                    .ConfigureAwait(false);
            Assert.Equal(StaffStatus.Suspended, restored.Status);
            Assert.Equal(suspended.Version, restored.Version);
        }

        StaffDirectoryMemberDto resumed;
        using (HttpResponseMessage response = await SendAsync(
                   client,
                   HttpMethod.Post,
                   $"/api/staff/members/{legacyMember.Id:D}/resume",
                   tokens.AccessToken,
                   new
                   {
                       operationId = Guid.NewGuid(),
                       reason = "Restriction released",
                       expectedVersion = suspended.Version
                   }).ConfigureAwait(false))
        {
            resumed = await ReadSuccessAsync<StaffDirectoryMemberDto>(response)
                .ConfigureAwait(false);
            Assert.Equal(StaffStatus.Active, resumed.Status);
        }

        using (HttpResponseMessage visibleList = await SendAsync(
                   client,
                   HttpMethod.Get,
                   "/api/staff/members",
                   tokens.AccessToken).ConfigureAwait(false))
        {
            StaffDirectoryListResponse directory =
                await ReadSuccessAsync<StaffDirectoryListResponse>(visibleList)
                    .ConfigureAwait(false);
            Assert.Equal(legacyMember.Id, Assert.Single(directory.Items).StaffMemberId);
        }

        await AssertAudienceAsync(
            api,
            legacyMember.Id,
            expectedPropertyAudience: [legacyMember.AuthSubjectId!],
            expectedDirectRecipient: legacyMember.AuthSubjectId)
            .ConfigureAwait(false);
        await AssertDurableStateAndPublishedEventsAsync(
            api,
            legacyMember,
            applyCase,
            releaseCase,
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    private static async Task<StaffMember> SeedLegacyStaffAsync(
        string connectionString)
    {
        await using StaffDbContext staff = CreateStaffDbContext(connectionString);
        await staff.Database.GetService<IMigrator>()
            .MigrateAsync(PreviousStaffMigration).ConfigureAwait(false);

        StaffMember member = StaffMember.Create(
            Guid.NewGuid(),
            TenantId,
            "Legacy Restricted Staff",
            "Legacy Legal Name",
            "legacy.restricted.staff@example.test",
            "+44 20 7000 0000",
            "EMP-PRIVACY-1",
            "Duty Manager",
            "Operations",
            "account-legacy-restricted-staff",
            "user:migration-seed",
            Guid.NewGuid(),
            SeededAtUtc).Value;
        Assert.True(member.AssignProperty(
            Guid.NewGuid(),
            PropertyId,
            "Duty Manager",
            isPrimary: true,
            new DateOnly(2026, 7, 1),
            member.Version,
            "user:migration-seed",
            Guid.NewGuid(),
            SeededAtUtc.AddMinutes(1)).IsSuccess);
        await LegacyStaffPersistenceTestData.InsertMemberAsync(staff, member)
            .ConfigureAwait(false);
        return member;
    }

    private static async Task AssertLegacyProjectionBackfilledAsync(
        string connectionString,
        Guid staffMemberId)
    {
        await using StaffDbContext staff = CreateStaffDbContext(connectionString);
        await staff.Database.MigrateAsync().ConfigureAwait(false);
        StaffProcessingRestrictionProjection projection =
            await staff.ProcessingRestrictionProjections
                .AsNoTracking()
                .SingleAsync(candidate => candidate.StaffMemberId == staffMemberId)
                .ConfigureAwait(false);
        Assert.Equal(
            StaffProcessingRestrictionContract.CurrentVersion,
            projection.ContractVersion);
        Assert.Equal(0, projection.Revision);
        Assert.Equal(0, projection.ActiveRestrictionCount);
        Assert.False(projection.IsRestricted);
        Assert.True(projection.ProjectionOrdinal > 0);
    }

    private static async Task<DataRightsCase> SeedApprovedCaseAsync(
        AuthTestApplication api,
        Guid staffMemberId,
        long selectedStaffVersion,
        DataRightsRestrictionAction action,
        DateTimeOffset startedAtUtc)
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.StaffRights,
            DataRightsCaseOperation.Restriction,
            DataRightsRequesterRelation.ControllerInitiated,
            action).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            TenantId,
            request,
            "user:privacy-reviewer",
            startedAtUtc).Value;
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:privacy-reviewer",
            startedAtUtc.AddMinutes(1)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            StaffDataRightsCoordinates.Owner,
            StaffDataRightsCoordinates.StaffMemberRecordType,
            staffMemberId,
            selectedStaffVersion,
            dataRightsCase.Version,
            "user:privacy-reviewer",
            startedAtUtc.AddMinutes(2)).IsSuccess);
        if (action == DataRightsRestrictionAction.Release)
        {
            using IServiceScope targetScope = api.Services.CreateScope();
            targetScope.ServiceProvider
                .GetRequiredService<ITenantContextAccessor>()
                .SetTenant(TenantId);
            StaffProcessingRestriction target = await targetScope
                .ServiceProvider
                .GetRequiredService<StaffDbContext>()
                .ProcessingRestrictions
                .AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.StaffMemberId == staffMemberId &&
                    candidate.Status ==
                        StaffProcessingRestrictionState.Active)
                .ConfigureAwait(false);
            Assert.True(dataRightsCase.SelectRestrictionReleaseTarget(
                StaffDataRightsCoordinates.Owner,
                target.Id,
                target.Version,
                dataRightsCase.Version,
                "user:privacy-reviewer",
                startedAtUtc.AddMinutes(2).AddSeconds(30)).IsSuccess);
        }
        Assert.True(dataRightsCase.RequireReview(
            dataRightsCase.Version,
            "user:privacy-reviewer",
            startedAtUtc.AddMinutes(3)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            dataRightsCase.Version,
            "user:decision-maker",
            startedAtUtc.AddMinutes(4)).IsSuccess);
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            dataRightsCase.Version,
            "user:decision-maker",
            startedAtUtc.AddMinutes(5)).IsSuccess);

        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        DataRightsDbContext dataRights =
            scope.ServiceProvider.GetRequiredService<DataRightsDbContext>();
        dataRights.Cases.Add(dataRightsCase);
        await dataRights.SaveChangesAsync().ConfigureAwait(false);
        return dataRightsCase;
    }

    private static async Task<DataRightsRestrictionExecutionDto>
        ExecuteRestrictionAsync(
            HttpClient client,
            string accessToken,
            DataRightsCase dataRightsCase,
            Guid idempotencyKey)
    {
        using HttpResponseMessage response = await AuthApiClient.PostJsonAsync(
            client,
            TenantId,
            $"/api/data-rights/tenant/cases/{dataRightsCase.Id:D}/restriction",
            new
            {
                idempotencyKey,
                expectedVersion = dataRightsCase.Version
            },
            accessToken).ConfigureAwait(false);
        return await ReadSuccessAsync<DataRightsRestrictionExecutionDto>(response)
            .ConfigureAwait(false);
    }

    private static async Task AssertRestrictedOwnerReadsAsync(
        AuthTestApplication api,
        Guid staffMemberId)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        IStaffMemberRepository members =
            scope.ServiceProvider.GetRequiredService<IStaffMemberRepository>();

        Assert.Null(await members.GetAsync(
            staffMemberId,
            CancellationToken.None).ConfigureAwait(false));
        Assert.NotNull(await members.GetForDataRightsAsync(
            staffMemberId,
            CancellationToken.None).ConfigureAwait(false));
        Assert.NotNull(await members.GetForSafetyTransitionAsync(
            staffMemberId,
            CancellationToken.None).ConfigureAwait(false));
    }

    private static async Task AssertAudienceAsync(
        AuthTestApplication api,
        Guid staffMemberId,
        IReadOnlyList<string> expectedPropertyAudience,
        string? expectedDirectRecipient)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        IStaffPropertyAudienceReader audience =
            scope.ServiceProvider.GetRequiredService<IStaffPropertyAudienceReader>();

        Assert.Equal(
            expectedPropertyAudience,
            await audience.ListActiveAuthSubjectIdsAsync(
                TenantId,
                PropertyId,
                CancellationToken.None).ConfigureAwait(false));
        Assert.Equal(
            expectedDirectRecipient,
            await audience.GetAuthSubjectIdAsync(
                TenantId,
                staffMemberId,
                CancellationToken.None).ConfigureAwait(false));
    }

    private static async Task AssertDurableStateAndPublishedEventsAsync(
        AuthTestApplication api,
        StaffMember member,
        DataRightsCase applyCase,
        DataRightsCase releaseCase,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope observation = api.Services.CreateScope();
            observation.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
                .SetTenant(TenantId);
            StaffDbContext observed =
                observation.ServiceProvider.GetRequiredService<StaffDbContext>();
            int published = await observed.OutboxMessages
                .AsNoTracking()
                .CountAsync(message =>
                    message.EventType ==
                        typeof(StaffProcessingRestrictionChangedIntegrationEvent).FullName &&
                    message.ProcessedAtUtc != null)
                .ConfigureAwait(false);
            if (published >= 2)
            {
                break;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        StaffDbContext staff =
            scope.ServiceProvider.GetRequiredService<StaffDbContext>();
        StaffProcessingRestriction restriction =
            await staff.ProcessingRestrictions
                .AsNoTracking()
                .SingleAsync()
                .ConfigureAwait(false);
        StaffProcessingRestrictionProjection projection =
            await staff.ProcessingRestrictionProjections
                .AsNoTracking()
                .SingleAsync(candidate => candidate.StaffMemberId == member.Id)
                .ConfigureAwait(false);
        StaffProcessingRestrictionReceipt[] receipts =
            await staff.ProcessingRestrictionReceipts
                .AsNoTracking()
                .OrderBy(receipt => receipt.CompletedAtUtc)
                .ThenBy(receipt => receipt.Id)
                .ToArrayAsync()
                .ConfigureAwait(false);
        string[] eventPayloads = await staff.OutboxMessages
            .AsNoTracking()
            .Where(message =>
                message.EventType ==
                    typeof(StaffProcessingRestrictionChangedIntegrationEvent).FullName)
            .OrderBy(message => message.OccurredAtUtc)
            .Select(message => message.Payload)
            .ToArrayAsync()
            .ConfigureAwait(false);
        int publishedEventCount = await staff.OutboxMessages
            .AsNoTracking()
            .CountAsync(message =>
                message.EventType ==
                    typeof(StaffProcessingRestrictionChangedIntegrationEvent).FullName &&
                message.ProcessedAtUtc != null)
            .ConfigureAwait(false);

        DataRightsDbContext dataRights =
            scope.ServiceProvider.GetRequiredService<DataRightsDbContext>();
        DataRightsCase[] completedCases = await dataRights.Cases
            .AsNoTracking()
            .Where(candidate =>
                candidate.Id == applyCase.Id ||
                candidate.Id == releaseCase.Id)
            .ToArrayAsync()
            .ConfigureAwait(false);

        Assert.Equal(StaffProcessingRestrictionState.Released, restriction.Status);
        Assert.Equal(releaseCase.Id, restriction.ReleaseCaseId);
        Assert.False(projection.IsRestricted);
        Assert.Equal(0, projection.ActiveRestrictionCount);
        Assert.Equal(2, projection.Revision);
        Assert.Equal(2, receipts.Length);
        Assert.Contains(
            receipts,
            receipt => receipt.Action == StaffProcessingRestrictionAction.Apply);
        Assert.Contains(
            receipts,
            receipt => receipt.Action == StaffProcessingRestrictionAction.Release);
        Assert.Equal(2, completedCases.Length);
        Assert.All(completedCases, dataRightsCase =>
        {
            Assert.Equal(DataRightsCaseState.Completed, dataRightsCase.Status);
            Assert.NotNull(dataRightsCase.RestrictionExecutionProof);
        });
        Assert.Equal(2, eventPayloads.Length);
        Assert.Equal(2, publishedEventCount);
        Assert.All(eventPayloads, payload =>
        {
            Assert.DoesNotContain(member.DisplayName, payload, StringComparison.Ordinal);
            Assert.DoesNotContain(member.WorkEmail!, payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(member.AuthSubjectId!, payload, StringComparison.Ordinal);
        });
    }

    private static async Task GrantRestrictionAccessAsync(
        AdminCliTestApplication admin,
        Guid operatorId)
    {
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin",
            "bootstrap",
            "--actor",
            "owner",
            "--yes"));
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin",
            "roles",
            "create",
            "--actor",
            "owner",
            "--name",
            "staff-privacy-restriction-operator"));
        foreach (string permission in new[]
                 {
                     DataRightsAdminPermissionCodes.Restrict,
                     StaffAdminPermissionCodes.Read,
                     StaffAdminPermissionCodes.ManageLifecycle
                 })
        {
            await AssertAdminSuccessAsync(admin.ExecuteAsync(
                "admin",
                "roles",
                "grant",
                "--actor",
                "owner",
                "--role",
                "staff-privacy-restriction-operator",
                "--permission",
                permission));
        }

        await AssertAdminSuccessAsync(admin.ExecuteAsync(
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
            "staff-privacy-restriction-operator",
            "--scope",
            $"tenant:{TenantId}"));
    }

    private static StaffDbContext CreateStaffDbContext(string connectionString)
    {
        DbContextOptions<StaffDbContext> options =
            new DbContextOptionsBuilder<StaffDbContext>()
                .UseNpgsql(connectionString, provider => provider
                    .MigrationsAssembly(StaffMigrations.PostgreSqlAssembly)
                    .MigrationsHistoryTable(
                        StaffMigrations.HistoryTable,
                        StaffMigrations.Schema))
                .Options;
        return new(
            options,
            new TestScopeContext(),
            OpenWorkspaceTerminationFenceReader.Instance);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? accessToken = null,
        object? body = null)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Add("X-Tenant-Id", TenantId);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new("Bearer", accessToken);
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
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync()
                .ConfigureAwait(false);
            Assert.Fail(
                $"Request failed with HTTP {(int)response.StatusCode} " +
                $"({response.StatusCode}): {body}");
        }

        T? value = await response.Content.ReadFromJsonAsync<T>()
            .ConfigureAwait(false);
        return Assert.IsType<T>(value);
    }

    private static async Task AssertStatusAsync(
        HttpStatusCode expected,
        HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync()
            .ConfigureAwait(false);
        Assert.True(
            response.StatusCode == expected,
            $"Expected HTTP {(int)expected}, received " +
            $"{(int)response.StatusCode}: {body}");
    }

    private static Guid GetSubjectId(string accessToken)
    {
        JwtSecurityToken token =
            new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        return Guid.Parse(token.Claims.Single(claim =>
            claim.Type is "sub" or "nameid").Value);
    }

    private static async Task AssertAdminSuccessAsync(
        Task<AdminCliResult> operation)
    {
        AdminCliResult result = await operation.ConfigureAwait(false);
        Assert.True(
            result.ExitCode == 0,
            $"Admin CLI failed with exit code {result.ExitCode}:" +
            $"{Environment.NewLine}{result.Output}{Environment.NewLine}" +
            result.Error);
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }
}
