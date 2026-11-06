namespace Integration.Tests;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using BunkFy.Modules.DataRights.Persistence;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Properties.Contracts;
using DotNet.Testcontainers.Containers;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Tenancy;
using Gma.Modules.Auth.Contracts;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class GuestProcessingRestrictionIntegrationTests
{
    private const string TenantId = "a5000000-0000-0000-0000-000000000001";
    private static readonly Guid PropertyId =
        Guid.Parse("75000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 24, 17, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Targeted_release_completes_one_of_multiple_owner_restrictions()
    {
        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await nats.StartAsync();
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_guest_processing_restriction_tests")
            .Build();
        await postgreSql.StartAsync();

        string connectionString = postgreSql.GetConnectionString();
        await using AuthTestApplication api = new(
            "PostgreSql",
            connectionString,
            AuthTestContainers.GetNatsConnectionString(nats));
        await api.MigrateGuestDataRightsAuthorizationDatabaseAsync().ConfigureAwait(false);
        await using AdminCliTestApplication admin = new("PostgreSql", connectionString);
        await admin.MigrateAsync().ConfigureAwait(false);
        using HttpClient client = api.CreateClient();

        AuthTokensResponse tokens = await AuthApiClient.RegisterAsync(
            client,
            TenantId,
            "privacy-restriction-operator@guests.test").ConfigureAwait(false);
        Guid operatorId = GetSubjectId(tokens.AccessToken);
        await api.SeedOrganizationMembershipAsync(TenantId, operatorId).ConfigureAwait(false);
        await GrantRestrictionAccessAsync(admin, operatorId).ConfigureAwait(false);

        (GuestProfile profile, DataRightsCase firstApplyCase, DataRightsCase secondApplyCase) =
            await SeedApprovedApplyCasesAsync(api).ConfigureAwait(false);
        Guid applyIdempotencyKey = Guid.NewGuid();
        DataRightsRestrictionExecutionDto firstApplied;
        using (HttpResponseMessage response = await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   $"/api/data-rights/properties/{PropertyId:D}/cases/" +
                   $"{firstApplyCase.Id:D}/restriction",
                   new
                   {
                       idempotencyKey = applyIdempotencyKey,
                       expectedVersion = firstApplyCase.Version
                   },
                   tokens.AccessToken).ConfigureAwait(false))
        {
            firstApplied = await ReadSuccessAsync<DataRightsRestrictionExecutionDto>(response)
                .ConfigureAwait(false);
        }

        Assert.Equal(DataRightsCaseStatus.Completed, firstApplied.Case.Status);
        Assert.Equal(DataRightsRestrictionDirective.Apply, firstApplied.Proof.Directive);
        Assert.True(firstApplied.Proof.EffectiveRestricted);
        Assert.Equal(1, firstApplied.Proof.ResultingOwnerRevision);
        Assert.Equal(1, firstApplied.Proof.ResultingProjectionRevision);

        using (HttpResponseMessage response = await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   $"/api/data-rights/properties/{PropertyId:D}/cases/" +
                   $"{firstApplyCase.Id:D}/restriction",
                   new
                   {
                       idempotencyKey = applyIdempotencyKey,
                       expectedVersion = firstApplyCase.Version
                   },
                   tokens.AccessToken).ConfigureAwait(false))
        {
            DataRightsRestrictionExecutionDto replay =
                await ReadSuccessAsync<DataRightsRestrictionExecutionDto>(response)
                    .ConfigureAwait(false);
            Assert.Equal(firstApplied.Proof, replay.Proof);
        }

        DataRightsRestrictionExecutionDto secondApplied;
        using (HttpResponseMessage response = await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   $"/api/data-rights/properties/{PropertyId:D}/cases/" +
                   $"{secondApplyCase.Id:D}/restriction",
                   new
                   {
                       idempotencyKey = Guid.NewGuid(),
                       expectedVersion = secondApplyCase.Version
                   },
                   tokens.AccessToken).ConfigureAwait(false))
        {
            secondApplied = await ReadSuccessAsync<DataRightsRestrictionExecutionDto>(response)
                .ConfigureAwait(false);
        }

        Assert.Equal(DataRightsCaseStatus.Completed, secondApplied.Case.Status);
        Assert.Equal(DataRightsRestrictionDirective.Apply, secondApplied.Proof.Directive);
        Assert.True(secondApplied.Proof.EffectiveRestricted);
        Assert.Equal(1, secondApplied.Proof.ResultingOwnerRevision);
        Assert.Equal(2, secondApplied.Proof.ResultingProjectionRevision);

        using (HttpResponseMessage response = await AuthApiClient.GetAsync(
                   client,
                   TenantId,
                   $"/api/guests/properties/{PropertyId:D}/{profile.Id:D}",
                   tokens.AccessToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (HttpResponseMessage response = await AuthApiClient.GetAsync(
                   client,
                   TenantId,
                   $"/api/guests/properties/{PropertyId:D}",
                   tokens.AccessToken).ConfigureAwait(false))
        {
            GuestListResponse visible = await ReadSuccessAsync<GuestListResponse>(response)
                .ConfigureAwait(false);
            Assert.Empty(visible.Guests);
        }

        using (HttpResponseMessage response = await AuthApiClient.GetAsync(
                   client,
                   TenantId,
                   $"/api/guests/properties/{PropertyId:D}/{profile.Id:D}/data-rights-restrictions",
                   tokens.AccessToken).ConfigureAwait(false))
        {
            GuestProcessingRestrictionListResponse active =
                await ReadSuccessAsync<GuestProcessingRestrictionListResponse>(response)
                    .ConfigureAwait(false);
            Assert.Equal(2, active.Restrictions.Count);
            Assert.Contains(active.Restrictions, item => item.ApplyCaseId == firstApplyCase.Id);
            Assert.Contains(active.Restrictions, item => item.ApplyCaseId == secondApplyCase.Id);
        }

        DataRightsCase releaseCase = await SeedReleaseDiscoveryCaseAsync(api, profile)
            .ConfigureAwait(false);
        DataRightsRestrictionReleaseTargetListResponse targets;
        using (HttpResponseMessage response = await AuthApiClient.GetAsync(
                   client,
                   TenantId,
                   $"/api/data-rights/properties/{PropertyId:D}/cases/" +
                   $"{releaseCase.Id:D}/restriction/release-targets",
                   tokens.AccessToken).ConfigureAwait(false))
        {
            targets = await ReadSuccessAsync<DataRightsRestrictionReleaseTargetListResponse>(
                    response)
                .ConfigureAwait(false);
        }

        Assert.Equal(releaseCase.Version, targets.CaseVersion);
        Assert.False(targets.LimitReached);
        Assert.Equal(2, targets.Targets.Count);
        Assert.Contains(targets.Targets, item => item.SourceCaseId == firstApplyCase.Id);
        Assert.Contains(targets.Targets, item => item.SourceCaseId == secondApplyCase.Id);
        DataRightsRestrictionReleaseTargetCandidateDto selectedTarget =
            targets.Targets.Single(item => item.SourceCaseId == firstApplyCase.Id);

        DataRightsCaseDto targetedCase;
        using (HttpResponseMessage response = await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   $"/api/data-rights/properties/{PropertyId:D}/cases/" +
                   $"{releaseCase.Id:D}/restriction/release-target",
                   new
                   {
                       ownerOperationId = selectedTarget.OwnerOperationId,
                       ownerOperationVersion = selectedTarget.OwnerOperationVersion,
                       expectedVersion = targets.CaseVersion
                   },
                   tokens.AccessToken).ConfigureAwait(false))
        {
            targetedCase = await ReadSuccessAsync<DataRightsCaseDto>(response)
                .ConfigureAwait(false);
        }

        Assert.Equal(
            DataRightsRestrictionContract.TargetBindingVersion,
            targetedCase.RestrictionTargetingContractVersion);
        Assert.Equal(
            selectedTarget.OwnerOperationId,
            targetedCase.RestrictionReleaseTarget?.OwnerOperationId);
        Assert.Equal(
            selectedTarget.OwnerOperationVersion,
            targetedCase.RestrictionReleaseTarget?.OwnerOperationVersion);

        DataRightsCaseDto reviewCase = await PostVersionedCaseAsync(
            client,
            tokens.AccessToken,
            releaseCase.Id,
            "review",
            targetedCase.Version).ConfigureAwait(false);
        DataRightsCaseDto decisionCase = await PostVersionedCaseAsync(
            client,
            tokens.AccessToken,
            releaseCase.Id,
            "decision",
            reviewCase.Version).ConfigureAwait(false);
        DataRightsCaseDto approvedCase;
        using (HttpResponseMessage response = await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   $"/api/data-rights/properties/{PropertyId:D}/cases/" +
                   $"{releaseCase.Id:D}/decision/outcome",
                   new
                   {
                       decision = DataRightsDecisionOutcome.Approved,
                       reason = DataRightsDecisionReason.RequestValidated,
                       expectedVersion = decisionCase.Version
                   },
                   tokens.AccessToken).ConfigureAwait(false))
        {
            approvedCase = await ReadSuccessAsync<DataRightsCaseDto>(response)
                .ConfigureAwait(false);
        }

        DataRightsRestrictionExecutionDto released;
        using (HttpResponseMessage response = await AuthApiClient.PostJsonAsync(
                   client,
                   TenantId,
                   $"/api/data-rights/properties/{PropertyId:D}/cases/" +
                   $"{releaseCase.Id:D}/restriction",
                   new
                   {
                       idempotencyKey = Guid.NewGuid(),
                       expectedVersion = approvedCase.Version
                   },
                   tokens.AccessToken).ConfigureAwait(false))
        {
            released = await ReadSuccessAsync<DataRightsRestrictionExecutionDto>(response)
                .ConfigureAwait(false);
        }

        Assert.Equal(DataRightsCaseStatus.Completed, released.Case.Status);
        Assert.Equal(DataRightsRestrictionDirective.Release, released.Proof.Directive);
        Assert.True(released.Proof.EffectiveRestricted);
        Assert.Equal(2, released.Proof.ResultingOwnerRevision);
        Assert.Equal(3, released.Proof.ResultingProjectionRevision);
        Assert.Equal(released.Proof, released.Case.RestrictionExecutionProof);
        Assert.Equal(
            selectedTarget.OwnerOperationId,
            released.Case.RestrictionReleaseTarget?.OwnerOperationId);

        using (HttpResponseMessage response = await AuthApiClient.GetAsync(
                   client,
                   TenantId,
                   $"/api/guests/properties/{PropertyId:D}/{profile.Id:D}",
                   tokens.AccessToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (HttpResponseMessage response = await AuthApiClient.GetAsync(
                   client,
                   TenantId,
                   $"/api/guests/properties/{PropertyId:D}",
                   tokens.AccessToken).ConfigureAwait(false))
        {
            GuestListResponse visible = await ReadSuccessAsync<GuestListResponse>(response)
                .ConfigureAwait(false);
            Assert.Empty(visible.Guests);
        }

        using (HttpResponseMessage response = await AuthApiClient.GetAsync(
                   client,
                   TenantId,
                   $"/api/guests/properties/{PropertyId:D}/{profile.Id:D}/data-rights-restrictions",
                   tokens.AccessToken).ConfigureAwait(false))
        {
            GuestProcessingRestrictionListResponse active =
                await ReadSuccessAsync<GuestProcessingRestrictionListResponse>(response)
                    .ConfigureAwait(false);
            GuestProcessingRestrictionDto remaining = Assert.Single(active.Restrictions);
            Assert.Equal(secondApplyCase.Id, remaining.ApplyCaseId);
        }

        using IServiceScope verificationScope = api.Services.CreateScope();
        verificationScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        GuestsDbContext guests =
            verificationScope.ServiceProvider.GetRequiredService<GuestsDbContext>();
        GuestProcessingRestriction[] restrictionRows = await guests.ProcessingRestrictions
            .AsNoTracking()
            .OrderBy(item => item.ApplyCaseId)
            .ToArrayAsync()
            .ConfigureAwait(false);
        GuestProcessingRestrictionProjection projection =
            await guests.ProcessingRestrictionProjections
                .AsNoTracking()
                .SingleAsync()
                .ConfigureAwait(false);
        GuestProcessingRestrictionReceipt[] receipts =
            await guests.ProcessingRestrictionReceipts
                .AsNoTracking()
                .OrderBy(receipt => receipt.CompletedAtUtc)
                .ThenBy(receipt => receipt.Id)
                .ToArrayAsync()
                .ConfigureAwait(false);
        DataRightsDbContext dataRights =
            verificationScope.ServiceProvider.GetRequiredService<DataRightsDbContext>();
        DataRightsCase[] completedCases = await dataRights.Cases
            .AsNoTracking()
            .Where(item =>
                item.Id == firstApplyCase.Id ||
                item.Id == secondApplyCase.Id ||
                item.Id == releaseCase.Id)
            .OrderBy(item => item.Id)
            .ToArrayAsync()
            .ConfigureAwait(false);

        Assert.Equal(2, restrictionRows.Length);
        GuestProcessingRestriction releasedRow = restrictionRows.Single(
            item => item.Id == selectedTarget.OwnerOperationId);
        GuestProcessingRestriction remainingRow = restrictionRows.Single(
            item => item.Id != selectedTarget.OwnerOperationId);
        Assert.Equal(GuestProcessingRestrictionState.Released, releasedRow.Status);
        Assert.Equal(releaseCase.Id, releasedRow.ReleaseCaseId);
        Assert.Equal(approvedCase.DecisionRevision, releasedRow.ReleaseApprovalRevision);
        Assert.Equal(GuestProcessingRestrictionState.Active, remainingRow.Status);
        Assert.Null(remainingRow.ReleaseCaseId);
        Assert.True(projection.IsRestricted);
        Assert.Equal(1, projection.ActiveRestrictionCount);
        Assert.Equal(3, projection.Revision);
        Assert.Equal(3, receipts.Length);
        Assert.Contains(receipts, receipt => receipt.Action == GuestProcessingRestrictionAction.Apply);
        Assert.Contains(receipts, receipt => receipt.Action == GuestProcessingRestrictionAction.Release);
        Assert.Equal(3, completedCases.Length);
        Assert.All(completedCases, item =>
        {
            Assert.Equal(DataRightsCaseState.Completed, item.Status);
            Assert.NotNull(item.RestrictionExecutionProof);
        });
        DataRightsCase persistedRelease = completedCases.Single(item => item.Id == releaseCase.Id);
        Assert.Equal(
            DataRightsRestrictionContract.TargetBindingVersion,
            persistedRelease.RestrictionTargetingContractVersion);
        Assert.Equal(
            selectedTarget.OwnerOperationId,
            persistedRelease.RestrictionReleaseTarget?.OwnerOperationId);
        Assert.True(persistedRelease.RestrictionExecutionProof?.EffectiveRestricted);
    }

    private static async Task<(
        GuestProfile Profile,
        DataRightsCase FirstApply,
        DataRightsCase SecondApply)> SeedApprovedApplyCasesAsync(AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        GuestsDbContext guests = scope.ServiceProvider.GetRequiredService<GuestsDbContext>();
        guests.PropertyProjections.Add(new GuestPropertyProjection(
            TenantId,
            PropertyId,
            "Restriction House",
            PropertyStatus.Active,
            1));
        await CountryPolicyIntegrationTestData.ApplyActivationAsync(
            scope.ServiceProvider,
            GuestsModuleMetadata.Name,
            TenantId,
            PropertyId,
            2).ConfigureAwait(false);
        GuestProfile profile = GuestProfile.Create(
            Guid.NewGuid(),
            TenantId,
            PropertyId,
            "Restricted Guest",
            null,
            "restricted@example.test",
            null,
            null,
            null,
            null,
            null,
            "user:seed",
            Guid.NewGuid(),
            Now).Value;
        guests.GuestProfiles.Add(profile);
        guests.ProcessingRestrictionProjections.Add(
            GuestProcessingRestrictionProjection.Create(
                TenantId,
                PropertyId,
                profile.Id,
                GuestProcessingRestrictionContract.CurrentVersion,
                Now).Value);
        await guests.SaveChangesAsync().ConfigureAwait(false);

        DataRightsCase firstApply = CreateApprovedCase(
            profile,
            DataRightsRestrictionAction.Apply,
            Now.AddMinutes(1));
        DataRightsCase secondApply = CreateApprovedCase(
            profile,
            DataRightsRestrictionAction.Apply,
            Now.AddMinutes(10));
        DataRightsDbContext dataRights =
            scope.ServiceProvider.GetRequiredService<DataRightsDbContext>();
        dataRights.Cases.AddRange(firstApply, secondApply);
        await dataRights.SaveChangesAsync().ConfigureAwait(false);
        return (profile, firstApply, secondApply);
    }

    private static async Task<DataRightsCase> SeedReleaseDiscoveryCaseAsync(
        AuthTestApplication api,
        GuestProfile profile)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            PropertyId,
            DataRightsCaseKind.GuestRights,
            DataRightsCaseOperation.Restriction,
            DataRightsRequesterRelation.ControllerInitiated,
            DataRightsRestrictionAction.Release).Value;
        DataRightsCase releaseCase = DataRightsCase.Create(
            Guid.NewGuid(),
            TenantId,
            request,
            "user:privacy-reviewer",
            Now.AddMinutes(20)).Value;
        Assert.True(releaseCase.BeginDiscovery(
            releaseCase.Version,
            "user:privacy-reviewer",
            Now.AddMinutes(21)).IsSuccess);
        Assert.True(releaseCase.SelectSubject(
            GuestsDataRightsCoordinates.Owner,
            GuestsDataRightsCoordinates.GuestProfileRecordType,
            profile.Id,
            profile.Version,
            releaseCase.Version,
            "user:privacy-reviewer",
            Now.AddMinutes(22)).IsSuccess);
        DataRightsDbContext dataRights =
            scope.ServiceProvider.GetRequiredService<DataRightsDbContext>();
        dataRights.Cases.Add(releaseCase);
        await dataRights.SaveChangesAsync().ConfigureAwait(false);
        return releaseCase;
    }

    private static DataRightsCase CreateApprovedCase(
        GuestProfile profile,
        DataRightsRestrictionAction action,
        DateTimeOffset startedAtUtc)
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            PropertyId,
            DataRightsCaseKind.GuestRights,
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
            GuestsDataRightsCoordinates.Owner,
            GuestsDataRightsCoordinates.GuestProfileRecordType,
            profile.Id,
            profile.Version,
            dataRightsCase.Version,
            "user:privacy-reviewer",
            startedAtUtc.AddMinutes(2)).IsSuccess);
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
        return dataRightsCase;
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
            "privacy-restriction-operator"));
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin",
            "roles",
            "grant",
            "--actor",
            "owner",
            "--role",
            "privacy-restriction-operator",
            "--permission",
            DataRightsAdminPermissionCodes.Restrict));
        await GrantPermissionAsync(
            admin,
            DataRightsAdminPermissionCodes.Discover).ConfigureAwait(false);
        await GrantPermissionAsync(
            admin,
            DataRightsAdminPermissionCodes.Review).ConfigureAwait(false);
        await GrantPermissionAsync(
            admin,
            DataRightsAdminPermissionCodes.Decide).ConfigureAwait(false);
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin",
            "roles",
            "grant",
            "--actor",
            "owner",
            "--role",
            "privacy-restriction-operator",
            "--permission",
            GuestsAdminPermissionCodes.Read));
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
            "privacy-restriction-operator",
            "--scope",
            $"tenant:{TenantId}/property:{PropertyId:D}"));
    }

    private static async Task GrantPermissionAsync(
        AdminCliTestApplication admin,
        string permission) =>
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin",
            "roles",
            "grant",
            "--actor",
            "owner",
            "--role",
            "privacy-restriction-operator",
            "--permission",
            permission));

    private static async Task<DataRightsCaseDto> PostVersionedCaseAsync(
        HttpClient client,
        string accessToken,
        Guid caseId,
        string action,
        long expectedVersion)
    {
        using HttpResponseMessage response = await AuthApiClient.PostJsonAsync(
            client,
            TenantId,
            $"/api/data-rights/properties/{PropertyId:D}/cases/{caseId:D}/{action}",
            new { expectedVersion },
            accessToken).ConfigureAwait(false);
        return await ReadSuccessAsync<DataRightsCaseDto>(response).ConfigureAwait(false);
    }

    private static async Task<T> ReadSuccessAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.Fail(
                $"Request failed with HTTP {(int)response.StatusCode} ({response.StatusCode}): {body}");
        }

        T? value = await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false);
        return Assert.IsType<T>(value);
    }

    private static Guid GetSubjectId(string accessToken)
    {
        JwtSecurityToken token = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        return Guid.Parse(token.Claims.Single(claim =>
            claim.Type is "sub" or "nameid").Value);
    }

    private static async Task AssertAdminSuccessAsync(Task<AdminCliResult> operation)
    {
        AdminCliResult result = await operation.ConfigureAwait(false);
        Assert.True(
            result.ExitCode == 0,
            $"Admin CLI failed with exit code {result.ExitCode}:{Environment.NewLine}" +
            $"{result.Output}{Environment.NewLine}{result.Error}");
    }
}
