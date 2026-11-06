namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Mapping;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class TenantTerminationExportExecutionHandlersTests
{
    private const string TenantId = "tenant-a";
    private const string OwnerKey = "workspaces";
    private const string Executor = "system:tenant-termination";
    private static readonly string Digest = new('a', 64);
    private static readonly string FrozenDigest = new('b', 64);
    private static readonly string PlaintextDigest = new('c', 64);
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Protected_fragment_and_artifact_complete_with_exact_proof()
    {
        StubContributor contributor = new();
        TenantTerminationProcess process = ExportingProcess(contributor);
        TenantTerminationPhasePlanner planner = new([contributor]);
        TenantTerminationOwnerWorkItem workItem = Assert.Single(
            planner.PrepareWorkItems(process, Now.AddMinutes(3)).Value);
        Guid ownerRunId = TenantTerminationExecutionIdentity.CreateTaskRunId(
            workItem.Id,
            dispatchSequence: 1);
        Assert.True(workItem.BeginProcessing(
            ownerRunId,
            taskAttempt: 1,
            workItem.Version,
            Now.AddMinutes(4)).IsSuccess);

        StubTerminationRepository repository = new(process, [workItem]);
        StubFragmentRepository fragments = new();
        RecordingTenantTerminationExportRetentionScheduler retention = new();
        BeginTenantTerminationExportFragmentGenerationCommandHandler beginFragment =
            new(
                repository,
                DataRightsMutationTestSupport.TenantTermination(repository),
                fragments,
                new FixedArtifactPolicy(TimeSpan.FromHours(24)),
                planner,
                retention,
                new FixedClock(Now.AddMinutes(4).AddSeconds(10)));
        BeginTenantTerminationExportFragmentGenerationCommand beginCommand = new(
            process.Id,
            workItem.Id,
            process.OperationRevision,
            workItem.OwnerKey,
            ownerRunId,
            TaskAttempt: 1,
            workItem.Version);

        Result<TenantTerminationExportFragmentGenerationStart> begun =
            await beginFragment.HandleAsync(
                beginCommand,
                CancellationToken.None);

        Assert.True(begun.IsSuccess);
        TenantTerminationExportFragment fragment = Assert.Single(
            fragments.Items);
        Assert.Equal(
            TenantTerminationExportFragmentState.Generating,
            fragment.State);
        Assert.Equal(
            process.FrozenRevisionSha256,
            begun.Value.Request.AssemblyRequest.FrozenRevisionSha256);
        Assert.Equal(
            process.WorkspaceFenceRevision,
            begun.Value.Request.AssemblyRequest.WorkspaceFenceRevision);

        TenantTerminationContributionRequest ownerRequest = new(
            workItem.OwnerContractVersion,
            process.ScopeId,
            process.Id,
            process.CaseId,
            process.ApprovalRevision,
            process.OperationRevision,
            process.TerminationEpoch,
            TenantTerminationContributionPhase.Export,
            workItem.Id,
            workItem.IdempotencyKey,
            process.PolicyEvidenceSha256,
            Executor,
            begun.Value.Request.AssemblyRequest.DeadlineUtc);
        TenantTerminationReplayDispatch dispatch =
            TenantTerminationReplayDispatch.Create(
                ownerRequest,
                workItem.OwnerKey,
                workItem.CatalogVersion,
                workItem.CatalogSha256,
                TenantTerminationExecutionBoundary.TenantScopedTask,
                ownerRunId,
                taskAttempt: 1,
                Now.AddMinutes(4).AddSeconds(20));
        TenantTerminationContributionResult contribution = new(
            TenantTerminationContributionStatus.Completed,
            "workspaces.tenant-export.completed",
            AffectedCount: 2,
            RetainedMinimumCount: 0,
            RemainingActiveCount: 0,
            HoldReviewAtUtc: null,
            SelectedProofRevision: 11,
            ResultingProofRevision: 11,
            workItem.CatalogVersion,
            workItem.CatalogSha256,
            Now.AddMinutes(5));
        TenantTerminationReplayResult replayResult =
            TenantTerminationReplayResult.Create(
                dispatch,
                contribution,
                Now.AddMinutes(5).AddSeconds(1));
        StubTenantTerminationReplayStore replayStore = new();
        replayStore.Add(new(dispatch, replayResult));
        RecordingTenantTerminationCoordinationSignal signal = new();
        RecordTenantTerminationOwnerResultCommandHandler recorder = new(
            repository,
            DataRightsMutationTestSupport.TenantTermination(repository),
            replayStore,
            planner,
            signal);
        CompleteTenantTerminationExportFragmentGenerationCommandHandler
            completeFragment = new(
                DataRightsMutationTestSupport.TenantTermination(repository),
                fragments,
                replayStore,
                new RecordResultDispatcher(recorder));
        TenantTerminationProtectedExportFragment protectedFragment = new(
            new(
                FrozenDigest,
                new(
                    OwnerKey,
                    RecordCount: 2,
                    SelectedProofRevision: 11,
                    ResultingProofRevision: 11,
                    contribution.ResultCode,
                    contribution.RecordedAtUtc)),
            "data-rights/tenant-exports/workspaces.bftxf",
            EncryptedByteLength: 2048,
            PlaintextDigest,
            EncryptionKeyVersion: 1,
            FormatVersion: 1,
            AvailableAtUtc: Now.AddMinutes(6),
            ExpiresAtUtc: fragment.ExpiresAtUtc);

        Result<TenantTerminationOwnerResultRecorded> fragmentCompleted =
            await completeFragment.HandleAsync(
                new(
                    process.Id,
                    workItem.Id,
                    process.OperationRevision,
                    workItem.OwnerKey,
                    ownerRunId,
                    TaskAttempt: 1,
                    workItem.Version,
                    begun.Value.FragmentVersion,
                    protectedFragment),
                CancellationToken.None);

        Assert.True(fragmentCompleted.IsSuccess);
        Assert.Equal(TenantTerminationExportFragmentState.Available, fragment.State);
        Assert.Equal(TenantTerminationOwnerWorkState.Completed, workItem.State);

        StubArtifactRepository artifacts = new();
        Guid artifactRunId = TenantTerminationExecutionIdentity
            .CreateExportArtifactTaskRunId(
                process.Id,
                process.OperationRevision);
        BeginTenantTerminationExportArtifactGenerationCommandHandler beginArtifact =
            new(
                repository,
                DataRightsMutationTestSupport.TenantTermination(repository),
                fragments,
                artifacts,
                retention,
                new FixedClock(Now.AddMinutes(7)));
        Result<TenantTerminationExportArtifactGenerationStart> artifactBegun =
            await beginArtifact.HandleAsync(
                new(
                    process.Id,
                    process.OperationRevision,
                    artifactRunId,
                    TaskAttempt: 1),
                CancellationToken.None);

        Assert.True(artifactBegun.IsSuccess);
        Assert.True(artifactBegun.Value.DispatchRequired);
        Assert.Equal(
            TenantTerminationExecutionIdentity.CreateExportArtifactId(
                process.Id,
                process.OperationRevision),
            artifactBegun.Value.Artifact.Id);
        TenantTerminationExportArtifact artifact = artifactBegun.Value.Artifact;
        CompleteTenantTerminationExportArtifactGenerationCommandHandler
            completeArtifact = new(
                DataRightsMutationTestSupport.TenantTermination(repository),
                artifacts);
        TenantTerminationProtectedExportArtifact protectedArtifact = new(
            FragmentCount: 1,
            RecordCount: 2,
            artifact.FragmentSetSha256,
            "data-rights/tenant-exports/final.bftxa",
            EncryptedByteLength: 4096,
            PlaintextDigest,
            EncryptionKeyVersion: 1,
            FormatVersion: 1,
            AvailableAtUtc: Now.AddMinutes(8),
            ExpiresAtUtc: artifact.ExpiresAtUtc);

        Result<TenantTerminationExportArtifactGenerationCompleted>
            artifactCompleted = await completeArtifact.HandleAsync(
                new(
                    process.Id,
                    process.OperationRevision,
                    artifact.Id,
                    artifactRunId,
                    TaskAttempt: 1,
                    artifactBegun.Value.ProcessVersion,
                    artifactBegun.Value.ArtifactVersion,
                    protectedArtifact),
                CancellationToken.None);

        Assert.True(artifactCompleted.IsSuccess);
        Assert.Equal(TenantTerminationExportArtifactState.Available, artifact.State);
        Assert.False(process.HasCurrentExportConfirmation());
        Assert.Null(process.ExportArtifactId);
        Assert.Single(signal.Captures);
        Assert.Single(retention.Fragments);
        Assert.Single(retention.Artifacts);

        ConfirmTenantTerminationExportCommandHandler confirmer = new(
            DataRightsMutationTestSupport.TenantTermination(repository),
            artifacts,
            signal,
            new FixedClock(Now.AddMinutes(9)));
        Result<TenantTerminationProcessDto> confirmed =
            await confirmer.HandleAsync(
                new(
                    process.CaseId,
                    process.Id,
                    artifact.Id,
                    artifact.ExportOperationRevision,
                    process.Version,
                    artifact.Version,
                    artifact.FrozenRevisionSha256,
                    artifact.FragmentSetSha256,
                    "operator:export-reviewer"),
                CancellationToken.None);

        Assert.True(confirmed.IsSuccess);
        Assert.True(process.HasCurrentExportConfirmation());
        Assert.Equal(artifact.Id, process.ExportArtifactId);
        Assert.Equal("operator:export-reviewer", process.ExportConfirmedBy);
        Assert.Equal(2, signal.Captures.Count);

        long confirmedOperationRevision = process.OperationRevision;
        Assert.True(process.CompletePhase(
            TenantTerminationProcessPhase.Export,
            confirmedOperationRevision,
            process.Version,
            Executor,
            Now.AddMinutes(10)).IsSuccess);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Destroy,
            process.Version,
            Executor,
            Now.AddMinutes(11)).IsSuccess);
        Assert.True(TenantTerminationExportArtifactCoordinator
            .IsAvailableForDownload(
                process,
                artifact,
                Now.AddMinutes(12)));

        long confirmedArtifactVersion = artifact.Version;
        Assert.True(artifact.MarkExpired(artifact.ExpiresAtUtc).IsSuccess);
        TenantTerminationExportHandoffDto retainedReceipt =
            artifact.ToHandoffDto(process);
        Assert.True(retainedReceipt.Confirmed);
        Assert.Equal(
            confirmedArtifactVersion,
            retainedReceipt.ConfirmedArtifactVersion);
        Assert.True(retainedReceipt.ArtifactVersion > confirmedArtifactVersion);
    }

    [Fact]
    public async Task Confirmation_rejects_system_actor_and_stale_coordinates()
    {
        TenantTerminationProcess process = ExportingProcess(
            new StubContributor());
        TenantTerminationExportArtifact artifact = AvailableArtifact(
            process,
            Now.AddHours(24));
        StubArtifactRepository artifacts = new();
        await artifacts.AddAsync(artifact, CancellationToken.None);
        StubTerminationRepository repository = new(process, []);
        RecordingTenantTerminationCoordinationSignal signal = new();
        ConfirmTenantTerminationExportCommandHandler handler = new(
            DataRightsMutationTestSupport.TenantTermination(repository),
            artifacts,
            signal,
            new FixedClock(Now.AddMinutes(10)));

        Result<TenantTerminationProcessDto> systemActor =
            await handler.HandleAsync(
                new(
                    process.CaseId,
                    process.Id,
                    artifact.Id,
                    artifact.ExportOperationRevision,
                    process.Version,
                    artifact.Version,
                    artifact.FrozenRevisionSha256,
                    artifact.FragmentSetSha256,
                    Executor),
                CancellationToken.None);
        Result<TenantTerminationProcessDto> staleCase =
            await handler.HandleAsync(
                new(
                    Guid.NewGuid(),
                    process.Id,
                    artifact.Id,
                    artifact.ExportOperationRevision,
                    process.Version,
                    artifact.Version,
                    artifact.FrozenRevisionSha256,
                    artifact.FragmentSetSha256,
                    "operator:export-reviewer"),
                CancellationToken.None);
        Result<TenantTerminationProcessDto> staleDigest =
            await handler.HandleAsync(
                new(
                    process.CaseId,
                    process.Id,
                    artifact.Id,
                    artifact.ExportOperationRevision,
                    process.Version,
                    artifact.Version,
                    new string('f', 64),
                    artifact.FragmentSetSha256,
                    "operator:export-reviewer"),
                CancellationToken.None);

        Result<TenantTerminationProcessDto>[] failures =
            [systemActor, staleCase, staleDigest];
        Assert.All(
            failures,
            result =>
            {
                Assert.True(result.IsFailure);
                Assert.Equal(
                    DataRightsApplicationErrors
                        .TenantTerminationExportConfirmationInvalid,
                    result.Error);
            });
        Assert.False(process.HasCurrentExportConfirmation());
        Assert.Empty(signal.Captures);
    }

    [Fact]
    public async Task Available_export_download_is_verified_and_audited()
    {
        TenantTerminationProcess process = ExportingProcess(
            new StubContributor());
        TenantTerminationExportArtifact artifact = AvailableArtifact(
            process,
            Now.AddHours(24));
        StubArtifactRepository artifacts = new();
        await artifacts.AddAsync(artifact, CancellationToken.None);
        byte[] content = [1, 2, 3, 4];
        RecordingArtifactReader reader = new(content);
        RecordingAuditSink audit = new();
        PrepareTenantTerminationExportDownloadCommandHandler handler = new(
            new StubTerminationRepository(process, []),
            artifacts,
            reader,
            audit,
            new FixedScopeContext(TenantId),
            new FixedClock(Now.AddMinutes(10)));

        Result<DataRightsExportDownload> result = await handler.HandleAsync(
            new(
                process.CaseId,
                process.Id,
                artifact.Id,
                "operator:export-downloader"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(content.Length, result.Value.ContentLength);
        Assert.Equal("operator:export-downloader", Assert.Single(audit.Facts).ActorId);
        await result.Value.Content.DisposeAsync();
    }

    [Fact]
    public async Task Expired_export_blocks_and_can_restart_only_export()
    {
        StubContributor contributor = new();
        TenantTerminationProcess process = ExportingProcess(contributor);
        DateTimeOffset expiresAtUtc = Now.AddHours(1);
        TenantTerminationExportArtifact artifact = AvailableArtifact(
            process,
            expiresAtUtc);
        StubArtifactRepository artifacts = new();
        await artifacts.AddAsync(artifact, CancellationToken.None);
        StubTerminationRepository repository = new(process, []);
        RecordingAuditSink audit = new();
        Guid cleanupRunId = TenantTerminationExecutionIdentity
            .CreateExportArtifactCleanupTaskRunId(artifact.Id);
        BeginTenantTerminationExportArtifactDeletionCommandHandler cleanup = new(
            DataRightsMutationTestSupport.TenantTermination(repository),
            artifacts,
            audit,
            new FixedClock(expiresAtUtc));

        Result<TenantTerminationExportObjectDeletionStart> deletion =
            await cleanup.HandleAsync(
                new(
                    process.Id,
                    artifact.Id,
                    artifact.ExportOperationRevision,
                    artifact.ExpiresAtUtc,
                    cleanupRunId),
                CancellationToken.None);

        Assert.True(deletion.IsSuccess);
        Assert.Equal(
            TenantTerminationExportArtifactState.Deleting,
            artifact.State);
        Assert.Equal(TenantTerminationProcessStatus.Blocked, process.Status);
        Assert.Equal(
            TenantTerminationProcess.ExportArtifactExpiredOutcomeCode,
            process.OutcomeCode);

        RecordingTenantTerminationCoordinationSignal signal = new();
        RetryTenantTerminationCommandHandler retry = new(
            repository,
            DataRightsMutationTestSupport.TenantTermination(repository),
            artifacts,
            new StubFragmentRepository(),
            new TenantTerminationPhasePlanner([contributor]),
            signal,
            new FixedClock(expiresAtUtc.AddMinutes(1)));
        long previousOperation = process.OperationRevision;
        Result<TenantTerminationProcessDto> restarted =
            await retry.HandleAsync(
                new(
                    process.Id,
                    process.Version,
                    "operator:export-recovery"),
                CancellationToken.None);

        Assert.True(restarted.IsSuccess);
        Assert.Equal(TenantTerminationStatus.Pending, restarted.Value.Status);
        Assert.Equal(previousOperation, restarted.Value.OperationRevision);
        Assert.Single(signal.Captures);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Export,
            process.Version,
            Executor,
            expiresAtUtc.AddMinutes(2)).IsSuccess);
        Assert.Equal(previousOperation + 1, process.OperationRevision);
    }

    [Fact]
    public async Task Expired_fragment_without_artifact_blocks_and_can_restart_export()
    {
        StubContributor contributor = new();
        TenantTerminationProcess process = ExportingProcess(contributor);
        DateTimeOffset expiresAtUtc = Now.AddHours(1);
        TenantTerminationExportFragment fragment = RequestedFragment(
            process,
            contributor,
            expiresAtUtc);
        StubFragmentRepository fragments = new();
        await fragments.AddAsync(fragment, CancellationToken.None);
        StubArtifactRepository artifacts = new();
        StubTerminationRepository repository = new(process, []);
        Guid cleanupRunId = TenantTerminationExecutionIdentity
            .CreateExportFragmentCleanupTaskRunId(fragment.Id);
        BeginTenantTerminationExportFragmentDeletionCommandHandler cleanup =
            new(
                DataRightsMutationTestSupport.TenantTermination(repository),
                fragments,
                artifacts,
                new RecordingAuditSink(),
                new FixedClock(expiresAtUtc));

        Result<TenantTerminationExportObjectDeletionStart> deletion =
            await cleanup.HandleAsync(
                new(
                    process.Id,
                    fragment.Id,
                    fragment.ExportOperationRevision,
                    fragment.ExpiresAtUtc,
                    cleanupRunId),
                CancellationToken.None);

        Assert.True(deletion.IsSuccess);
        Assert.Equal(
            TenantTerminationExportFragmentState.Deleting,
            fragment.State);
        Assert.Equal(TenantTerminationProcessStatus.Blocked, process.Status);
        Assert.Equal(
            TenantTerminationProcess.ExportFragmentExpiredOutcomeCode,
            process.OutcomeCode);

        RecordingTenantTerminationCoordinationSignal signal = new();
        RetryTenantTerminationCommandHandler retry = new(
            repository,
            DataRightsMutationTestSupport.TenantTermination(repository),
            artifacts,
            fragments,
            new TenantTerminationPhasePlanner([contributor]),
            signal,
            new FixedClock(expiresAtUtc.AddMinutes(1)));

        Result<TenantTerminationProcessDto> restarted =
            await retry.HandleAsync(
                new(
                    process.Id,
                    process.Version,
                    "operator:export-recovery"),
                CancellationToken.None);

        Assert.True(restarted.IsSuccess);
        Assert.Equal(TenantTerminationStatus.Pending, restarted.Value.Status);
        Assert.Single(signal.Captures);
    }

    [Fact]
    public async Task Confirmed_export_fragment_cleanup_survives_destroy_revision_advance()
    {
        StubContributor contributor = new();
        TenantTerminationProcess process = ExportingProcess(contributor);
        long exportOperationRevision = process.OperationRevision;
        DateTimeOffset expiresAtUtc = Now.AddHours(1);
        TenantTerminationExportFragment fragment = RequestedFragment(
            process,
            contributor,
            expiresAtUtc);
        TenantTerminationExportArtifact artifact = AvailableArtifact(
            process,
            expiresAtUtc);
        Assert.True(TenantTerminationExportArtifactCoordinator.Confirm(
            process,
            artifact,
            process.Version,
            "operator:export-reviewer",
            Now.AddMinutes(7)).IsSuccess);
        Assert.True(process.CompletePhase(
            TenantTerminationProcessPhase.Export,
            exportOperationRevision,
            process.Version,
            Executor,
            Now.AddMinutes(8)).IsSuccess);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Destroy,
            process.Version,
            Executor,
            Now.AddMinutes(9)).IsSuccess);
        Assert.NotEqual(exportOperationRevision, process.OperationRevision);

        StubFragmentRepository fragments = new();
        await fragments.AddAsync(fragment, CancellationToken.None);
        StubArtifactRepository artifacts = new();
        await artifacts.AddAsync(artifact, CancellationToken.None);
        StubTerminationRepository repository = new(process, []);
        Guid cleanupRunId = TenantTerminationExecutionIdentity
            .CreateExportFragmentCleanupTaskRunId(fragment.Id);
        BeginTenantTerminationExportFragmentDeletionCommandHandler cleanup =
            new(
                DataRightsMutationTestSupport.TenantTermination(repository),
                fragments,
                artifacts,
                new RecordingAuditSink(),
                new FixedClock(expiresAtUtc));

        Result<TenantTerminationExportObjectDeletionStart> deletion =
            await cleanup.HandleAsync(
                new(
                    process.Id,
                    fragment.Id,
                    fragment.ExportOperationRevision,
                    fragment.ExpiresAtUtc,
                    cleanupRunId),
                CancellationToken.None);

        Assert.True(deletion.IsSuccess);
        Assert.Equal(
            TenantTerminationExportFragmentState.Deleting,
            fragment.State);
        Assert.Equal(TenantTerminationProcessPhase.Destroy, process.Phase);
        Assert.Equal(TenantTerminationProcessStatus.Running, process.Status);
        Assert.Null(process.OutcomeCode);
    }

    [Fact]
    public async Task Failed_fragment_without_artifact_can_restart_export()
    {
        StubContributor contributor = new();
        TenantTerminationProcess process = ExportingProcess(contributor);
        TenantTerminationExportFragment fragment = RequestedFragment(
            process,
            contributor,
            Now.AddHours(1));
        Guid runId = Guid.NewGuid();
        Assert.True(fragment.BeginGeneration(
            runId,
            attempt: 1,
            Now.AddMinutes(4)).IsSuccess);
        Assert.True(fragment.MarkFailed(
            runId,
            attempt: 1,
            "generator-failed",
            Now.AddMinutes(5)).IsSuccess);
        StubFragmentRepository fragments = new();
        await fragments.AddAsync(fragment, CancellationToken.None);
        StubTerminationRepository repository = new(process, []);
        RecordingTenantTerminationCoordinationSignal signal = new();
        RetryTenantTerminationCommandHandler retry = new(
            repository,
            DataRightsMutationTestSupport.TenantTermination(repository),
            new StubArtifactRepository(),
            fragments,
            new TenantTerminationPhasePlanner([contributor]),
            signal,
            new FixedClock(Now.AddMinutes(6)));

        Result<TenantTerminationProcessDto> restarted =
            await retry.HandleAsync(
                new(
                    process.Id,
                    process.Version,
                    "operator:export-recovery"),
                CancellationToken.None);

        Assert.True(restarted.IsSuccess);
        Assert.Equal(TenantTerminationStatus.Pending, restarted.Value.Status);
        Assert.Single(signal.Captures);
    }

    private static TenantTerminationExportArtifact AvailableArtifact(
        TenantTerminationProcess process,
        DateTimeOffset expiresAtUtc)
    {
        Guid artifactId = TenantTerminationExecutionIdentity
            .CreateExportArtifactId(process.Id, process.OperationRevision);
        TenantTerminationExportArtifact artifact =
            TenantTerminationExportArtifact.Request(
                artifactId,
                process.ScopeId,
                process.Id,
                process.CaseId,
                process.ApprovalRevision,
                process.FreezeOperationRevision!.Value,
                process.OperationRevision,
                process.TerminationEpoch,
                TenantTerminationExecutionIdentity
                    .CreateExportArtifactIdempotencyKey(
                        process.Id,
                        process.OperationRevision),
                process.FrozenRevisionSha256!,
                process.PolicyEvidenceSha256,
                expectedFragmentCount: 1,
                Digest,
                Now.AddMinutes(4),
                expiresAtUtc).Value;
        Guid runId = TenantTerminationExecutionIdentity
            .CreateExportArtifactTaskRunId(
                process.Id,
                process.OperationRevision);
        Assert.True(artifact.BeginGeneration(
            runId,
            attempt: 1,
            Now.AddMinutes(5)).IsSuccess);
        Assert.True(artifact.MarkAvailable(
            runId,
            attempt: 1,
            fragmentCount: 1,
            recordCount: 2,
            Digest,
            "data-rights/tenant-exports/final.bftxa",
            encryptedByteLength: 4096,
            PlaintextDigest,
            encryptionKeyVersion: 1,
            formatVersion: 1,
            Now.AddMinutes(6),
            expiresAtUtc).IsSuccess);
        return artifact;
    }

    private static TenantTerminationExportFragment RequestedFragment(
        TenantTerminationProcess process,
        StubContributor contributor,
        DateTimeOffset expiresAtUtc) =>
        TenantTerminationExportFragment.Request(
            Guid.NewGuid(),
            process.ScopeId,
            process.Id,
            process.CaseId,
            process.ApprovalRevision,
            process.FreezeOperationRevision!.Value,
            process.OperationRevision,
            process.TerminationEpoch,
            Guid.NewGuid(),
            contributor.Descriptor.OwnerKey,
            contributor.Descriptor.ContractVersion,
            contributor.Descriptor.CatalogVersion,
            contributor.Descriptor.CatalogSha256,
            process.FrozenRevisionSha256!,
            process.PolicyEvidenceSha256,
            Now.AddMinutes(3),
            expiresAtUtc).Value;

    private static TenantTerminationProcess ExportingProcess(
        StubContributor contributor)
    {
        TenantTerminationProcess process = TenantTerminationProcess.Prepare(
            Guid.NewGuid(),
            TenantId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            approvalRevision: 4,
            Guid.NewGuid(),
            exportRequested: true,
            Digest,
            "owner:approver",
            Now,
            "creator",
            Now).Value;
        _ = process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            Executor,
            Now.AddMinutes(1));
        _ = process.CompleteFreeze(
            process.OperationRevision,
            workspaceFenceRevision: 9,
            FrozenDigest,
            [new(
                OwnerKey,
                contributor.Descriptor.ContractVersion,
                contributor.Descriptor.CatalogVersion,
                contributor.Descriptor.CatalogSha256)],
            process.Version,
            Executor,
            Now.AddMinutes(2));
        _ = process.BeginPhase(
            TenantTerminationProcessPhase.Export,
            process.Version,
            Executor,
            Now.AddMinutes(3));
        return process;
    }

    private sealed class StubContributor : ITenantTerminationContributor
    {
        public TenantTerminationContributorDescriptor Descriptor { get; } =
            new(
                OwnerKey,
                TenantTerminationContract.CurrentVersion,
                [new(TenantTerminationContributionPhase.Export, [])],
                MandatoryForProduction: true,
                CatalogVersion: 1,
                CatalogSha256: Digest);

        public Task<TenantTerminationContributionResult> ExecuteAsync(
            TenantTerminationContributionRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubTerminationRepository(
        TenantTerminationProcess process,
        IReadOnlyList<TenantTerminationOwnerWorkItem> workItems)
        : ITenantTerminationRepository
    {
        public Task AddProcessAsync(
            TenantTerminationProcess candidate,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddOwnerWorkItemAsync(
            TenantTerminationOwnerWorkItem workItem,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationProcess?> GetProcessAsync(
            Guid processId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                process.Id == processId ? process : null);

        public Task<TenantTerminationProcess?> GetActiveProcessAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<TenantTerminationProcess?>(process);

        public Task<TenantTerminationProcess?> GetProcessByIdempotencyKeyAsync(
            Guid idempotencyKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                process.IdempotencyKey == idempotencyKey ? process : null);

        public Task<TenantTerminationOwnerWorkItem?> GetOwnerWorkItemAsync(
            Guid processId,
            TenantTerminationOwnerPhase phase,
            string ownerKey,
            long operationRevision,
            CancellationToken cancellationToken) =>
            Task.FromResult(workItems.SingleOrDefault(item =>
                item.ProcessId == processId &&
                item.Phase == phase &&
                item.OwnerKey == ownerKey &&
                item.OperationRevision == operationRevision));

        public Task<IReadOnlyList<TenantTerminationOwnerWorkItem>>
            ListOwnerWorkItemsAsync(
                Guid processId,
                TenantTerminationOwnerPhase phase,
                long operationRevision,
                CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TenantTerminationOwnerWorkItem>>(
                workItems.Where(item =>
                    item.ProcessId == processId &&
                    item.Phase == phase &&
                    item.OperationRevision == operationRevision).ToArray());
    }

    private sealed class StubFragmentRepository
        : ITenantTerminationExportFragmentRepository
    {
        public List<TenantTerminationExportFragment> Items { get; } = [];

        public Task AddAsync(
            TenantTerminationExportFragment fragment,
            CancellationToken cancellationToken)
        {
            this.Items.Add(fragment);
            return Task.CompletedTask;
        }

        public Task<TenantTerminationExportFragment?> GetAsync(
            Guid workItemId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.SingleOrDefault(item =>
                item.Id == workItemId));

        public Task<TenantTerminationExportFragment?>
            GetByIdempotencyKeyAsync(
                Guid idempotencyKey,
                CancellationToken cancellationToken) =>
            Task.FromResult(this.Items.SingleOrDefault(item =>
                item.IdempotencyKey == idempotencyKey));

        public Task<IReadOnlyList<TenantTerminationExportFragment>> ListAsync(
            Guid processId,
            long exportOperationRevision,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TenantTerminationExportFragment>>(
                this.Items.Where(item =>
                    item.ProcessId == processId &&
                    item.ExportOperationRevision == exportOperationRevision)
                    .ToArray());
    }

    private sealed class StubArtifactRepository
        : ITenantTerminationExportArtifactRepository
    {
        private readonly List<TenantTerminationExportArtifact> items = [];

        public Task AddAsync(
            TenantTerminationExportArtifact artifact,
            CancellationToken cancellationToken)
        {
            this.items.Add(artifact);
            return Task.CompletedTask;
        }

        public Task<TenantTerminationExportArtifact?> GetAsync(
            Guid artifactId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.items.SingleOrDefault(item =>
                item.Id == artifactId));

        public Task<TenantTerminationExportArtifact?>
            GetByIdempotencyKeyAsync(
                Guid idempotencyKey,
                CancellationToken cancellationToken) =>
            Task.FromResult(this.items.SingleOrDefault(item =>
                item.IdempotencyKey == idempotencyKey));

        public Task<TenantTerminationExportArtifact?> GetByProcessAsync(
            Guid processId,
            long exportOperationRevision,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.items.SingleOrDefault(item =>
                item.ProcessId == processId &&
                item.ExportOperationRevision == exportOperationRevision));
    }

    private sealed class RecordResultDispatcher(
        RecordTenantTerminationOwnerResultCommandHandler handler)
        : IRequestDispatcher
    {
        public async Task<Result<TResponse>> SendAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            if (command is not RecordTenantTerminationOwnerResultCommand record)
            {
                throw new NotSupportedException();
            }

            Result<TenantTerminationOwnerResultRecorded> result =
                await handler.HandleAsync(
                    record,
                    cancellationToken);
            return (Result<TResponse>)(object)result;
        }

        public Task<Result<TResponse>> QueryAsync<TResponse>(
            IQuery<TResponse> query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedArtifactPolicy(TimeSpan lifetime)
        : IDataRightsExportArtifactPolicy
    {
        public DateTimeOffset ExpiresAt(DateTimeOffset requestedAtUtc) =>
            requestedAtUtc.Add(lifetime);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FixedScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }

    private sealed class RecordingArtifactReader(byte[] content)
        : ITenantTerminationExportArtifactReader
    {
        public Task<DataRightsExportDownload> OpenVerifiedAsync(
            TenantTerminationExportArtifact artifact,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DataRightsExportDownload(
                new MemoryStream(content, writable: false),
                content.Length,
                "tenant-export.zip"));
    }

    private sealed class RecordingAuditSink : IDataRightsExportAuditSink
    {
        public List<DataRightsExportAuditFact> Facts { get; } = [];

        public Task RecordAsync(
            DataRightsExportAuditFact auditFact,
            CancellationToken cancellationToken)
        {
            this.Facts.Add(auditFact);
            return Task.CompletedTask;
        }
    }
}
