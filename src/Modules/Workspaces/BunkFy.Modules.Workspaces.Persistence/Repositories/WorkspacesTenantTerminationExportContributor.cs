namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using System.Data;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using BunkFy.Modules.Workspaces.Domain.Termination;
using Gma.Framework.Naming;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using DomainFenceState =
    Domain.Termination.WorkspaceTerminationFenceState;

internal sealed class WorkspacesTenantTerminationExportContributor(
    WorkspacesDbContext dbContext,
    IScopeContext scopeContext,
    ISystemClock clock,
    IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader outcomes,
    ILogger<WorkspacesTenantTerminationExportContributor> logger)
    : ITenantTerminationExportContributor
{
    internal const string StaffIdentityAnchorSweepCheckpointRecordType =
        "staff-identity-anchor-sweep-checkpoint";
    internal const string StaffHistoricalNoProvisionReceiptRecordType =
        "staff-historical-no-provision-receipt";

    private const int IdentityAnchorPreflightPageSize =
        StaffWorkspaceOnboardingIdentityAnchorLifecycleLimits.MaximumBatchSize;

    public DataRightsExportDescriptor ExportDescriptor =>
        WorkspacesDataRightsExportSchema.TenantTerminationDescriptor;

    public async Task<TenantTerminationContributionResult> ExportAsync(
        TenantTerminationExportRequest request,
        IDataRightsExportSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);

        DateTimeOffset startedAtUtc = clock.UtcNow;
        if (!IsValid(request, scopeContext, startedAtUtc))
        {
            return Failed(
                "workspace.termination.export-request-invalid",
                startedAtUtc);
        }

        _ = TenantIds.TryNormalize(
            request.Contribution.TenantId,
            out string? tenantId);

        if (dbContext.Database.IsRelational() &&
            dbContext.Database.CurrentTransaction is not null)
        {
            return Failed(
                "workspace.termination.export-transaction-conflict",
                startedAtUtc);
        }

        IDbContextTransaction? transaction = null;
        try
        {
            if (dbContext.Database.IsRelational())
            {
                transaction = await dbContext.Database
                    .BeginTransactionAsync(
                        IsolationLevel.RepeatableRead,
                        cancellationToken)
                    .ConfigureAwait(false);
                await WorkspaceTenantMutationLock.AcquireExclusiveAsync(
                    dbContext,
                    tenantId!,
                    cancellationToken).ConfigureAwait(false);
            }

            WorkspaceTerminationFence? selectedFence =
                await this.GetFenceAsync(
                    request,
                    tenantId!,
                    cancellationToken)
                    .ConfigureAwait(false);
            if (!Matches(request, selectedFence))
            {
                return RetryRequired(
                    "workspace.termination.export-fence-unavailable",
                    clock.UtcNow);
            }

            if (!await this.PreflightIdentityAnchorsAsync(
                    tenantId!,
                    cancellationToken).ConfigureAwait(false))
            {
                return RetryRequired(
                    "workspace.termination.export-identity-anchor-unavailable",
                    clock.UtcNow);
            }

            long recordCount = await this.ExportRecordsAsync(
                tenantId!,
                sink,
                cancellationToken).ConfigureAwait(false);

            dbContext.ChangeTracker.Clear();
            WorkspaceTerminationFence? resultingFence =
                await this.GetFenceAsync(
                    request,
                    tenantId!,
                    cancellationToken)
                    .ConfigureAwait(false);
            if (!Matches(request, resultingFence) ||
                resultingFence!.Version != selectedFence!.Version)
            {
                return RetryRequired(
                    "workspace.termination.export-revision-changed",
                    clock.UtcNow);
            }

            DateTimeOffset completedAtUtc = clock.UtcNow;
            if (completedAtUtc > request.Contribution.DeadlineUtc)
            {
                return RetryRequired(
                    "workspace.termination.export-deadline-expired",
                    completedAtUtc);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return new TenantTerminationContributionResult(
                TenantTerminationContributionStatus.Completed,
                "workspace.termination.exported",
                recordCount,
                RetainedMinimumCount: 0,
                RemainingActiveCount: 0,
                HoldReviewAtUtc: null,
                selectedFence.Version,
                resultingFence.Version,
                WorkspacesTenantTerminationMetadata.CatalogVersion,
                WorkspacesTenantTerminationMetadata.CatalogSha256,
                completedAtUtc);
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> PreflightIdentityAnchorsAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        Guid? afterApplicationId = null;
        while (true)
        {
            IQueryable<WorkspaceStaffOnboarding> query =
                dbContext.StaffOnboardingApplications
                    .AsNoTracking()
                    .Where(application => application.ScopeId == tenantId);
            if (afterApplicationId.HasValue)
            {
                Guid cursor = afterApplicationId.Value;
                query = query.Where(application =>
                    application.Id.CompareTo(cursor) > 0);
            }

            WorkspaceStaffOnboarding[] loaded = await query
                .OrderBy(application => application.Id)
                .Take(IdentityAnchorPreflightPageSize + 1)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            bool hasMore = loaded.Length > IdentityAnchorPreflightPageSize;
            WorkspaceStaffOnboarding[] page = loaded
                .Take(IdentityAnchorPreflightPageSize)
                .ToArray();
            if (page.Length == 0)
            {
                return !hasMore;
            }

            if (afterApplicationId.HasValue &&
                page[0].Id.CompareTo(afterApplicationId.Value) <= 0)
            {
                return false;
            }

            IReadOnlyList<
                StaffWorkspaceOnboardingIdentityAnchorOutcome> read;
            try
            {
                read = await outcomes.ReadAsync(
                    page.Select(application =>
                        new StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest(
                            application.Id,
                            application.SubjectId))
                    .ToArray(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Staff identity-anchor preflight was cancelled outside the Workspaces tenant export request.");
                return false;
            }
            catch (Exception exception) when (exception is not
                OperationCanceledException)
            {
                logger.LogWarning(
                    "Staff identity-anchor preflight is unavailable for Workspaces tenant export.");
                return false;
            }

            if (read is null ||
                read.Count != page.Length ||
                read.Select(outcome => outcome.ApplicationId)
                    .Distinct().Count() != read.Count)
            {
                return false;
            }

            Dictionary<Guid, StaffWorkspaceOnboardingIdentityAnchorOutcome>
                byApplication = read.ToDictionary(
                    outcome => outcome.ApplicationId);
            if (page.Any(application =>
                !byApplication.TryGetValue(
                    application.Id,
                    out StaffWorkspaceOnboardingIdentityAnchorOutcome?
                        outcome) ||
                !WorkspaceStaffOnboardingExportAuthority.IsAuthorized(
                    application,
                    outcome)))
            {
                return false;
            }

            Guid next = page[^1].Id;
            if (afterApplicationId.HasValue &&
                next.CompareTo(afterApplicationId.Value) <= 0)
            {
                return false;
            }

            if (!hasMore)
            {
                return true;
            }

            afterApplicationId = next;
        }
    }

    private async Task<long> ExportRecordsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        CancellationToken cancellationToken)
    {
        long count = 0;

        await foreach (WorkspaceStaffOnboardingDataRightsExport record in
                       dbContext.StaffOnboardingApplications
                           .AsNoTracking()
                           .Where(item => item.ScopeId == tenantId)
                           .OrderBy(item => item.Id)
                           .Select(item =>
                               new WorkspaceStaffOnboardingDataRightsExport(
                                   item.Id,
                                   item.ScopeId,
                                   item.SourceKind,
                                   item.SourceId,
                                   item.ClaimId,
                                   item.ClaimVersion,
                                   item.SubjectId,
                                   item.VerifiedAccountEmail,
                                   item.DisplayName,
                                   item.LegalName,
                                   item.WorkEmail,
                                   item.WorkPhone,
                                   item.EmployeeNumber,
                                   item.JobTitle,
                                   item.Department,
                                   item.Status,
                                   item.StaffMemberId,
                                   item.IdentityAnchorExpectedResolutionEventId,
                                   item.IdentityAnchorContinuationEventId,
                                   item.IdentityAnchorResolutionEventId,
                                   item.IdentityAnchorResolutionStaffMemberId,
                                   item.IdentityAnchorResolutionApplicationVersion,
                                   item.IdentityAnchorResolutionDisposition,
                                   item.IdentityAnchorResolutionIntentAtUtc,
                                   item.IdentityAnchorResolutionObservedAtUtc,
                                   item.IdentityAnchorSweepOrdinal,
                                   item.FailureCode,
                                   item.Version,
                                   item.CreatedAtUtc,
                                   item.LastChangedAtUtc))
                           .AsAsyncEnumerable()
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            await WriteAsync(
                WorkspacesDataRightsCoordinates.StaffOnboardingRecordType,
                record.Id,
                record.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }


        await foreach (
            WorkspaceStaffOnboardingRecoveryReceiptDataRightsExport receipt in
            dbContext.StaffOnboardingRecoveryReceipts
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.Id)
                .Select(WorkspacesDataRightsExportContributor
                    .RecoveryReceiptProjection)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            await WriteAsync(
                WorkspacesDataRightsExportContributor
                    .StaffOnboardingRecoveryReceiptRecordType,
                receipt.ReceiptId,
                receipt.ContractVersion,
                receipt,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        await foreach (
            WorkspaceStaffIdentityAnchorSweepCheckpoint checkpoint in
            dbContext.StaffIdentityAnchorSweepCheckpoints
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            WorkspaceStaffIdentityAnchorSweepPageCounts current =
                checkpoint.CurrentCounts();
            WorkspaceStaffIdentityAnchorSweepPageCounts lastCompleted =
                checkpoint.LastCompletedCounts();
            WorkspaceStaffIdentityAnchorSweepCheckpointDataRightsExport
                record = new(
                    new WorkspaceStaffIdentityAnchorSweepCheckpointExportState(
                        checkpoint.ProtocolVersion,
                        checkpoint.HasActiveCycle,
                        checkpoint.CycleUpperOrdinal,
                        checkpoint.AfterOrdinal,
                        checkpoint.CycleStartedAtUtc,
                        current.ScannedCount,
                        current.BacklogCount,
                        checkpoint.LastCompletedUpperOrdinal,
                        checkpoint.LastCompletedAtUtc,
                        lastCompleted.ScannedCount,
                        lastCompleted.BacklogCount,
                        checkpoint.UpdatedAtUtc));
            await WriteAsync(
                StaffIdentityAnchorSweepCheckpointRecordType,
                checkpoint.Id,
                checkpoint.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        await foreach (
            WorkspaceStaffHistoricalNoProvisionReceipt receipt in
            dbContext.StaffHistoricalNoProvisionReceipts
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            WorkspaceStaffHistoricalNoProvisionReceiptDataRightsExport
                record = new(
                    receipt.ContractVersion,
                    receipt.Id,
                    receipt.ScopeId,
                    receipt.OperationId,
                    receipt.ApplicationId,
                    receipt.SourceKind,
                    receipt.SourceId,
                    receipt.ExpectedApplicationVersion,
                    receipt.ExpectedApplicationStatus,
                    receipt.ResultApplicationVersion,
                    receipt.ResultApplicationStatus,
                    receipt.OrganizationsScopeRevision,
                    receipt.OrganizationsSourceVersion,
                    receipt.OrganizationsSourceStatus,
                    receipt.StaffEvidenceSha256,
                    receipt.ExternalEvidenceManifestId,
                    receipt.ExternalEvidenceSha256,
                    receipt.ReviewerId,
                    receipt.ReviewedAtUtc,
                    receipt.CanonicalSha256);
            await WriteAsync(
                StaffHistoricalNoProvisionReceiptRecordType,
                receipt.Id,
                receipt.ContractVersion,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        await foreach (
            WorkspaceStaffDeferredClaimWithdrawalDataRightsExport record in
            dbContext.StaffDeferredClaimWithdrawals
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.Id)
                .Select(item =>
                    new WorkspaceStaffDeferredClaimWithdrawalDataRightsExport(
                        item.Id,
                        item.ScopeId,
                        item.EnrollmentLinkId,
                        item.ClaimVersion,
                        item.EventId,
                        item.OccurredAtUtc))
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            await WriteAsync(
                WorkspacesDataRightsExportContributor
                    .StaffDeferredClaimWithdrawalRecordType,
                record.ClaimId,
                record.ClaimVersion,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        await foreach (WorkspaceStaffOnboardingCorrectionReceipt receipt in
                       dbContext.StaffOnboardingCorrectionReceipts
                           .AsNoTracking()
                           .Where(item => item.ScopeId == tenantId)
                           .OrderBy(item => item.Id)
                           .AsAsyncEnumerable()
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            WorkspaceStaffOnboardingCorrectionReceiptDataRightsExport record =
                new(
                    receipt.ContractVersion,
                    receipt.Id,
                    receipt.ExecutionId,
                    receipt.CaseId,
                    receipt.ApprovalRevision,
                    receipt.ApplicationId,
                    receipt.SelectedRecordVersion,
                    receipt.CurrentRecordVersion,
                    receipt.ChangedFields,
                    receipt.ApplicantEventId,
                    receipt.CompletionEventId,
                    receipt.CompletedAtUtc);
            await WriteAsync(
                WorkspacesDataRightsExportContributor
                    .StaffOnboardingCorrectionReceiptRecordType,
                receipt.Id,
                receipt.CurrentRecordVersion,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        await foreach (
            WorkspaceStaffOnboardingProcessingRestriction restriction in
            dbContext.StaffOnboardingProcessingRestrictions
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            WorkspaceStaffOnboardingProcessingRestrictionDataRightsExport
                record = new(
                    restriction.Id,
                    restriction.ApplicationId,
                    restriction.ApplyCaseId,
                    restriction.ApplyApprovalRevision,
                    restriction.ApplySelectedOnboardingVersion,
                    restriction.Status,
                    restriction.Version,
                    restriction.AppliedAtUtc,
                    restriction.ReleaseCaseId,
                    restriction.ReleaseApprovalRevision,
                    restriction.ReleaseSelectedOnboardingVersion,
                    restriction.ReleasedAtUtc);
            await WriteAsync(
                WorkspacesDataRightsExportContributor
                    .StaffOnboardingProcessingRestrictionRecordType,
                restriction.Id,
                restriction.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        await foreach (
            WorkspaceStaffOnboardingProcessingRestrictionReceipt receipt in
            dbContext.StaffOnboardingProcessingRestrictionReceipts
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            WorkspaceStaffOnboardingProcessingRestrictionReceiptDataRightsExport
                record = new(
                    receipt.Id,
                    receipt.RestrictionId,
                    receipt.Action,
                    receipt.ApplicationId,
                    receipt.CaseId,
                    receipt.ApprovalRevision,
                    receipt.SelectedOnboardingVersion,
                    receipt.ResultingRestrictionVersion,
                    receipt.ResultingProjectionRevision,
                    receipt.EffectiveRestricted,
                    receipt.EventId,
                    receipt.CompletedAtUtc);
            await WriteAsync(
                WorkspacesDataRightsExportContributor
                    .StaffOnboardingProcessingRestrictionReceiptRecordType,
                receipt.Id,
                receipt.ResultingRestrictionVersion,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        await foreach (WorkspaceStaffAccessProcessDataRightsExport record in
                       dbContext.StaffAccessProcesses
                           .AsNoTracking()
                           .Where(item => item.ScopeId == tenantId)
                           .OrderBy(item => item.Id)
                           .Select(item =>
                               new WorkspaceStaffAccessProcessDataRightsExport(
                                   item.Id,
                                   item.ScopeId,
                                   item.StaffMemberId,
                                   item.SubjectId,
                                   item.TargetState,
                                   item.TargetStaffVersion,
                                   item.EffectiveOn,
                                   item.RequestedBy,
                                   item.State,
                                   item.RestorationDisposition,
                                   item.FailureCode,
                                   item.Version,
                                   item.CreatedAtUtc,
                                   item.LastChangedAtUtc,
                                   item.CompletedAtUtc))
                           .AsAsyncEnumerable()
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            await WriteAsync(
                WorkspacesDataRightsCoordinates.StaffAccessProcessRecordType,
                record.Id,
                record.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        await foreach (AccessProfileExportRow row in
                       (from process in dbContext.StaffAccessProcesses
                            .AsNoTracking()
                        where process.ScopeId == tenantId
                        from profile in process.ProfileSnapshots
                        orderby process.Id,
                            profile.AssignmentScope,
                            profile.ProfileId
                        select new AccessProfileExportRow(
                            process.Id,
                            process.Version,
                            profile.ProfileId,
                            profile.AssignmentScope))
                       .AsAsyncEnumerable()
                       .WithCancellation(cancellationToken)
                       .ConfigureAwait(false))
        {
            WorkspaceStaffAccessProfileDataRightsExport record = new(
                row.ProcessId,
                row.ProfileId,
                row.AssignmentScope);
            await WriteAsync(
                WorkspacesDataRightsExportContributor
                    .StaffAccessProfileSnapshotRecordType,
                DataRightsExportRecordIds.CreateDeterministicChild(
                    row.ProcessId,
                    $"{row.ProfileId:N}|{row.AssignmentScope}"),
                row.ProcessVersion,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        await foreach (WorkspaceStaffAccessPlanDataRightsExport record in
                       dbContext.StaffAccessPlans
                           .AsNoTracking()
                           .Where(item => item.ScopeId == tenantId)
                           .OrderBy(item => item.Id)
                           .Select(item =>
                               new WorkspaceStaffAccessPlanDataRightsExport(
                                   item.Id,
                                   item.ScopeId,
                                   item.SourceKind,
                                   item.ProfileId,
                                   item.ProfileKey,
                                   item.CreatedBySubjectId,
                                   item.Status,
                                   item.SourceExpiredAtUtc,
                                   item.Version,
                                   item.CreatedAtUtc,
                                   item.LastChangedAtUtc))
                           .AsAsyncEnumerable()
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            await WriteAsync(
                WorkspacesDataRightsCoordinates.StaffAccessPlanRecordType,
                record.Id,
                record.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        await foreach (AccessPlanPropertyExportRow row in
                       (from plan in dbContext.StaffAccessPlans
                            .AsNoTracking()
                        where plan.ScopeId == tenantId
                        from property in plan.Properties
                        orderby plan.Id, property.PropertyId
                        select new AccessPlanPropertyExportRow(
                            plan.Id,
                            plan.Version,
                            property.ScopeId,
                            property.PropertyId))
                       .AsAsyncEnumerable()
                       .WithCancellation(cancellationToken)
                       .ConfigureAwait(false))
        {
            WorkspaceStaffAccessPlanPropertyDataRightsExport record = new(
                row.ScopeId,
                row.PlanId,
                row.PropertyId);
            await WriteAsync(
                WorkspacesDataRightsExportContributor
                    .StaffAccessPlanPropertyRecordType,
                DataRightsExportRecordIds.CreateDeterministicChild(
                    row.PlanId,
                    row.PropertyId.ToString("N")),
                row.PlanVersion,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        await foreach (WorkspaceStaffRetentionCorrelationReceipt receipt in
                       dbContext.StaffRetentionCorrelationReceipts
                           .AsNoTracking()
                           .Where(item => item.ScopeId == tenantId)
                           .OrderBy(item => item.Id)
                           .AsAsyncEnumerable()
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            WorkspaceStaffRetentionCorrelationDataRightsExport record = new(
                receipt.ScopeId,
                receipt.StaffMemberId,
                receipt.SelectedStaffVersion,
                new WorkspaceStaffRetentionCorrelationProofDataRightsExport(
                    receipt.Id,
                    receipt.ContractVersion,
                    receipt.ExecutionId,
                    receipt.OnboardingRecordsScrubbed,
                    receipt.AccessProcessRecordsScrubbed,
                    receipt.AccessPlanRecordsScrubbed,
                    receipt.CompletedAtUtc,
                    receipt.CanonicalSha256));
            await WriteAsync(
                WorkspacesDataRightsCoordinates
                    .StaffRetentionCorrelationReceiptRecordType,
                receipt.Id,
                receipt.ContractVersion,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        await foreach (
            WorkspaceStaffOnboardingRetentionExecution execution in
            dbContext.StaffOnboardingRetentionExecutions
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            WorkspaceStaffOnboardingRetentionExecutionDataRightsExport record =
                new(
                    execution.ScopeId,
                    execution.Id,
                    new(
                        execution.DataClassKey,
                        execution.ExecutionPolicyVersion,
                        execution.Attempt,
                        execution.State,
                        execution.StartedAtUtc,
                        execution.DeadlineUtc,
                        execution.CompletedAtUtc,
                        execution.AffectedCount,
                        execution.ScannedCount,
                        execution.RemainingCount,
                        execution.OutcomeCode,
                        execution.Version));
            await WriteAsync(
                WorkspacesTenantTerminationMetadata
                    .StaffOnboardingRetentionExecutionRecordType,
                execution.Id,
                execution.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private Task<WorkspaceTerminationFence?> GetFenceAsync(
        TenantTerminationExportRequest request,
        string tenantId,
        CancellationToken cancellationToken) =>
        dbContext.WorkspaceTerminationFences
            .AsNoTracking()
            .SingleOrDefaultAsync(
                fence =>
                    fence.ScopeId == tenantId &&
                    fence.ProcessId == request.Contribution.ProcessId,
                cancellationToken);

    private static bool Matches(
        TenantTerminationExportRequest request,
        WorkspaceTerminationFence? fence) =>
        fence is not null &&
        fence.CaseId == request.Contribution.CaseId &&
        fence.ApprovalRevision == request.Contribution.ApprovalRevision &&
        fence.TerminationEpoch == request.Contribution.TerminationEpoch &&
        fence.State == DomainFenceState.Frozen &&
        fence.Version == request.WorkspaceFenceRevision &&
        string.Equals(
            fence.PolicyEvidenceSha256,
            request.Contribution.PolicyEvidenceSha256,
            StringComparison.Ordinal) &&
        fence.CreatedAtUtc <= request.FrozenAtUtc &&
        fence.LastChangedAtUtc == fence.CreatedAtUtc;

    private static bool IsValid(
        TenantTerminationExportRequest request,
        IScopeContext scopeContext,
        DateTimeOffset nowUtc)
    {
        TenantTerminationContributionRequest contribution =
            request.Contribution;
        return contribution is not null &&
            contribution.ContractVersion ==
                TenantTerminationContract.CurrentVersion &&
            TenantIds.TryNormalize(
                contribution.TenantId,
                out string? tenantId) &&
            scopeContext.IsEnabled &&
            string.Equals(
                scopeContext.ScopeId,
                tenantId,
                StringComparison.Ordinal) &&
            contribution.ProcessId != Guid.Empty &&
            contribution.CaseId != Guid.Empty &&
            contribution.ApprovalRevision > 0 &&
            contribution.OperationRevision > request.FreezeOperationRevision &&
            request.FreezeOperationRevision > 0 &&
            contribution.TerminationEpoch != Guid.Empty &&
            contribution.Phase == TenantTerminationContributionPhase.Export &&
            contribution.WorkItemId != Guid.Empty &&
            contribution.IdempotencyKey != Guid.Empty &&
            IsSha256(contribution.PolicyEvidenceSha256) &&
            IsSha256(request.FrozenRevisionSha256) &&
            request.WorkspaceFenceRevision > 0 &&
            request.FrozenAtUtc != default &&
            request.FrozenAtUtc <= nowUtc &&
            contribution.DeadlineUtc > nowUtc &&
            IsActor(contribution.ExecutingActorId);
    }

    private static async ValueTask WriteAsync(
        string recordType,
        Guid recordId,
        long recordVersion,
        object source,
        IDataRightsExportSink sink,
        CancellationToken cancellationToken) =>
        await sink.WriteAsync(
            WorkspacesDataRightsExportSchema.CreateRecord(
                recordType,
                recordId,
                recordVersion,
                source),
            cancellationToken).ConfigureAwait(false);

    private static TenantTerminationContributionResult RetryRequired(
        string code,
        DateTimeOffset recordedAtUtc) =>
        Result(
            TenantTerminationContributionStatus.RetryRequired,
            code,
            recordedAtUtc);

    private static TenantTerminationContributionResult Failed(
        string code,
        DateTimeOffset recordedAtUtc) =>
        Result(
            TenantTerminationContributionStatus.Failed,
            code,
            recordedAtUtc);

    private static TenantTerminationContributionResult Result(
        TenantTerminationContributionStatus status,
        string code,
        DateTimeOffset recordedAtUtc) =>
        new(
            status,
            code,
            AffectedCount: 0,
            RetainedMinimumCount: 0,
            RemainingActiveCount: 1,
            HoldReviewAtUtc: null,
            SelectedProofRevision: null,
            ResultingProofRevision: null,
            WorkspacesTenantTerminationMetadata.CatalogVersion,
            WorkspacesTenantTerminationMetadata.CatalogSha256,
            recordedAtUtc);

    private static bool IsActor(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <=
            TenantTerminationContract.ActorIdMaxLength;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: TenantTerminationContract.Sha256Length } &&
        value.All(character =>
            character is (>= '0' and <= '9') or
                (>= 'a' and <= 'f'));

    private sealed record AccessProfileExportRow(
        Guid ProcessId,
        long ProcessVersion,
        Guid ProfileId,
        string AssignmentScope);

    private sealed record AccessPlanPropertyExportRow(
        Guid PlanId,
        long PlanVersion,
        string ScopeId,
        Guid PropertyId);
}
