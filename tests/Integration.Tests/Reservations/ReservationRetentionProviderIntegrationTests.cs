namespace Integration.Tests;

using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.Retention;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Retention.Application.Commands;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Domain.Models;
using BunkFy.Modules.Retention.Persistence;
using Gma.Framework.Cqrs;
using Gma.Framework.Messaging;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Cqrs;
using Gma.Framework.Tenancy;
using Gma.Modules.Organizations.Contracts;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed partial class RetentionControlPlaneIntegrationTests
{
    private const string ReservationProviderPolicyUnavailableOutcome =
        "reservations.reservation-operational.policy-unavailable";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task
        Reservations_provider_replay_is_exact_and_tenant_fenced()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservation_retention_provider")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        var clock = new ReservationProviderMutableClock(
            DateTimeOffset.UtcNow);
        using IHost worker = CreateWorker(
            postgreSql.GetConnectionString(),
            clock);
        await MigrateAsync(worker).ConfigureAwait(false);

        const string exactTenant =
            "6e000000-0000-0000-0000-000000000001";
        Guid exactPropertyId = Guid.Parse(
            "6e100000-0000-0000-0000-000000000001");
        Guid exactReservationId =
            await SeedReservationProviderCandidateAsync(
                worker,
                exactTenant,
                exactPropertyId).ConfigureAwait(false);
        RetentionContributionRequest exactRequest =
            CreateReservationProviderRequest(
                worker,
                exactTenant,
                Guid.NewGuid(),
                attempt: 1);
        DateTimeOffset rawCompletedAtUtc =
            WithReservationProviderSubMicrosecondTicks(
                exactRequest.StartedAtUtc.AddSeconds(1));
        clock.UtcNow = rawCompletedAtUtc;

        RetentionContributionResult first =
            await ExecuteReservationProviderAsync(
                worker,
                exactTenant,
                exactRequest).ConfigureAwait(false);
        RetentionContributionResult replay =
            await ExecuteReservationProviderAsync(
                worker,
                exactTenant,
                exactRequest).ConfigureAwait(false);

        Assert.Equal(first, replay);
        Assert.NotEqual(
            0,
            rawCompletedAtUtc.Ticks % TimeSpan.TicksPerMicrosecond);
        Assert.Equal(
            rawCompletedAtUtc.AddTicks(
                -(rawCompletedAtUtc.Ticks %
                    TimeSpan.TicksPerMicrosecond)),
            first.CompletedAtUtc);
        Assert.Equal(0, first.CompletedAtUtc.Ticks %
            TimeSpan.TicksPerMicrosecond);
        Assert.Equal(RetentionContributionStatus.Completed, first.Status);
        Assert.Equal(1, first.ScannedCount);
        Assert.Equal(1, first.AffectedCount);
        Assert.Equal(0, first.RemainingCount);
        await AssertReservationProviderProofAsync(
            worker,
            exactTenant,
            exactReservationId,
            exactRequest.ExecutionId,
            expectedAttempt: 1,
            first.CompletedAtUtc).ConfigureAwait(false);

        const string localTenant =
            "6e000000-0000-0000-0000-000000000002";
        const string foreignTenant =
            "6e000000-0000-0000-0000-000000000003";
        Guid sharedPropertyId = Guid.Parse(
            "6e100000-0000-0000-0000-000000000002");
        Guid localReservationId;
        using (IServiceScope local = CreateReservationProviderScope(
            worker,
            localTenant))
        {
            localReservationId = await SeedReservationAsync(
                local.ServiceProvider,
                localTenant,
                sharedPropertyId,
                DateTimeOffset.UtcNow).ConfigureAwait(false);
        }

        await ApplyReservationProviderPropertyAsync(
            worker,
            foreignTenant,
            sharedPropertyId).ConfigureAwait(false);
        using (IServiceScope foreign = CreateReservationProviderScope(
            worker,
            foreignTenant))
        {
            ReservationsDbContext reservations = foreign.ServiceProvider
                .GetRequiredService<ReservationsDbContext>();
            reservations.DataHolds.Add(ReservationDataHold.Place(
                Guid.NewGuid(),
                foreignTenant,
                sharedPropertyId,
                localReservationId,
                "regulatory-request",
                "integration:foreign-tenant",
                DateTimeOffset.UtcNow.AddDays(-1)).Value);
            reservations.ProcessingRestrictionProjections.Add(
                ReservationProcessingRestrictionProjection.Create(
                    foreignTenant,
                    sharedPropertyId,
                    localReservationId,
                    ReservationProcessingRestrictionContract.CurrentVersion +
                        1,
                    DateTimeOffset.UtcNow.AddDays(-1)).Value);
            await reservations.SaveChangesAsync().ConfigureAwait(false);
        }

        using (IServiceScope local = CreateReservationProviderScope(
            worker,
            localTenant))
        {
            ReservationsDbContext reservations = local.ServiceProvider
                .GetRequiredService<ReservationsDbContext>();
            object repository = CreateReservationCandidateRepository(
                reservations);
            ReservationCandidateScanProof page =
                await InvokeReservationCandidateScanAsync(
                    repository,
                    afterProjectionOrdinal: 0,
                    limit: 10,
                    CancellationToken.None).ConfigureAwait(false);
            object candidate = Assert.Single(page.Candidates);
            Assert.Equal(
                localReservationId,
                GetReservationCandidateReservationId(candidate));
            Assert.Equal(0, GetReservationCandidateActiveHoldCount(
                candidate));
            Assert.Equal(
                ReservationProcessingRestrictionContract.CurrentVersion,
                GetReservationCandidateRestrictionVersion(candidate));
            Assert.Null(GetReservationCandidateProperty(candidate));
        }

        RetentionContributionRequest fencedRequest =
            CreateReservationProviderRequest(
                worker,
                localTenant,
                Guid.NewGuid(),
                attempt: 1);
        clock.UtcNow = WithReservationProviderSubMicrosecondTicks(
            fencedRequest.StartedAtUtc.AddSeconds(1));
        RetentionContributionResult fenced =
            await ExecuteReservationProviderAsync(
                worker,
                localTenant,
                fencedRequest).ConfigureAwait(false);
        Assert.Equal(RetentionContributionStatus.Failed, fenced.Status);
        Assert.Equal(
            ReservationProviderPolicyUnavailableOutcome,
            fenced.OutcomeCode);
        await AssertReservationProviderHasNoMutationAsync(
            worker,
            localTenant,
            localReservationId).ConfigureAwait(false);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task
        Reservations_provider_bounds_growth_and_recovers_partial_attempt()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservation_retention_bounds")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        using IHost worker = CreateWorker(
            postgreSql.GetConnectionString());
        await MigrateAsync(worker).ConfigureAwait(false);
        await AssertReservationProviderPageBoundAsync(worker)
            .ConfigureAwait(false);
        await AssertReservationProviderPolicySnapshotIsAtomicAsync(
            worker,
            postgreSql.GetConnectionString()).ConfigureAwait(false);
        await AssertReservationProviderLegacyCheckpointRetryAsync(worker)
            .ConfigureAwait(false);
        await AssertReservationProviderSplitCrashRecoveryAsync(worker)
            .ConfigureAwait(false);

        const string tenantId =
            "6e000000-0000-0000-0000-000000000010";
        Guid validPropertyId = Guid.Parse(
            "6e100000-0000-0000-0000-000000000010");
        Guid overflowPropertyId = Guid.Parse(
            "6e100000-0000-0000-0000-000000000011");
        Guid validReservationId =
            await SeedReservationProviderCandidateAsync(
                worker,
                tenantId,
                validPropertyId).ConfigureAwait(false);
        Guid overflowReservationId =
            await SeedReservationProviderCandidateAsync(
                worker,
                tenantId,
                overflowPropertyId).ConfigureAwait(false);
        await InsertReservationAcknowledgementsAsync(
            worker,
            tenantId,
            overflowPropertyId,
            PropertiesContractLimits.MaximumPolicyAcknowledgements - 1)
            .ConfigureAwait(false);

        using (IServiceScope scope = CreateReservationProviderScope(
            worker,
            tenantId))
        {
            ReservationsDbContext reservations = scope.ServiceProvider
                .GetRequiredService<ReservationsDbContext>();
            ReservationCandidateScanProof maximum =
                await InvokeReservationCandidateScanAsync(
                    CreateReservationCandidateRepository(reservations),
                    afterProjectionOrdinal: 0,
                    limit: 10,
                    CancellationToken.None).ConfigureAwait(false);
            object overflowCandidate = Assert.Single(
                maximum.Candidates,
                candidate => GetReservationCandidatePropertyId(candidate) ==
                    overflowPropertyId);
            Assert.Equal(
                PropertiesContractLimits.MaximumPolicyAcknowledgements,
                GetReservationCandidateAcknowledgementCount(
                    overflowCandidate));
        }

        ReservationCandidateScanProof raced;
        using (IServiceScope scope = CreateReservationProviderScope(
            worker,
            tenantId))
        {
            string connectionString = scope.ServiceProvider
                .GetRequiredService<ReservationsDbContext>()
                .Database.GetConnectionString()!;
            var growth = new ReservationAcknowledgementGrowthInterceptor(
                connectionString,
                overflowPropertyId);
            DbContextOptions<ReservationsDbContext> options =
                new DbContextOptionsBuilder<ReservationsDbContext>()
                    .UseNpgsql(connectionString)
                    .AddInterceptors(growth)
                    .Options;
            await using var racedContext = new ReservationsDbContext(
                options,
                new ReservationProviderScopeContext(tenantId));
            raced = await InvokeReservationCandidateScanAsync(
                CreateReservationCandidateRepository(racedContext),
                afterProjectionOrdinal: 0,
                limit: 10,
                CancellationToken.None).ConfigureAwait(false);
            Assert.True(growth.Inserted);
            Assert.Equal(1, growth.MatchedCommandCount);
            AssertReservationPropertyPolicyQueryShape(
                growth.MatchedCommandText);
        }

        object racedOverflow = Assert.Single(
            raced.Candidates,
            candidate => GetReservationCandidatePropertyId(candidate) ==
                overflowPropertyId);
        object? racedProperty = GetReservationCandidateProperty(
            racedOverflow);
        Assert.NotNull(racedProperty);
        Assert.Equal(
            PropertiesContractLimits.MaximumPolicyAcknowledgements,
            GetReservationCandidateAcknowledgementCount(
                racedOverflow));

        await AssertReservationProviderTaskRetryAsync(
            worker,
            tenantId,
            validReservationId,
            overflowPropertyId,
            overflowReservationId,
            GetReservationCandidateVersion(racedOverflow),
            GetReservationCandidateDetailsRevision(racedOverflow))
            .ConfigureAwait(false);
    }

    private static async Task AssertReservationProviderPageBoundAsync(
        IHost worker)
    {
        const string tenantId =
            "6e000000-0000-0000-0000-000000000020";
        Guid propertyId = Guid.Parse(
            "6e100000-0000-0000-0000-000000000020");
        await ApplyReservationProviderPropertyAsync(
            worker,
            tenantId,
            propertyId).ConfigureAwait(false);
        using (IServiceScope seed = CreateReservationProviderScope(
            worker,
            tenantId))
        {
            await SeedReservationAsync(
                seed.ServiceProvider,
                tenantId,
                propertyId,
                DateTimeOffset.UtcNow).ConfigureAwait(false);
            await SeedReservationAsync(
                seed.ServiceProvider,
                tenantId,
                propertyId,
                DateTimeOffset.UtcNow).ConfigureAwait(false);
        }

        using IServiceScope scope = CreateReservationProviderScope(
            worker,
            tenantId);
        ReservationsDbContext reservations = scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>();
        object repository = CreateReservationCandidateRepository(
            reservations);
        ReservationCandidateScanProof first =
            await InvokeReservationCandidateScanAsync(
                repository,
                afterProjectionOrdinal: 0,
                limit: 1,
                CancellationToken.None).ConfigureAwait(false);
        object firstCandidate = Assert.Single(first.Candidates);
        Assert.False(first.ReachedEnd);
        long firstOrdinal = GetReservationCandidateProjectionOrdinal(
            firstCandidate);
        ReservationCandidateScanProof second =
            await InvokeReservationCandidateScanAsync(
                repository,
                firstOrdinal,
                limit: 1,
                CancellationToken.None).ConfigureAwait(false);
        object secondCandidate = Assert.Single(second.Candidates);
        Assert.True(second.ReachedEnd);
        Assert.True(
            GetReservationCandidateProjectionOrdinal(secondCandidate) >
            firstOrdinal);

        ArgumentOutOfRangeException failure =
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                InvokeReservationCandidateScanAsync(
                    repository,
                    afterProjectionOrdinal: 0,
                    limit: 1001,
                    CancellationToken.None));
        Assert.Equal("limit", failure.ParamName);
    }

    private static async Task
        AssertReservationProviderPolicySnapshotIsAtomicAsync(
            IHost worker,
            string connectionString)
    {
        const string tenantId =
            "6e000000-0000-0000-0000-000000000030";
        Guid propertyId = Guid.Parse(
            "6e100000-0000-0000-0000-000000000030");
        Guid reservationId =
            await SeedReservationProviderCandidateAsync(
                worker,
                tenantId,
                propertyId).ConfigureAwait(false);
        var replacement = new ReservationPolicyGenerationReplacementInterceptor(
            connectionString,
            propertyId);
        DbContextOptions<ReservationsDbContext> options =
            new DbContextOptionsBuilder<ReservationsDbContext>()
                .UseNpgsql(connectionString)
                .AddInterceptors(replacement)
                .Options;
        ReservationCandidateScanProof page;
        await using (var racedContext = new ReservationsDbContext(
            options,
            new ReservationProviderScopeContext(tenantId)))
        {
            page = await InvokeReservationCandidateScanAsync(
                CreateReservationCandidateRepository(racedContext),
                afterProjectionOrdinal: 0,
                limit: 1,
                CancellationToken.None).ConfigureAwait(false);
        }

        Assert.True(replacement.Replaced);
        Assert.Equal(1, replacement.MatchedCommandCount);
        AssertReservationPropertyPolicyQueryShape(
            replacement.MatchedCommandText);
        object candidate = Assert.Single(page.Candidates);
        Assert.Equal(
            reservationId,
            GetReservationCandidateReservationId(candidate));
        Assert.Equal(2, GetReservationCandidatePolicySourceVersion(
            candidate));
        PropertyGovernancePolicyBinding policy =
            GetReservationCandidatePolicy(candidate);
        Assert.Equal("integration-hostel-baseline", policy.PolicyId);
        PropertyGovernanceAcknowledgement acknowledgement =
            Assert.Single(policy.Acknowledgements);
        Assert.Equal(
            "integration-operator-notice",
            acknowledgement.AcknowledgementId);
        Assert.Equal(1, acknowledgement.AcknowledgementVersion);

        await using var connection = new NpgsqlConnection(
            connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                property."PolicySourceVersion",
                property."JurisdictionPolicyId",
                acknowledgement."AcknowledgementId"
            FROM reservations.property_projection AS property
            INNER JOIN reservations.property_policy_acknowledgements
                AS acknowledgement
                ON acknowledgement."PropertyId" = property."Id"
            WHERE property."Id" = @property_id;
            """;
        command.Parameters.AddWithValue("property_id", propertyId);
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.True(await reader.ReadAsync().ConfigureAwait(false));
        Assert.Equal(3L, reader.GetInt64(0));
        Assert.Equal(
            "reservation-provider-replacement",
            reader.GetString(1));
        Assert.Equal(
            "reservation-provider-replacement-ack",
            reader.GetString(2));
        Assert.False(await reader.ReadAsync().ConfigureAwait(false));
    }

    private static async Task
        AssertReservationProviderLegacyCheckpointRetryAsync(
            IHost worker)
    {
        const string tenantId =
            "6e000000-0000-0000-0000-000000000035";
        Guid propertyId = Guid.Parse(
            "6e100000-0000-0000-0000-000000000035");
        Guid reservationId =
            await SeedReservationProviderCandidateAsync(
                worker,
                tenantId,
                propertyId).ConfigureAwait(false);
        Guid executionId = Guid.NewGuid();
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow
            .AddMinutes(-2);
        startedAtUtc = startedAtUtc.AddTicks(
            -(startedAtUtc.Ticks %
                TimeSpan.TicksPerMicrosecond));
        DateTimeOffset completedAtUtc =
            startedAtUtc.AddMinutes(1);
        long advancedCursor;
        using (IServiceScope seed = CreateReservationProviderScope(
            worker,
            tenantId))
        {
            ReservationsDbContext reservations = seed.ServiceProvider
                .GetRequiredService<ReservationsDbContext>();
            advancedCursor = await reservations.Reservations
                .AsNoTracking()
                .Where(item => item.Id == reservationId)
                .Select(item => item.ProjectionOrdinal)
                .SingleAsync().ConfigureAwait(false);
            ReservationRetentionSweepCheckpoint checkpoint =
                ReservationRetentionSweepCheckpoint.Create(
                    Guid.NewGuid(),
                    tenantId,
                    ReservationOperationalDataClass,
                    executionPolicyVersion: 1,
                    startedAtUtc).Value;
            ReservationRetentionExecution execution =
                ReservationRetentionExecution.Start(
                    executionId,
                    tenantId,
                    ReservationOperationalDataClass,
                    executionPolicyVersion: 1,
                    attempt: 1,
                    startingProjectionOrdinal: 0,
                    startedAtUtc,
                    startedAtUtc.AddMinutes(10)).Value;
            Assert.True(execution.Complete(
                ReservationRetentionExecutionState.Failed,
                attempt: 1,
                scannedCount: 1,
                remainingCount: 1,
                ReservationProviderPolicyUnavailableOutcome,
                completedAtUtc,
                holdReviewDueAtUtc: null).IsSuccess);
            Assert.True(checkpoint.Advance(
                expectedAfterProjectionOrdinal: 0,
                advancedCursor,
                executionId,
                completedAtUtc).IsSuccess);
            reservations.RetentionSweepCheckpoints.Add(checkpoint);
            reservations.RetentionExecutions.Add(execution);
            await reservations.SaveChangesAsync().ConfigureAwait(false);
        }

        using (IServiceScope legacy = CreateReservationProviderScope(
            worker,
            tenantId))
        {
            ReservationsDbContext reservations = legacy.ServiceProvider
                .GetRequiredService<ReservationsDbContext>();
            ReservationRetentionExecution failed = await reservations
                .RetentionExecutions.AsNoTracking()
                .SingleAsync().ConfigureAwait(false);
            ReservationRetentionSweepCheckpoint advanced =
                await reservations.RetentionSweepCheckpoints
                    .AsNoTracking()
                    .SingleAsync().ConfigureAwait(false);
            Assert.Equal(
                ReservationRetentionExecutionState.Failed,
                failed.State);
            Assert.Equal(failed.Id, advanced.LastExecutionId);
            Assert.Equal(advancedCursor, advanced.AfterProjectionOrdinal);
            Assert.Equal(failed.CompletedAtUtc, advanced.UpdatedAtUtc);
        }

        RetentionContributionRequest retry =
            CreateReservationProviderRequest(
                worker,
                tenantId,
                executionId,
                attempt: 2);
        RetentionContributionResult result =
            await ExecuteReservationProviderAsync(
                worker,
                tenantId,
                retry).ConfigureAwait(false);
        Assert.Equal(RetentionContributionStatus.Completed, result.Status);
        Assert.Equal(1, result.ScannedCount);
        Assert.Equal(1, result.AffectedCount);
        Assert.Equal(0, result.RemainingCount);
        await AssertReservationProviderProofAsync(
            worker,
            tenantId,
            reservationId,
            executionId,
            expectedAttempt: 2,
            result.CompletedAtUtc,
            earliestReceiptAtUtc: retry.StartedAtUtc)
            .ConfigureAwait(false);
        using IServiceScope proof = CreateReservationProviderScope(
            worker,
            tenantId);
        ReservationRetentionSweepCheckpoint recovered = await proof
            .ServiceProvider.GetRequiredService<ReservationsDbContext>()
            .RetentionSweepCheckpoints.AsNoTracking()
            .SingleAsync().ConfigureAwait(false);
        Assert.Equal(0, recovered.AfterProjectionOrdinal);
        Assert.Equal(executionId, recovered.LastExecutionId);
    }

    private static async Task
        AssertReservationProviderSplitCrashRecoveryAsync(
            IHost worker)
    {
        const string tenantId =
            "6e000000-0000-0000-0000-000000000040";
        Guid propertyId = Guid.Parse(
            "6e100000-0000-0000-0000-000000000040");
        Guid reservationId =
            await SeedReservationProviderCandidateAsync(
                worker,
                tenantId,
                propertyId).ConfigureAwait(false);
        await ApplyReservationProviderRetentionTenantAsync(
            worker,
            tenantId).ConfigureAwait(false);
        Guid runId = Guid.NewGuid();
        var payload = new ExecuteRetentionSchedulePayload(
            ReservationRetentionOwner,
            ReservationOperationalDataClass,
            ExecutionPolicyVersion: 1,
            RetentionTargetScopeKind.Tenant);
        DateTimeOffset createdAtUtc = DateTimeOffset.UtcNow;
        using (IServiceScope enqueue = worker.Services.CreateScope())
        {
            TaskRunEnqueueResult enqueued = await enqueue.ServiceProvider
                .GetRequiredService<ITaskRunStore>()
                .EnqueueAsync(
                    new TaskRunRequest(
                        runId,
                        RetentionModuleMetadata.Name,
                        ExecuteRetentionSchedulePayload.TaskName,
                        JsonSerializer.Serialize(payload),
                        createdAtUtc,
                        createdAtUtc,
                        RetentionModuleMetadata.WorkerGroup,
                        tenantId,
                        requestedBy:
                            "reservation-provider-split-crash",
                        maxAttempts: 1,
                        payloadVersion:
                            ExecuteRetentionSchedulePayload.PayloadVersion,
                        deduplicationKey:
                            $"reservation-provider-split:{runId:N}"),
                    CancellationToken.None).ConfigureAwait(false);
            Assert.True(enqueued.Created);
        }

        TaskRunLease firstLease = await ClaimReservationProviderRunAsync(
            worker,
            createdAtUtc.AddSeconds(1),
            "reservation-provider-split-worker-1")
            .ConfigureAwait(false);
        Assert.Equal(1, firstLease.Attempt);
        Assert.Equal(1, firstLease.LeaseGeneration);
        TaskExecutionContext firstContext =
            firstLease.CreateExecutionContext();
        using (IServiceScope started = worker.Services.CreateScope())
        {
            Assert.Equal(
                TaskRunMutationOutcome.Applied,
                await started.ServiceProvider
                    .GetRequiredService<ITaskRunStore>()
                    .MarkStartedAsync(
                        firstContext,
                        DateTimeOffset.UtcNow,
                        CancellationToken.None).ConfigureAwait(false));
        }

        RetentionContributionResult ownerResult;
        using (IServiceScope ownerScope = CreateReservationProviderScope(
            worker,
            tenantId))
        {
            IRetentionExecutionContributor contributor = ownerScope
                .ServiceProvider
                .GetServices<IRetentionExecutionContributor>()
                .Single(item =>
                    item.Schedule.OwnerKey == ReservationRetentionOwner &&
                    item.Schedule.DataClassKey ==
                        ReservationOperationalDataClass);
            DateTimeOffset startedAtUtc = ownerScope.ServiceProvider
                .GetRequiredService<ISystemClock>().UtcNow;
            Result<RetentionExecutionStart> central = await ownerScope
                .ServiceProvider
                .GetRequiredService<ITaskCommandDispatcher>()
                .DispatchAsync<
                    BeginRetentionExecutionCommand,
                    RetentionExecutionStart>(
                    firstContext,
                    new(
                        runId,
                        tenantId,
                        payload.OwnerKey,
                        payload.DataClassKey,
                        payload.TargetScopeKind,
                        payload.PropertyId,
                        payload.ExecutionPolicyVersion,
                        firstLease.LeaseGeneration,
                        startedAtUtc,
                        startedAtUtc +
                            contributor.Schedule.ExecutionTimeout,
                        startedAtUtc + contributor.Schedule.Interval),
                    CancellationToken.None).ConfigureAwait(false);
            Assert.True(central.IsSuccess, central.Error.Code);
            Assert.True(central.Value.DispatchRequired);
            Assert.False(central.Value.AttemptAdvanced);
            ownerResult = await contributor.ExecuteAsync(
                central.Value.Request,
                CancellationToken.None).ConfigureAwait(false);
        }

        Assert.Equal(
            RetentionContributionStatus.Completed,
            ownerResult.Status);
        ReservationProviderFenceProof split =
            await ReadReservationProviderFenceProofAsync(
                worker,
                tenantId,
                runId).ConfigureAwait(false);
        Assert.Equal(1, split.OwnerAttempt);
        Assert.Equal(
            ReservationRetentionExecutionState.Completed,
            split.OwnerState);
        Assert.Equal(1, split.CentralAttempt);
        Assert.Equal(RetentionExecutionState.Running, split.CentralState);
        Assert.Equal(runId, split.CheckpointLastExecutionId);
        await AssertReservationProviderSingleMutationAsync(
            worker,
            tenantId,
            reservationId,
            expectedReceiptCount: 1).ConfigureAwait(false);

        using (IServiceScope failed = worker.Services.CreateScope())
        {
            Assert.Equal(
                TaskRunMutationOutcome.Applied,
                await failed.ServiceProvider
                    .GetRequiredService<ITaskRunStore>()
                    .MarkFailedAsync(
                        firstContext,
                        "simulated split crash after owner commit",
                        DateTimeOffset.UtcNow,
                        retryAtUtc: null,
                        CancellationToken.None).ConfigureAwait(false));
        }

        await Task.Delay(10).ConfigureAwait(false);
        DateTimeOffset retryAtUtc = DateTimeOffset.UtcNow;
        using (IServiceScope retry = worker.Services.CreateScope())
        {
            Assert.Equal(
                TaskRunMutationOutcome.Applied,
                await retry.ServiceProvider
                    .GetRequiredService<ITaskRunStore>()
                    .RetryAsync(
                        runId,
                        "reservation-provider-split-operator",
                        retryAtUtc,
                        CancellationToken.None).ConfigureAwait(false));
        }

        TaskRunLease secondLease = await ClaimReservationProviderRunAsync(
            worker,
            retryAtUtc.AddSeconds(1),
            "reservation-provider-split-worker-2")
            .ConfigureAwait(false);
        Assert.Equal(1, secondLease.Attempt);
        Assert.Equal(2, secondLease.LeaseGeneration);
        _ = await ExecuteReservationProviderLeaseAsync(
            worker,
            tenantId,
            payload,
            secondLease,
            expectFailure: false).ConfigureAwait(false);
        TaskRunDetails succeeded =
            await GetReservationProviderRunAsync(worker, runId)
                .ConfigureAwait(false);
        Assert.Equal(TaskRunStatus.Succeeded, succeeded.Summary.Status);
        ReservationProviderFenceProof recovered =
            await ReadReservationProviderFenceProofAsync(
                worker,
                tenantId,
                runId).ConfigureAwait(false);
        Assert.Equal(split.OwnerAttempt, recovered.OwnerAttempt);
        Assert.Equal(split.OwnerState, recovered.OwnerState);
        Assert.Equal(
            split.OwnerScannedCount,
            recovered.OwnerScannedCount);
        Assert.Equal(
            split.OwnerAffectedCount,
            recovered.OwnerAffectedCount);
        Assert.Equal(
            split.OwnerRemainingCount,
            recovered.OwnerRemainingCount);
        Assert.Equal(
            split.OwnerCompletedAtUtc,
            recovered.OwnerCompletedAtUtc);
        Assert.Equal(split.OwnerVersion, recovered.OwnerVersion);
        Assert.Equal(
            split.CheckpointLastExecutionId,
            recovered.CheckpointLastExecutionId);
        Assert.Equal(
            split.CheckpointAfterProjectionOrdinal,
            recovered.CheckpointAfterProjectionOrdinal);
        Assert.Equal(
            split.CheckpointVersion,
            recovered.CheckpointVersion);
        Assert.Equal(2, recovered.CentralAttempt);
        Assert.Equal(
            RetentionExecutionState.Completed,
            recovered.CentralState);
        Assert.True(
            recovered.OwnerCompletedAtUtc <
                recovered.CentralStartedAtUtc);
        Assert.Equal(
            recovered.CentralStartedAtUtc,
            recovered.CentralCompletedAtUtc);
        await AssertReservationProviderSingleMutationAsync(
            worker,
            tenantId,
            reservationId,
            expectedReceiptCount: 1).ConfigureAwait(false);
    }

    private static async Task AssertReservationProviderTaskRetryAsync(
        IHost worker,
        string tenantId,
        Guid validReservationId,
        Guid overflowPropertyId,
        Guid overflowReservationId,
        long overflowReservationVersion,
        long overflowDetailsRevision)
    {
        await ApplyReservationProviderRetentionTenantAsync(
            worker,
            tenantId).ConfigureAwait(false);
        Guid runId = Guid.NewGuid();
        var payload = new ExecuteRetentionSchedulePayload(
            ReservationRetentionOwner,
            ReservationOperationalDataClass,
            ExecutionPolicyVersion: 1,
            RetentionTargetScopeKind.Tenant);
        DateTimeOffset createdAtUtc = DateTimeOffset.UtcNow;
        using (IServiceScope enqueue = worker.Services.CreateScope())
        {
            TaskRunEnqueueResult enqueued = await enqueue.ServiceProvider
                .GetRequiredService<ITaskRunStore>()
                .EnqueueAsync(
                    new TaskRunRequest(
                        runId,
                        RetentionModuleMetadata.Name,
                        ExecuteRetentionSchedulePayload.TaskName,
                        JsonSerializer.Serialize(payload),
                        createdAtUtc,
                        createdAtUtc,
                        RetentionModuleMetadata.WorkerGroup,
                        tenantId,
                        requestedBy:
                            "reservation-provider-integration-test",
                        maxAttempts: 1,
                        payloadVersion:
                            ExecuteRetentionSchedulePayload.PayloadVersion,
                        deduplicationKey:
                            $"reservation-provider:{runId:N}"),
                    CancellationToken.None).ConfigureAwait(false);
            Assert.True(enqueued.Created);
        }

        TaskRunLease firstLease = await ClaimReservationProviderRunAsync(
            worker,
            createdAtUtc.AddSeconds(1),
            "reservation-provider-worker-1").ConfigureAwait(false);
        Assert.Equal(runId, firstLease.RunId);
        Assert.Equal(1, firstLease.Attempt);
        Assert.Equal(1, firstLease.LeaseGeneration);
        InvalidOperationException ownerFailure =
            Assert.IsType<InvalidOperationException>(
                await ExecuteReservationProviderLeaseAsync(
                    worker,
                    tenantId,
                    payload,
                    firstLease,
                    expectFailure: true).ConfigureAwait(false));
        Assert.Equal(
            "Retention.OwnerReportedFailure",
            ownerFailure.Message);
        TaskRunDetails failedRun =
            await GetReservationProviderRunAsync(worker, runId)
                .ConfigureAwait(false);
        Assert.Equal(TaskRunStatus.Failed, failedRun.Summary.Status);
        Assert.Equal(1, failedRun.Summary.Attempts);
        Assert.Equal(1, failedRun.Summary.MaxAttempts);
        await AssertReservationProviderMutationCountsAsync(
            worker,
            tenantId,
            validReservationId,
            overflowReservationId,
            expectedReceiptCount: 1).ConfigureAwait(false);
        await AssertReservationProviderExecutionAsync(
            worker,
            tenantId,
            runId,
            expectedAttempt: 1,
            ReservationRetentionExecutionState.Failed,
            expectedScannedCount: 2,
            expectedAffectedCount: 1,
            expectedRemainingCount: 1).ConfigureAwait(false);

        await DeleteSyntheticReservationAcknowledgementsAsync(
            worker,
            tenantId,
            overflowPropertyId).ConfigureAwait(false);
        DateTimeOffset retryAtUtc = DateTimeOffset.UtcNow;
        using (IServiceScope retry = worker.Services.CreateScope())
        {
            TaskRunMutationOutcome retried = await retry.ServiceProvider
                .GetRequiredService<ITaskRunStore>()
                .RetryAsync(
                    runId,
                    "reservation-provider-operator",
                    retryAtUtc,
                    CancellationToken.None).ConfigureAwait(false);
            Assert.Equal(TaskRunMutationOutcome.Applied, retried);
        }

        TaskRunLease secondLease = await ClaimReservationProviderRunAsync(
            worker,
            retryAtUtc.AddSeconds(1),
            "reservation-provider-worker-2").ConfigureAwait(false);
        Assert.Equal(runId, secondLease.RunId);
        Assert.Equal(1, secondLease.Attempt);
        Assert.Equal(2, secondLease.LeaseGeneration);
        _ = await ExecuteReservationProviderLeaseAsync(
            worker,
            tenantId,
            payload,
            secondLease,
            expectFailure: false).ConfigureAwait(false);
        TaskRunDetails succeededRun =
            await GetReservationProviderRunAsync(worker, runId)
                .ConfigureAwait(false);
        Assert.Equal(TaskRunStatus.Succeeded, succeededRun.Summary.Status);
        Assert.Equal(1, succeededRun.Summary.Attempts);
        Assert.Equal(1, succeededRun.Summary.MaxAttempts);
        await AssertReservationProviderMutationCountsAsync(
            worker,
            tenantId,
            validReservationId,
            overflowReservationId,
            expectedReceiptCount: 2).ConfigureAwait(false);

        ReservationProviderFenceProof before =
            await ReadReservationProviderFenceProofAsync(
                worker,
                tenantId,
                runId).ConfigureAwait(false);
        Assert.Equal(2, before.OwnerAttempt);
        Assert.Equal(
            ReservationRetentionExecutionState.Completed,
            before.OwnerState);
        Assert.Equal(2, before.OwnerScannedCount);
        Assert.Equal(2, before.OwnerAffectedCount);
        Assert.Equal(0, before.OwnerRemainingCount);
        Assert.Equal(2, before.CentralAttempt);
        Assert.Equal(RetentionExecutionState.Completed, before.CentralState);
        Assert.Equal(runId, before.CheckpointLastExecutionId);
        Assert.Equal(0, before.CheckpointAfterProjectionOrdinal);

        string staleApplyCode = await DispatchReservationCommandAsync(
            worker,
            tenantId,
            "BunkFy.Modules.Reservations.Application.Commands." +
            "ApplyReservationRetentionCommand",
            [
                runId,
                1,
                overflowPropertyId,
                overflowReservationId,
                overflowReservationVersion,
                overflowDetailsRevision
            ]).ConfigureAwait(false);
        Assert.Equal(
            "Reservations.RetentionExecutionNotFound",
            staleApplyCode);
        string staleCompleteCode = await DispatchReservationCommandAsync(
            worker,
            tenantId,
            "BunkFy.Modules.Reservations.Application.Commands." +
            "CompleteReservationRetentionExecutionCommand",
            [
                runId,
                1,
                ReservationRetentionExecutionState.Completed,
                before.OwnerScannedCount,
                before.OwnerRemainingCount,
                ReservationRetentionCompletedOutcome,
                before.OwnerCompletedAtUtc,
                null,
                0L,
                0L
            ]).ConfigureAwait(false);
        Assert.Equal(
            "Reservations.RetentionExecutionResultInvalid",
            staleCompleteCode);
        ReservationProviderFenceProof after =
            await ReadReservationProviderFenceProofAsync(
                worker,
                tenantId,
                runId).ConfigureAwait(false);
        Assert.Equal(before, after);
        await AssertReservationProviderMutationCountsAsync(
            worker,
            tenantId,
            validReservationId,
            overflowReservationId,
            expectedReceiptCount: 2).ConfigureAwait(false);
    }

    private static async Task
        ApplyReservationProviderRetentionTenantAsync(
            IHost worker,
            string tenantId)
    {
        using IServiceScope scope = CreateReservationProviderScope(
            worker,
            tenantId);
        IIntegrationEventSubscriptionRegistry subscriptions =
            scope.ServiceProvider.GetRequiredService<
                IIntegrationEventSubscriptionRegistry>();
        IntegrationEventSubscription subscription = subscriptions
            .Subscriptions.Single(item =>
                item.ConsumerModule == RetentionModuleMetadata.Name &&
                item.EventType ==
                    typeof(OrganizationChangedIntegrationEvent));
        var handler =
            (IIntegrationEventHandler<OrganizationChangedIntegrationEvent>)
            scope.ServiceProvider.GetRequiredService(
                subscription.HandlerType);
        RetentionDbContext retention = scope.ServiceProvider
            .GetRequiredService<RetentionDbContext>();
        await using var transaction = await retention.Database
            .BeginTransactionAsync().ConfigureAwait(false);
        await handler.HandleAsync(
            new OrganizationChangedIntegrationEvent(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                tenantId,
                Guid.NewGuid(),
                OrganizationChange.Created,
                OrganizationStatus.Active,
                organizationVersion: 1),
            CancellationToken.None).ConfigureAwait(false);
        await retention.SaveChangesAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static async Task<TaskRunLease>
        ClaimReservationProviderRunAsync(
            IHost worker,
            DateTimeOffset claimedAtUtc,
            string workerId)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        IReadOnlyList<TaskRunLease> leases = await scope.ServiceProvider
            .GetRequiredService<ITaskRunStore>()
            .ClaimReadyAsync(
                new TaskWorkerClaim(
                    RetentionModuleMetadata.WorkerGroup,
                    workerId,
                    "reservation-provider-node",
                    claimedAtUtc,
                    maxRuns: 1,
                    leaseDuration: TimeSpan.FromMinutes(1)),
                CancellationToken.None).ConfigureAwait(false);
        return Assert.Single(leases);
    }

    private static async Task<InvalidOperationException?>
        ExecuteReservationProviderLeaseAsync(
            IHost worker,
            string tenantId,
            ExecuteRetentionSchedulePayload payload,
            TaskRunLease lease,
            bool expectFailure)
    {
        TaskExecutionContext context = lease.CreateExecutionContext();
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        using (IServiceScope started = worker.Services.CreateScope())
        {
            Assert.Equal(
                TaskRunMutationOutcome.Applied,
                await started.ServiceProvider
                    .GetRequiredService<ITaskRunStore>()
                    .MarkStartedAsync(
                        context,
                        startedAtUtc,
                        CancellationToken.None).ConfigureAwait(false));
        }

        InvalidOperationException? failure = null;
        using (IServiceScope execution = CreateReservationProviderScope(
            worker,
            tenantId))
        {
            if (expectFailure)
            {
                failure = await Assert.ThrowsAsync<
                    InvalidOperationException>(() =>
                    InvokeReservationProviderTaskHandlerAsync(
                        execution.ServiceProvider,
                        payload,
                        context,
                        CancellationToken.None));
            }
            else
            {
                await InvokeReservationProviderTaskHandlerAsync(
                    execution.ServiceProvider,
                    payload,
                    context,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        using IServiceScope completed = worker.Services.CreateScope();
        ITaskRunStore store = completed.ServiceProvider
            .GetRequiredService<ITaskRunStore>();
        DateTimeOffset completedAtUtc = DateTimeOffset.UtcNow;
        TaskRunMutationOutcome outcome = expectFailure
            ? await store.MarkFailedAsync(
                context,
                failure!.Message,
                completedAtUtc,
                retryAtUtc: null,
                CancellationToken.None).ConfigureAwait(false)
            : await store.MarkSucceededAsync(
                context,
                completedAtUtc,
                CancellationToken.None).ConfigureAwait(false);
        Assert.Equal(TaskRunMutationOutcome.Applied, outcome);
        return failure;
    }

    private static Task InvokeReservationProviderTaskHandlerAsync(
        IServiceProvider services,
        ExecuteRetentionSchedulePayload payload,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        TaskHandlerRegistration registration = Assert.IsType<
            TaskHandlerRegistration>(services
                .GetRequiredService<ITaskHandlerRegistry>()
                .Find(
                    RetentionModuleMetadata.Name,
                    ExecuteRetentionSchedulePayload.TaskName,
                    ExecuteRetentionSchedulePayload.PayloadVersion));
        object handler = services.GetRequiredService(
            registration.HandlerType);
        MethodInfo handle = typeof(ITaskHandler<
                ExecuteRetentionSchedulePayload>)
            .GetMethod(nameof(ITaskHandler<>.HandleAsync))!;
        return Assert.IsType<Task>(
            handle.Invoke(
                handler,
                [payload, context, cancellationToken]),
            exactMatch: false);
    }

    private static async Task<TaskRunDetails>
        GetReservationProviderRunAsync(
            IHost worker,
            Guid runId)
    {
        using IServiceScope scope = worker.Services.CreateScope();
        return Assert.IsType<TaskRunDetails>(
            await scope.ServiceProvider
                .GetRequiredService<ITaskRunStore>()
                .GetAsync(runId, CancellationToken.None)
                .ConfigureAwait(false));
    }

    private static async Task
        AssertReservationProviderExecutionAsync(
            IHost worker,
            string tenantId,
            Guid executionId,
            int expectedAttempt,
            ReservationRetentionExecutionState expectedState,
            int expectedScannedCount,
            int expectedAffectedCount,
            int expectedRemainingCount)
    {
        using IServiceScope scope = CreateReservationProviderScope(
            worker,
            tenantId);
        ReservationRetentionExecution execution = await scope
            .ServiceProvider.GetRequiredService<ReservationsDbContext>()
            .RetentionExecutions.AsNoTracking()
            .SingleAsync(item => item.Id == executionId)
            .ConfigureAwait(false);
        Assert.Equal(expectedAttempt, execution.Attempt);
        Assert.Equal(expectedState, execution.State);
        Assert.Equal(expectedScannedCount, execution.ScannedCount);
        Assert.Equal(expectedAffectedCount, execution.AffectedCount);
        Assert.Equal(expectedRemainingCount, execution.RemainingCount);
    }

    private static async Task<ReservationProviderFenceProof>
        ReadReservationProviderFenceProofAsync(
            IHost worker,
            string tenantId,
            Guid executionId)
    {
        using IServiceScope scope = CreateReservationProviderScope(
            worker,
            tenantId);
        ReservationsDbContext reservations = scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>();
        ReservationRetentionExecution owner = await reservations
            .RetentionExecutions.AsNoTracking()
            .SingleAsync(item => item.Id == executionId)
            .ConfigureAwait(false);
        ReservationRetentionSweepCheckpoint checkpoint =
            await reservations.RetentionSweepCheckpoints
                .AsNoTracking()
                .SingleAsync().ConfigureAwait(false);
        RetentionExecution central = await scope.ServiceProvider
            .GetRequiredService<RetentionDbContext>()
            .Executions.AsNoTracking()
            .SingleAsync(item => item.Id == executionId)
            .ConfigureAwait(false);
        return new(
            owner.Attempt,
            owner.State,
            owner.ScannedCount!.Value,
            owner.AffectedCount,
            owner.RemainingCount!.Value,
            owner.CompletedAtUtc!.Value,
            owner.Version,
            central.Attempt,
            central.State,
            central.StartedAtUtc,
            central.CompletedAtUtc,
            central.Version,
            checkpoint.LastExecutionId,
            checkpoint.AfterProjectionOrdinal,
            checkpoint.Version);
    }

    private static async Task<string> DispatchReservationCommandAsync(
        IHost worker,
        string tenantId,
        string commandTypeName,
        object?[] arguments)
    {
        using IServiceScope scope = CreateReservationProviderScope(
            worker,
            tenantId);
        IRetentionExecutionContributor contributor = scope.ServiceProvider
            .GetServices<IRetentionExecutionContributor>()
            .Single(item =>
                item.Schedule.OwnerKey == ReservationRetentionOwner &&
                item.Schedule.DataClassKey ==
                    ReservationOperationalDataClass);
        Type commandType = Assert.IsType<Type>(
            contributor.GetType().Assembly.GetType(
                commandTypeName,
                throwOnError: true),
            exactMatch: false);
        object command = Activator.CreateInstance(
            commandType,
            BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic,
            binder: null,
            args: arguments,
            culture: null)!;
        Type commandInterface = commandType.GetInterfaces().Single(
            type =>
                type.IsGenericType &&
                type.GetGenericTypeDefinition() == typeof(ICommand<>));
        Type responseType = commandInterface.GenericTypeArguments[0];
        MethodInfo send = typeof(IRequestDispatcher)
            .GetMethod(nameof(IRequestDispatcher.SendAsync))!
            .MakeGenericMethod(responseType);
        Task dispatched = Assert.IsType<Task>(
            send.Invoke(
                scope.ServiceProvider
                    .GetRequiredService<IRequestDispatcher>(),
                [command, CancellationToken.None]),
            exactMatch: false);
        await dispatched.ConfigureAwait(false);
        object result = dispatched.GetType().GetProperty("Result")!
            .GetValue(dispatched)!;
        Assert.True(Assert.IsType<bool>(result.GetType()
            .GetProperty("IsFailure")!.GetValue(result)));
        object error = result.GetType().GetProperty("Error")!
            .GetValue(result)!;
        return Assert.IsType<string>(error.GetType()
            .GetProperty("Code")!.GetValue(error));
    }

    private static void AssertReservationPropertyPolicyQueryShape(
        string? commandText)
    {
        Assert.NotNull(commandText);
        Assert.Contains(
            "property_projection",
            commandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "property_policy_acknowledgements",
            commandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "LATERAL",
            commandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "LIMIT 65",
            commandText,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "ROW_NUMBER",
            commandText,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Guid>
        SeedReservationProviderCandidateAsync(
            IHost worker,
            string tenantId,
            Guid propertyId)
    {
        await ApplyReservationProviderPropertyAsync(
            worker,
            tenantId,
            propertyId).ConfigureAwait(false);
        using IServiceScope scope = CreateReservationProviderScope(
            worker,
            tenantId);
        return await SeedReservationAsync(
            scope.ServiceProvider,
            tenantId,
            propertyId,
            DateTimeOffset.UtcNow).ConfigureAwait(false);
    }

    private static async Task ApplyReservationProviderPropertyAsync(
        IHost worker,
        string tenantId,
        Guid propertyId)
    {
        using IServiceScope scope = CreateReservationProviderScope(
            worker,
            tenantId);
        IIntegrationEventSubscriptionRegistry subscriptions =
            scope.ServiceProvider.GetRequiredService<
                IIntegrationEventSubscriptionRegistry>();
        await ApplyReservationPropertyCreatedAsync(
            scope.ServiceProvider,
            subscriptions,
            new PropertyCreatedIntegrationEvent(
                Guid.NewGuid(),
                tenantId,
                DateTimeOffset.UtcNow,
                propertyId,
                $"Reservation Provider {propertyId:N}",
                $"reservation-provider-{propertyId:N}",
                "UTC",
                PropertyStatus.Active,
                propertyVersion: 1)).ConfigureAwait(false);
        await CountryPolicyIntegrationTestData.ApplyActivationAsync(
            scope.ServiceProvider,
            ReservationsModuleMetadata.Name,
            tenantId,
            propertyId,
            propertyVersion: 2).ConfigureAwait(false);
        await scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>()
            .SaveChangesAsync().ConfigureAwait(false);
    }

    private static RetentionContributionRequest
        CreateReservationProviderRequest(
            IHost worker,
            string tenantId,
            Guid executionId,
            int attempt)
    {
        using IServiceScope scope = CreateReservationProviderScope(
            worker,
            tenantId);
        IRetentionExecutionContributor contributor = scope.ServiceProvider
            .GetServices<IRetentionExecutionContributor>()
            .Single(item =>
                item.Schedule.OwnerKey == ReservationRetentionOwner &&
                item.Schedule.DataClassKey ==
                    ReservationOperationalDataClass);
        DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
        return new(
            RetentionExecutionContract.CurrentVersion,
            executionId,
            tenantId,
            PropertyId: null,
            contributor.Schedule.OwnerKey,
            contributor.Schedule.DataClassKey,
            contributor.Schedule.ExecutionPolicyVersion,
            attempt,
            startedAtUtc,
            startedAtUtc.AddMinutes(10));
    }

    private static async Task<RetentionContributionResult>
        ExecuteReservationProviderAsync(
            IHost worker,
            string tenantId,
            RetentionContributionRequest request)
    {
        using IServiceScope scope = CreateReservationProviderScope(
            worker,
            tenantId);
        IRetentionExecutionContributor contributor = scope.ServiceProvider
            .GetServices<IRetentionExecutionContributor>()
            .Single(item =>
                item.Schedule.OwnerKey == ReservationRetentionOwner &&
                item.Schedule.DataClassKey ==
                    ReservationOperationalDataClass);
        return await contributor.ExecuteAsync(
            request,
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task AssertReservationProviderProofAsync(
        IHost worker,
        string tenantId,
        Guid reservationId,
        Guid executionId,
        int expectedAttempt,
        DateTimeOffset expectedCompletedAtUtc,
        DateTimeOffset? earliestReceiptAtUtc = null)
    {
        using IServiceScope scope = CreateReservationProviderScope(
            worker,
            tenantId);
        ReservationsDbContext reservations = scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>();
        ReservationRetentionExecution execution = await reservations
            .RetentionExecutions.AsNoTracking()
            .SingleAsync(item => item.Id == executionId)
            .ConfigureAwait(false);
        Assert.Equal(expectedAttempt, execution.Attempt);
        Assert.Equal(expectedCompletedAtUtc, execution.CompletedAtUtc);
        ReservationRetentionAnonymisationReceipt receipt =
            await reservations.RetentionAnonymisationReceipts
                .AsNoTracking()
                .SingleAsync(item =>
                    item.ReservationId == reservationId)
                .ConfigureAwait(false);
        if (earliestReceiptAtUtc.HasValue)
        {
            Assert.InRange(
                receipt.CompletedAtUtc,
                earliestReceiptAtUtc.Value,
                expectedCompletedAtUtc);
        }
        else
        {
            Assert.Equal(expectedCompletedAtUtc, receipt.CompletedAtUtc);
        }

        Assert.Equal(0, receipt.CompletedAtUtc.Ticks %
            TimeSpan.TicksPerMicrosecond);
        ReservationAnonymisationTombstone tombstone =
            await reservations.AnonymisationTombstones
                .AsNoTracking()
                .SingleAsync(item => item.Id == reservationId)
                .ConfigureAwait(false);
        Assert.True(tombstone.MatchesRetention(receipt));
    }

    private static async Task
        AssertReservationProviderHasNoMutationAsync(
            IHost worker,
            string tenantId,
            Guid reservationId)
    {
        using IServiceScope scope = CreateReservationProviderScope(
            worker,
            tenantId);
        ReservationsDbContext reservations = scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>();
        Reservation reservation = await reservations.Reservations
            .AsNoTracking()
            .SingleAsync(item => item.Id == reservationId)
            .ConfigureAwait(false);
        Assert.False(reservation.IsAnonymised);
        Assert.Empty(await reservations.RetentionAnonymisationReceipts
            .AsNoTracking()
            .Where(item => item.ReservationId == reservationId)
            .ToArrayAsync().ConfigureAwait(false));
        Assert.Empty(await reservations.AnonymisationTombstones
            .AsNoTracking()
            .Where(item => item.Id == reservationId)
            .ToArrayAsync().ConfigureAwait(false));
    }

    private static async Task
        AssertReservationProviderMutationCountsAsync(
            IHost worker,
            string tenantId,
            Guid validReservationId,
            Guid overflowReservationId,
            int expectedReceiptCount)
    {
        using IServiceScope scope = CreateReservationProviderScope(
            worker,
            tenantId);
        ReservationsDbContext reservations = scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>();
        Reservation[] candidates = await reservations.Reservations
            .AsNoTracking()
            .Where(item =>
                item.Id == validReservationId ||
                item.Id == overflowReservationId)
            .OrderBy(item => item.ProjectionOrdinal)
            .ToArrayAsync().ConfigureAwait(false);
        Assert.Equal(2, candidates.Length);
        Assert.True(candidates[0].IsAnonymised);
        Assert.Equal(
            expectedReceiptCount == 2,
            candidates[1].IsAnonymised);
        Assert.Equal(
            expectedReceiptCount,
            await reservations.RetentionAnonymisationReceipts
                .AsNoTracking()
                .CountAsync(item =>
                    item.ReservationId == validReservationId ||
                    item.ReservationId == overflowReservationId)
                .ConfigureAwait(false));
        Assert.Equal(
            expectedReceiptCount,
            await reservations.AnonymisationTombstones
                .AsNoTracking()
                .CountAsync(item =>
                    item.Id == validReservationId ||
                    item.Id == overflowReservationId)
                .ConfigureAwait(false));
    }

    private static async Task
        AssertReservationProviderSingleMutationAsync(
            IHost worker,
            string tenantId,
            Guid reservationId,
            int expectedReceiptCount)
    {
        using IServiceScope scope = CreateReservationProviderScope(
            worker,
            tenantId);
        ReservationsDbContext reservations = scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>();
        Reservation reservation = await reservations.Reservations
            .AsNoTracking()
            .SingleAsync(item => item.Id == reservationId)
            .ConfigureAwait(false);
        Assert.Equal(
            expectedReceiptCount == 1,
            reservation.IsAnonymised);
        Assert.Equal(
            expectedReceiptCount,
            await reservations.RetentionAnonymisationReceipts
                .AsNoTracking()
                .CountAsync(item =>
                    item.ReservationId == reservationId)
                .ConfigureAwait(false));
        Assert.Equal(
            expectedReceiptCount,
            await reservations.AnonymisationTombstones
                .AsNoTracking()
                .CountAsync(item => item.Id == reservationId)
                .ConfigureAwait(false));
    }

    private static async Task InsertReservationAcknowledgementsAsync(
        IHost worker,
        string tenantId,
        Guid propertyId,
        int count)
    {
        using IServiceScope scope = CreateReservationProviderScope(
            worker,
            tenantId);
        ReservationsDbContext reservations = scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>();
        await reservations.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reservations.property_policy_acknowledgements (
                "PropertyId",
                "AcknowledgementId",
                "AcknowledgementVersion")
            SELECT
                {propertyId},
                'reservation-provider-bound-' ||
                    lpad(item::text, 3, '0'),
                1
            FROM generate_series(0, {count - 1}) AS item;
            """).ConfigureAwait(false);
    }

    private static async Task
        DeleteSyntheticReservationAcknowledgementsAsync(
            IHost worker,
            string tenantId,
            Guid propertyId)
    {
        using IServiceScope scope = CreateReservationProviderScope(
            worker,
            tenantId);
        ReservationsDbContext reservations = scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>();
        int deleted =
            await reservations.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM reservations.property_policy_acknowledgements
            WHERE "PropertyId" = {propertyId}
              AND (
                  "AcknowledgementId" =
                      'reservation-provider-raced'
                  OR "AcknowledgementId" LIKE
                      'reservation-provider-bound-%');
            """).ConfigureAwait(false);
        Assert.Equal(
            PropertiesContractLimits.MaximumPolicyAcknowledgements,
            deleted);
    }

    private static object CreateReservationCandidateRepository(
        ReservationsDbContext reservations) =>
        Activator.CreateInstance(
            typeof(ReservationsDbContext).Assembly.GetType(
                "BunkFy.Modules.Reservations.Persistence.Repositories." +
                "ReservationRetentionCandidateRepository",
                throwOnError: true)!,
            reservations)!;

    private static async Task<ReservationCandidateScanProof>
        InvokeReservationCandidateScanAsync(
            object repository,
            long afterProjectionOrdinal,
            int limit,
            CancellationToken cancellationToken)
    {
        object invocation = repository.GetType()
            .GetMethod("ScanAsync")!
            .Invoke(
                repository,
                [afterProjectionOrdinal, limit, cancellationToken])!;
        Task task = Assert.IsType<Task>(
            invocation,
            exactMatch: false);
        await task.ConfigureAwait(false);
        object page = task.GetType().GetProperty("Result")!
            .GetValue(task)!;
        object[] candidates =
            ((System.Collections.IEnumerable)page.GetType()
                .GetProperty("Candidates")!
                .GetValue(page)!)
            .Cast<object>()
            .ToArray();
        bool reachedEnd = Assert.IsType<bool>(page.GetType()
            .GetProperty("ReachedEnd")!
            .GetValue(page));
        return new(candidates, reachedEnd);
    }

    private static Guid GetReservationCandidateReservationId(
        object candidate) =>
        Assert.IsType<Guid>(candidate.GetType()
            .GetProperty("ReservationId")!
            .GetValue(candidate));

    private static Guid GetReservationCandidatePropertyId(
        object candidate) =>
        Assert.IsType<Guid>(candidate.GetType()
            .GetProperty("PropertyId")!
            .GetValue(candidate));

    private static long GetReservationCandidateProjectionOrdinal(
        object candidate) =>
        Assert.IsType<long>(candidate.GetType()
            .GetProperty("ProjectionOrdinal")!
            .GetValue(candidate));

    private static long GetReservationCandidateVersion(
        object candidate) =>
        Assert.IsType<long>(candidate.GetType()
            .GetProperty("ReservationVersion")!
            .GetValue(candidate));

    private static long GetReservationCandidateDetailsRevision(
        object candidate) =>
        Assert.IsType<long>(candidate.GetType()
            .GetProperty("DetailsRevision")!
            .GetValue(candidate));

    private static int GetReservationCandidateActiveHoldCount(
        object candidate) =>
        Assert.IsType<int>(candidate.GetType()
            .GetProperty("ActiveHoldCount")!
            .GetValue(candidate));

    private static int? GetReservationCandidateRestrictionVersion(
        object candidate) =>
        (int?)candidate.GetType()
            .GetProperty("ProcessingRestrictionContractVersion")!
            .GetValue(candidate);

    private static object? GetReservationCandidateProperty(
        object candidate) =>
        candidate.GetType().GetProperty("Property")!
            .GetValue(candidate);

    private static object? GetReservationCandidateGovernancePolicy(
        object property) =>
        property.GetType().GetProperty("GovernancePolicy")!
            .GetValue(property);

    private static long GetReservationCandidatePolicySourceVersion(
        object candidate)
    {
        object property = Assert.IsType<object>(
            GetReservationCandidateProperty(candidate),
            exactMatch: false);
        return Assert.IsType<long>(property.GetType()
            .GetProperty("PolicySourceVersion")!
            .GetValue(property));
    }

    private static PropertyGovernancePolicyBinding
        GetReservationCandidatePolicy(
            object candidate)
    {
        object property = Assert.IsType<object>(
            GetReservationCandidateProperty(candidate),
            exactMatch: false);
        return Assert.IsType<PropertyGovernancePolicyBinding>(
            GetReservationCandidateGovernancePolicy(property));
    }

    private static int GetReservationCandidateAcknowledgementCount(
        object candidate) =>
        GetReservationCandidatePolicy(candidate)
            .Acknowledgements.Count;

    private static IServiceScope CreateReservationProviderScope(
        IHost worker,
        string tenantId)
    {
        IServiceScope scope = worker.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(tenantId);
        return scope;
    }

    private static DateTimeOffset
        WithReservationProviderSubMicrosecondTicks(
            DateTimeOffset value) =>
        value.AddTicks(
            -(value.Ticks % TimeSpan.TicksPerMicrosecond) + 7);

    private sealed record ReservationCandidateScanProof(
        object[] Candidates,
        bool ReachedEnd);

    private sealed record ReservationProviderFenceProof(
        int OwnerAttempt,
        ReservationRetentionExecutionState OwnerState,
        int OwnerScannedCount,
        int OwnerAffectedCount,
        int OwnerRemainingCount,
        DateTimeOffset OwnerCompletedAtUtc,
        long OwnerVersion,
        int CentralAttempt,
        RetentionExecutionState CentralState,
        DateTimeOffset CentralStartedAtUtc,
        DateTimeOffset? CentralCompletedAtUtc,
        long CentralVersion,
        Guid? CheckpointLastExecutionId,
        long CheckpointAfterProjectionOrdinal,
        long CheckpointVersion);

    private sealed class ReservationProviderScopeContext(string scopeId)
        : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }

    private sealed class ReservationProviderMutableClock(
        DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class ReservationAcknowledgementGrowthInterceptor(
        string connectionString,
        Guid propertyId) : DbCommandInterceptor
    {
        private int inserted;
        private int matchedCommandCount;
        private string? matchedCommandText;

        public bool Inserted => Volatile.Read(ref this.inserted) == 1;
        public int MatchedCommandCount =>
            Volatile.Read(ref this.matchedCommandCount);
        public string? MatchedCommandText =>
            Volatile.Read(ref this.matchedCommandText);

        public override async ValueTask<DbDataReader>
            ReaderExecutedAsync(
                DbCommand command,
                CommandExecutedEventData eventData,
                DbDataReader result,
                CancellationToken cancellationToken = default)
        {
            if (!IsPropertyPolicySnapshotQuery(command.CommandText))
            {
                return result;
            }

            _ = Interlocked.Increment(ref this.matchedCommandCount);
            Volatile.Write(
                ref this.matchedCommandText,
                command.CommandText);
            if (
                Interlocked.CompareExchange(
                    ref this.inserted,
                    1,
                    0) != 0)
            {
                return result;
            }

            await using var connection = new NpgsqlConnection(
                connectionString);
            await connection.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NpgsqlCommand insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO reservations.property_policy_acknowledgements (
                    "PropertyId",
                    "AcknowledgementId",
                    "AcknowledgementVersion")
                VALUES (
                    @property_id,
                    'reservation-provider-raced',
                    1);
                """;
            insert.Parameters.AddWithValue("property_id", propertyId);
            await insert.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
    }

    private sealed class
        ReservationPolicyGenerationReplacementInterceptor(
            string connectionString,
            Guid propertyId) : DbCommandInterceptor
    {
        private int replaced;
        private int matchedCommandCount;
        private string? matchedCommandText;

        public bool Replaced => Volatile.Read(ref this.replaced) == 1;
        public int MatchedCommandCount =>
            Volatile.Read(ref this.matchedCommandCount);
        public string? MatchedCommandText =>
            Volatile.Read(ref this.matchedCommandText);

        public override async ValueTask<DbDataReader>
            ReaderExecutedAsync(
                DbCommand command,
                CommandExecutedEventData eventData,
                DbDataReader result,
                CancellationToken cancellationToken = default)
        {
            if (!IsPropertyPolicySnapshotQuery(command.CommandText))
            {
                return result;
            }

            _ = Interlocked.Increment(ref this.matchedCommandCount);
            Volatile.Write(
                ref this.matchedCommandText,
                command.CommandText);
            if (Interlocked.CompareExchange(
                    ref this.replaced,
                    1,
                    0) != 0)
            {
                return result;
            }

            await using var connection = new NpgsqlConnection(
                connectionString);
            await connection.OpenAsync(cancellationToken)
                .ConfigureAwait(false);
            await using NpgsqlTransaction transaction =
                await connection.BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);
            await using NpgsqlCommand replacement =
                connection.CreateCommand();
            replacement.Transaction = transaction;
            replacement.CommandText = """
                UPDATE reservations.property_projection
                SET
                    "PolicySourceVersion" =
                        "PolicySourceVersion" + 1,
                    "JurisdictionPolicyId" =
                        'reservation-provider-replacement',
                    "JurisdictionPolicyVersion" = 2
                WHERE "Id" = @property_id;

                DELETE FROM
                    reservations.property_policy_acknowledgements
                WHERE "PropertyId" = @property_id;

                INSERT INTO
                    reservations.property_policy_acknowledgements (
                        "PropertyId",
                        "AcknowledgementId",
                        "AcknowledgementVersion")
                VALUES (
                    @property_id,
                    'reservation-provider-replacement-ack',
                    1);
                """;
            replacement.Parameters.AddWithValue(
                "property_id",
                propertyId);
            int affected = await replacement.ExecuteNonQueryAsync(
                cancellationToken).ConfigureAwait(false);
            if (affected != 3)
            {
                throw new InvalidOperationException(
                    $"Expected three policy generation rows, got {affected}.");
            }

            await transaction.CommitAsync(cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
    }

    private static bool IsPropertyPolicySnapshotQuery(
        string commandText) =>
        commandText.Contains(
            "property_projection",
            StringComparison.OrdinalIgnoreCase) &&
        commandText.Contains(
            "property_policy_acknowledgements",
            StringComparison.OrdinalIgnoreCase);
}
