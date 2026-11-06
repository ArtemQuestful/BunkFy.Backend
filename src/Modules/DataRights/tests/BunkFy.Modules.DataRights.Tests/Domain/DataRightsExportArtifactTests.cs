namespace BunkFy.Modules.DataRights.Tests.Domain;

using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DataRightsExportArtifactTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly string SelectionHash = new('a', 64);

    [Fact]
    public void Request_freezes_scope_selection_and_expiry()
    {
        Guid idempotencyKey = Guid.NewGuid();
        Guid caseId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();

        DataRightsExportArtifact artifact = DataRightsExportArtifact.Request(
            Guid.NewGuid(),
            " tenant-a ",
            idempotencyKey,
            caseId,
            propertyId,
            DataRightsCaseKind.GuestRights,
            decisionRevision: 7,
            selectedSubjectCount: 2,
            SelectionHash,
            " user:operator ",
            Now,
            Now.AddHours(24)).Value;

        Assert.Equal("tenant-a", artifact.ScopeId);
        Assert.Equal(propertyId, artifact.PropertyId);
        Assert.Equal(DataRightsExportArtifactState.Requested, artifact.State);
        Assert.Equal(Now.AddHours(24), artifact.ExpiresAtUtc);
        Assert.True(artifact.Matches(
            idempotencyKey,
            caseId,
            decisionRevision: 7,
            SelectionHash));
    }

    [Fact]
    public void Generation_retry_keeps_original_generation_time()
    {
        DataRightsExportArtifact artifact = StaffArtifact();
        Guid runId = Guid.NewGuid();

        Assert.True(artifact.BeginGeneration(
            runId,
            attempt: 1,
            "system:data-rights-export",
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(artifact.BeginGeneration(
            runId,
            attempt: 2,
            "system:data-rights-export",
            Now.AddMinutes(3)).IsSuccess);

        Assert.Equal(Now.AddMinutes(1), artifact.GenerationStartedAtUtc);
        Assert.Equal(2, artifact.GenerationAttempt);
        Assert.Equal(DataRightsExportArtifactState.Generating, artifact.State);
    }

    [Fact]
    public void Failed_generation_requires_explicit_version_pinned_retry()
    {
        DataRightsExportArtifact artifact = StaffArtifact();
        Guid runId = Guid.NewGuid();
        _ = artifact.BeginGeneration(
            runId,
            attempt: 1,
            "system:data-rights-export",
            Now.AddMinutes(1));
        _ = artifact.MarkFailed(
            runId,
            attempt: 1,
            "owner-export-failed",
            Now.AddMinutes(2));
        long failedVersion = artifact.Version;

        Assert.Equal(
            "DataRights.ExportArtifactTransitionInvalid",
            artifact.BeginGeneration(
                runId,
                attempt: 2,
                "system:data-rights-export",
                Now.AddMinutes(3)).Error.Code);
        Assert.True(artifact.RequestRetry(
            failedVersion,
            Now.AddMinutes(3)).IsSuccess);

        Assert.Equal(DataRightsExportArtifactState.Requested, artifact.State);
        Assert.Equal(failedVersion, artifact.LastRetryBaseVersion);
        Assert.Null(artifact.GenerationActor);
        Assert.Null(artifact.GenerationRunId);
        Assert.Null(artifact.GenerationAttempt);
        Assert.Null(artifact.GenerationStartedAtUtc);
        Assert.Null(artifact.FailureCode);
        Assert.Equal(failedVersion + 1, artifact.Version);
    }

    [Fact]
    public void Manual_retry_replay_is_idempotent_after_generation_advances()
    {
        DataRightsExportArtifact artifact = StaffArtifact();
        Guid firstRunId = Guid.NewGuid();
        _ = artifact.BeginGeneration(
            firstRunId,
            attempt: 1,
            "system:data-rights-export",
            Now.AddMinutes(1));
        _ = artifact.MarkFailed(
            firstRunId,
            attempt: 1,
            "owner-export-failed",
            Now.AddMinutes(2));
        long failedVersion = artifact.Version;
        _ = artifact.RequestRetry(failedVersion, Now.AddMinutes(3));
        _ = artifact.BeginGeneration(
            Guid.NewGuid(),
            attempt: 1,
            "system:data-rights-export",
            Now.AddMinutes(4));
        long advancedVersion = artifact.Version;

        Assert.True(artifact.RequestRetry(
            failedVersion,
            Now.AddMinutes(5)).IsSuccess);
        Assert.Equal(advancedVersion, artifact.Version);
        Assert.Equal(DataRightsExportArtifactState.Generating, artifact.State);
    }

    [Fact]
    public void Matching_failure_retry_is_idempotent()
    {
        DataRightsExportArtifact artifact = StaffArtifact();
        Guid runId = Guid.NewGuid();
        _ = artifact.BeginGeneration(
            runId,
            attempt: 1,
            "system:data-rights-export",
            Now.AddMinutes(1));
        _ = artifact.MarkFailed(
            runId,
            attempt: 1,
            "owner-export-failed",
            Now.AddMinutes(2));
        long failedVersion = artifact.Version;

        Assert.True(artifact.MarkFailed(
            runId,
            attempt: 1,
            "owner-export-failed",
            Now.AddMinutes(3)).IsSuccess);
        Assert.Equal(failedVersion, artifact.Version);
        Assert.Equal(
            "DataRights.ExportArtifactFailureInvalid",
            artifact.MarkFailed(
                runId,
                attempt: 1,
                "different-failure",
                Now.AddMinutes(3)).Error.Code);
    }

    [Fact]
    public void Requested_generation_can_be_rejected_with_run_bound_failure()
    {
        DataRightsExportArtifact artifact = StaffArtifact();
        Guid runId = Guid.NewGuid();

        Assert.True(artifact.RejectGeneration(
            runId,
            attempt: 1,
            "system:data-rights-export",
            "owner-catalog-invalid",
            Now.AddMinutes(1)).IsSuccess);
        long failedVersion = artifact.Version;
        Assert.True(artifact.RejectGeneration(
            runId,
            attempt: 1,
            "system:data-rights-export",
            "owner-catalog-invalid",
            Now.AddMinutes(2)).IsSuccess);

        Assert.Equal(DataRightsExportArtifactState.Failed, artifact.State);
        Assert.Equal(runId, artifact.GenerationRunId);
        Assert.Equal(1, artifact.GenerationAttempt);
        Assert.Equal(Now.AddMinutes(1), artifact.GenerationStartedAtUtc);
        Assert.Equal("owner-catalog-invalid", artifact.FailureCode);
        Assert.Equal(failedVersion, artifact.Version);
    }

    [Fact]
    public void Completion_requires_matching_attempt_and_frozen_expiry()
    {
        DataRightsExportArtifact artifact = StaffArtifact();
        Guid runId = Guid.NewGuid();
        _ = artifact.BeginGeneration(
            runId,
            attempt: 1,
            "system:data-rights-export",
            Now.AddMinutes(1));

        Assert.Equal(
            "DataRights.ExportArtifactCompletionInvalid",
            artifact.MarkAvailable(
                Guid.NewGuid(),
                attempt: 1,
                "data-rights/exports/artifact.bfdrx",
                encryptedByteLength: 100,
                SelectionHash,
                encryptionKeyVersion: 1,
                formatVersion: 1,
                Now.AddMinutes(2),
                Now.AddHours(24)).Error.Code);
        Assert.Equal(
            "DataRights.TimestampInvalid",
            artifact.MarkAvailable(
                runId,
                attempt: 1,
                "data-rights/exports/artifact.bfdrx",
                encryptedByteLength: 100,
                SelectionHash,
                encryptionKeyVersion: 1,
                formatVersion: 1,
                Now.AddMinutes(2),
                Now.AddHours(12)).Error.Code);

        Assert.True(artifact.MarkAvailable(
            runId,
            attempt: 1,
            "data-rights/exports/artifact.bfdrx",
            encryptedByteLength: 100,
            SelectionHash,
            encryptionKeyVersion: 1,
            formatVersion: 1,
            Now.AddMinutes(2),
            Now.AddHours(24)).IsSuccess);
        Assert.Equal(DataRightsExportArtifactState.Available, artifact.State);
    }

    [Fact]
    public void Completion_retry_must_match_the_committed_artifact_metadata()
    {
        DataRightsExportArtifact artifact = StaffArtifact();
        Guid runId = Guid.NewGuid();
        DateTimeOffset availableAt = Now.AddMinutes(2);
        _ = artifact.BeginGeneration(
            runId,
            attempt: 1,
            "system:data-rights-export",
            Now.AddMinutes(1));
        Assert.True(artifact.MarkAvailable(
            runId,
            attempt: 1,
            "data-rights/exports/artifact.bfdrx",
            encryptedByteLength: 100,
            SelectionHash,
            encryptionKeyVersion: 1,
            formatVersion: 1,
            availableAt,
            Now.AddHours(24)).IsSuccess);

        Assert.True(artifact.MarkAvailable(
            runId,
            attempt: 1,
            " data-rights/exports/artifact.bfdrx ",
            encryptedByteLength: 100,
            SelectionHash.ToUpperInvariant(),
            encryptionKeyVersion: 1,
            formatVersion: 1,
            availableAt,
            Now.AddHours(24)).IsSuccess);
        Assert.Equal(
            "DataRights.ExportArtifactCompletionInvalid",
            artifact.MarkAvailable(
                runId,
                attempt: 1,
                "data-rights/exports/other.bfdrx",
                encryptedByteLength: 100,
                SelectionHash,
                encryptionKeyVersion: 1,
                formatVersion: 1,
                availableAt,
                Now.AddHours(24)).Error.Code);
    }

    [Fact]
    public void Generation_cannot_start_at_or_after_frozen_expiry()
    {
        DataRightsExportArtifact artifact = StaffArtifact();

        Assert.Equal(
            "DataRights.TimestampInvalid",
            artifact.BeginGeneration(
                Guid.NewGuid(),
                attempt: 1,
                "system:data-rights-export",
                artifact.ExpiresAtUtc).Error.Code);
        Assert.Equal(DataRightsExportArtifactState.Requested, artifact.State);
    }

    [Fact]
    public void Expiry_and_deletion_are_retryable_and_run_bound()
    {
        DataRightsExportArtifact artifact = StaffArtifact();
        Guid generationRunId = Guid.NewGuid();
        Guid deletionRunId = Guid.NewGuid();
        _ = artifact.BeginGeneration(
            generationRunId,
            attempt: 1,
            "system:data-rights-export",
            Now.AddMinutes(1));
        _ = artifact.MarkAvailable(
            generationRunId,
            attempt: 1,
            "data-rights/exports/artifact.bfdrx",
            encryptedByteLength: 100,
            SelectionHash,
            encryptionKeyVersion: 1,
            formatVersion: 1,
            Now.AddMinutes(2),
            Now.AddHours(24));

        Assert.True(artifact.MarkExpired(Now.AddHours(24)).IsSuccess);
        Assert.Equal(DataRightsExportArtifactState.Expired, artifact.State);
        Assert.True(artifact.BeginDeletion(
            deletionRunId,
            Now.AddHours(24)).IsSuccess);
        Assert.Equal(DataRightsExportArtifactState.Deleting, artifact.State);
        Assert.Equal(
            "DataRights.ExportArtifactDeletionInvalid",
            artifact.MarkDeleted(
                Guid.NewGuid(),
                Now.AddHours(24).AddMinutes(1)).Error.Code);
        Assert.True(artifact.MarkDeleted(
            deletionRunId,
            Now.AddHours(24).AddMinutes(1)).IsSuccess);
        long deletedVersion = artifact.Version;
        Assert.True(artifact.MarkDeleted(
            deletionRunId,
            Now.AddHours(24).AddMinutes(2)).IsSuccess);
        Assert.Equal(DataRightsExportArtifactState.Deleted, artifact.State);
        Assert.Equal(deletedVersion, artifact.Version);
    }

    [Fact]
    public void Requested_artifact_can_expire_without_storage_metadata()
    {
        DataRightsExportArtifact artifact = StaffArtifact();
        Guid deletionRunId = Guid.NewGuid();

        Assert.True(artifact.MarkExpired(artifact.ExpiresAtUtc).IsSuccess);
        Assert.True(artifact.BeginDeletion(
            deletionRunId,
            artifact.ExpiresAtUtc).IsSuccess);
        Assert.True(artifact.MarkDeleted(
            deletionRunId,
            artifact.ExpiresAtUtc.AddSeconds(1)).IsSuccess);

        Assert.Null(artifact.StorageKey);
        Assert.Equal(DataRightsExportArtifactState.Deleted, artifact.State);
    }

    [Fact]
    public void Approved_access_export_completion_is_revision_bound_and_idempotent()
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.StaffRights,
            DataRightsCaseOperation.AccessExport,
            DataRightsRequesterRelation.ControllerInitiated).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            Guid.NewGuid(),
            "tenant-a",
            request,
            "user:privacy",
            Now.AddMinutes(-5)).Value;
        _ = dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-4));
        _ = dataRightsCase.SelectSubject(
            "staff",
            "staff-profile",
            Guid.NewGuid(),
            recordVersion: 3,
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-3));
        _ = dataRightsCase.RequireReview(
            dataRightsCase.Version,
            "user:privacy",
            Now.AddMinutes(-2));
        _ = dataRightsCase.BeginDecision(
            dataRightsCase.Version,
            "user:decision-maker",
            Now.AddMinutes(-1));
        _ = dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            dataRightsCase.Version,
            "user:decision-maker",
            Now);
        long decisionRevision = dataRightsCase.DecisionRevision!.Value;

        Assert.Equal(
            "DataRights.AccessExportCompletionInvalid",
            dataRightsCase.CompleteAccessExport(
                decisionRevision + 1,
                "system:data-rights-export",
                Now.AddMinutes(1)).Error.Code);
        Assert.True(dataRightsCase.CompleteAccessExport(
            decisionRevision,
            "system:data-rights-export",
            Now.AddMinutes(1)).IsSuccess);
        long completedVersion = dataRightsCase.Version;
        Assert.True(dataRightsCase.CompleteAccessExport(
            decisionRevision,
            "system:data-rights-export",
            Now.AddMinutes(2)).IsSuccess);
        Assert.Equal(DataRightsCaseState.Completed, dataRightsCase.Status);
        Assert.Equal(completedVersion, dataRightsCase.Version);
    }

    [Theory]
    [InlineData(DataRightsCaseKind.GuestRights, false)]
    [InlineData(DataRightsCaseKind.StaffRights, true)]
    [InlineData(DataRightsCaseKind.TenantTermination, false)]
    public void Request_rejects_invalid_scope(
        DataRightsCaseKind kind,
        bool includeProperty)
    {
        Assert.Equal(
            "DataRights.ExportArtifactCoordinateInvalid",
            DataRightsExportArtifact.Request(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid(),
                includeProperty ? Guid.NewGuid() : null,
                kind,
                decisionRevision: 7,
                selectedSubjectCount: 1,
                SelectionHash,
                "user:operator",
                Now,
                Now.AddHours(24)).Error.Code);
    }

    private static DataRightsExportArtifact StaffArtifact() =>
        DataRightsExportArtifact.Request(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            propertyId: null,
            DataRightsCaseKind.StaffRights,
            decisionRevision: 7,
            selectedSubjectCount: 1,
            SelectionHash,
            "user:operator",
            Now,
            Now.AddHours(24)).Value;
}
