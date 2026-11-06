namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;

internal sealed class WorkspacesDataRightsDiscoveryContributor(
    WorkspacesDbContext dbContext,
    IScopeContext scopeContext) : IDataRightsSubjectDiscoveryContributor
{
    public string OwnerKey => WorkspacesDataRightsCoordinates.Owner;

    public IReadOnlyCollection<DataRightsCaseType> SupportedCaseTypes { get; } =
        [DataRightsCaseType.StaffRights];

    public async Task<DataRightsSubjectDiscoveryResult> DiscoverAsync(
        DataRightsSubjectDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        if (!this.IsValidScope(
                request.CaseType,
                request.TenantId,
                request.PropertyId) ||
            request.MaxCandidates is <= 0 or >
                DataRightsSubjectDiscoveryLimits.MaxCandidates ||
            !TryGetLookup(
                request.Lookup,
                out Guid? recordId,
                out string? subjectId))
        {
            return DataRightsSubjectDiscoveryResult.ScopeUnavailable();
        }

        if (subjectId is not null &&
            subjectId.StartsWith(
                WorkspaceStaffRetentionCorrelationReceipt.PseudonymPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return DataRightsSubjectDiscoveryResult.ScopeUnavailable();
        }

        string tenantId = request.TenantId.Trim();
        List<DataRightsSubjectCandidate> candidates = [];
        await this.AddOnboardingCandidatesAsync(
            candidates,
            tenantId,
            recordId,
            subjectId,
            request.MaxCandidates,
            cancellationToken).ConfigureAwait(false);
        await this.AddRecoveryReceiptCandidatesAsync(
            candidates,
            tenantId,
            recordId,
            subjectId,
            request.MaxCandidates,
            cancellationToken).ConfigureAwait(false);
        await this.AddAccessProcessCandidatesAsync(
            candidates,
            tenantId,
            recordId,
            subjectId,
            request.MaxCandidates,
            cancellationToken).ConfigureAwait(false);
        await this.AddAccessPlanCandidatesAsync(
            candidates,
            tenantId,
            recordId,
            subjectId,
            request.MaxCandidates,
            cancellationToken).ConfigureAwait(false);
        await this.AddRetentionReceiptCandidatesAsync(
            candidates,
            tenantId,
            recordId,
            request.MaxCandidates,
            cancellationToken).ConfigureAwait(false);

        DataRightsSubjectCandidate[] bounded = candidates
            .GroupBy(candidate => (
                candidate.Coordinate.RecordType,
                candidate.Coordinate.RecordId))
            .Select(group => group.First())
            .OrderBy(
                candidate => candidate.Coordinate.RecordType,
                StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Coordinate.RecordId)
            .Take(request.MaxCandidates)
            .ToArray();
        return DataRightsSubjectDiscoveryResult.Success(bounded);
    }

    public async Task<DataRightsSubjectSelectionValidation>
        ValidateSelectionAsync(
            DataRightsSubjectSelectionRequest request,
            CancellationToken cancellationToken)
    {
        if (!this.IsValidScope(
                request.CaseType,
                request.TenantId,
                request.PropertyId))
        {
            return DataRightsSubjectSelectionValidation.ScopeUnavailable();
        }

        DataRightsSubjectCoordinate? coordinate = request.Coordinate;
        if (coordinate is null ||
            !string.Equals(
                coordinate.OwnerKey,
                WorkspacesDataRightsCoordinates.Owner,
                StringComparison.OrdinalIgnoreCase) ||
            coordinate.RecordId == Guid.Empty ||
            coordinate.RecordVersion <= 0)
        {
            return DataRightsSubjectSelectionValidation.NotFound();
        }

        long? currentVersion = await this.GetVersionAsync(
            request.TenantId.Trim(),
            coordinate.RecordType,
            coordinate.RecordId,
            cancellationToken).ConfigureAwait(false);
        if (!currentVersion.HasValue)
        {
            return DataRightsSubjectSelectionValidation.NotFound();
        }

        if (currentVersion.Value != coordinate.RecordVersion)
        {
            return DataRightsSubjectSelectionValidation.Stale();
        }

        return DataRightsSubjectSelectionValidation.Valid(
            new DataRightsSubjectCoordinate(
                WorkspacesDataRightsCoordinates.Owner,
                coordinate.RecordType.Trim().ToLowerInvariant(),
                coordinate.RecordId,
                currentVersion.Value));
    }

    private async Task AddOnboardingCandidatesAsync(
        List<DataRightsSubjectCandidate> candidates,
        string tenantId,
        Guid? recordId,
        string? subjectId,
        int limit,
        CancellationToken cancellationToken)
    {
        IQueryable<WorkspaceStaffOnboarding> query =
            dbContext.StaffOnboardingApplications
                .AsNoTracking()
                .Where(application => application.ScopeId == tenantId);
        query = recordId.HasValue
            ? query.Where(application =>
                application.Id == recordId.Value ||
                application.StaffMemberId == recordId.Value)
            : query.Where(application =>
                application.SubjectId == subjectId);

        WorkspaceStaffOnboardingCandidate[] records = await query
            .OrderBy(application => application.Id)
            .Take(limit)
            .Select(application =>
                new WorkspaceStaffOnboardingCandidate(
                    application.Id,
                    application.Version,
                    application.Status))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (WorkspaceStaffOnboardingCandidate record in records)
        {
            candidates.Add(new DataRightsSubjectCandidate(
                new DataRightsSubjectCoordinate(
                    WorkspacesDataRightsCoordinates.Owner,
                    WorkspacesDataRightsCoordinates
                        .StaffOnboardingRecordType,
                    record.Id,
                    record.Version),
                $"Workspace onboarding: {record.Status}",
                EmailHint: null,
                PhoneHint: null));
        }
    }

    private async Task AddAccessProcessCandidatesAsync(
        List<DataRightsSubjectCandidate> candidates,
        string tenantId,
        Guid? recordId,
        string? subjectId,
        int limit,
        CancellationToken cancellationToken)
    {
        IQueryable<WorkspaceStaffAccessProcess> query =
            dbContext.StaffAccessProcesses
                .AsNoTracking()
                .Where(process => process.ScopeId == tenantId);
        query = recordId.HasValue
            ? query.Where(process =>
                process.Id == recordId.Value ||
                process.StaffMemberId == recordId.Value)
            : query.Where(process =>
                process.SubjectId == subjectId ||
                process.RequestedBy == subjectId);

        WorkspaceStaffAccessProcessCandidate[] records = await query
            .OrderBy(process => process.Id)
            .Take(limit)
            .Select(process =>
                new WorkspaceStaffAccessProcessCandidate(
                    process.Id,
                    process.Version,
                    process.TargetState,
                    process.EffectiveOn))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (WorkspaceStaffAccessProcessCandidate record in records)
        {
            candidates.Add(new DataRightsSubjectCandidate(
                new DataRightsSubjectCoordinate(
                    WorkspacesDataRightsCoordinates.Owner,
                    WorkspacesDataRightsCoordinates
                        .StaffAccessProcessRecordType,
                    record.Id,
                    record.Version),
                $"Workspace access: {record.TargetState} on " +
                $"{record.EffectiveOn:yyyy-MM-dd}",
                EmailHint: null,
                PhoneHint: null));
        }
    }

    private async Task AddRecoveryReceiptCandidatesAsync(
        List<DataRightsSubjectCandidate> candidates,
        string tenantId,
        Guid? recordId,
        string? subjectId,
        int limit,
        CancellationToken cancellationToken)
    {
        IQueryable<WorkspaceStaffOnboardingRecoveryReceipt> query =
            dbContext.StaffOnboardingRecoveryReceipts
                .AsNoTracking()
                .Where(receipt => receipt.ScopeId == tenantId);
        query = recordId.HasValue
            ? query.Where(receipt =>
                receipt.Id == recordId.Value ||
                receipt.OperationId == recordId.Value ||
                receipt.ApplicationId == recordId.Value ||
                receipt.StaffMemberId == recordId.Value)
            : query.Where(receipt =>
                receipt.ApplicantSubjectId == subjectId ||
                receipt.PlanCreatorActorId == subjectId ||
                receipt.RecoveryActorId == subjectId);
        WorkspaceStaffOnboardingRecoveryCandidate[] records = await query
            .OrderBy(receipt => receipt.RecordedAtUtc)
            .ThenBy(receipt => receipt.Id)
            .Take(limit)
            .Select(receipt => new WorkspaceStaffOnboardingRecoveryCandidate(
                receipt.Id,
                receipt.ContractVersion,
                receipt.Outcome,
                receipt.RecordedAtUtc))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (WorkspaceStaffOnboardingRecoveryCandidate record in records)
        {
            candidates.Add(new DataRightsSubjectCandidate(
                new DataRightsSubjectCoordinate(
                    WorkspacesDataRightsCoordinates.Owner,
                    WorkspacesDataRightsCoordinates
                        .StaffOnboardingRecoveryReceiptRecordType,
                    record.Id,
                    record.ContractVersion),
                $"Workspace onboarding recovery: {record.Outcome} on " +
                $"{record.RecordedAtUtc:yyyy-MM-dd}",
                EmailHint: null,
                PhoneHint: null));
        }
    }

    private async Task AddAccessPlanCandidatesAsync(
        List<DataRightsSubjectCandidate> candidates,
        string tenantId,
        Guid? recordId,
        string? subjectId,
        int limit,
        CancellationToken cancellationToken)
    {
        IQueryable<WorkspaceStaffAccessPlan> query =
            dbContext.StaffAccessPlans
                .AsNoTracking()
                .Where(plan => plan.ScopeId == tenantId);
        query = recordId.HasValue
            ? query.Where(plan => plan.Id == recordId.Value)
            : query.Where(plan =>
                plan.CreatedBySubjectId == subjectId);

        WorkspaceStaffAccessPlanCandidate[] records = await query
            .OrderBy(plan => plan.Id)
            .Take(limit)
            .Select(plan =>
                new WorkspaceStaffAccessPlanCandidate(
                    plan.Id,
                    plan.Version,
                    plan.Status))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (WorkspaceStaffAccessPlanCandidate record in records)
        {
            candidates.Add(new DataRightsSubjectCandidate(
                new DataRightsSubjectCoordinate(
                    WorkspacesDataRightsCoordinates.Owner,
                    WorkspacesDataRightsCoordinates
                        .StaffAccessPlanRecordType,
                    record.Id,
                    record.Version),
                $"Workspace access plan: {record.Status}",
                EmailHint: null,
                PhoneHint: null));
        }
    }

    private async Task AddRetentionReceiptCandidatesAsync(
        List<DataRightsSubjectCandidate> candidates,
        string tenantId,
        Guid? recordId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!recordId.HasValue)
        {
            return;
        }

        WorkspaceStaffRetentionCorrelationCandidate[] records =
            await dbContext.StaffRetentionCorrelationReceipts
                .AsNoTracking()
                .Where(receipt =>
                    receipt.ScopeId == tenantId &&
                    (receipt.Id == recordId.Value ||
                     receipt.StaffMemberId == recordId.Value))
                .OrderBy(receipt => receipt.Id)
                .Take(limit)
                .Select(receipt =>
                    new WorkspaceStaffRetentionCorrelationCandidate(
                        receipt.Id,
                        receipt.ContractVersion,
                        receipt.CompletedAtUtc))
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        foreach (WorkspaceStaffRetentionCorrelationCandidate record in records)
        {
            candidates.Add(new DataRightsSubjectCandidate(
                new DataRightsSubjectCoordinate(
                    WorkspacesDataRightsCoordinates.Owner,
                    WorkspacesDataRightsCoordinates
                        .StaffRetentionCorrelationReceiptRecordType,
                    record.Id,
                    record.ContractVersion),
                "Workspace retention proof: " +
                $"{record.CompletedAtUtc:yyyy-MM-dd}",
                EmailHint: null,
                PhoneHint: null));
        }
    }

    private Task<long?> GetVersionAsync(
        string tenantId,
        string recordType,
        Guid recordId,
        CancellationToken cancellationToken) =>
        recordType?.Trim().ToLowerInvariant() switch
        {
            WorkspacesDataRightsCoordinates.StaffOnboardingRecordType =>
                dbContext.StaffOnboardingApplications
                    .AsNoTracking()
                    .Where(record =>
                        record.ScopeId == tenantId &&
                        record.Id == recordId)
                    .Select(record => (long?)record.Version)
                    .SingleOrDefaultAsync(cancellationToken),
            WorkspacesDataRightsCoordinates.StaffAccessProcessRecordType =>
                dbContext.StaffAccessProcesses
                    .AsNoTracking()
                    .Where(record =>
                        record.ScopeId == tenantId &&
                        record.Id == recordId)
                    .Select(record => (long?)record.Version)
                    .SingleOrDefaultAsync(cancellationToken),
            WorkspacesDataRightsCoordinates
                .StaffOnboardingRecoveryReceiptRecordType =>
                dbContext.StaffOnboardingRecoveryReceipts
                    .AsNoTracking()
                    .Where(record =>
                        record.ScopeId == tenantId &&
                        record.Id == recordId)
                    .Select(record => (long?)record.ContractVersion)
                    .SingleOrDefaultAsync(cancellationToken),
            WorkspacesDataRightsCoordinates.StaffAccessPlanRecordType =>
                dbContext.StaffAccessPlans
                    .AsNoTracking()
                    .Where(record =>
                        record.ScopeId == tenantId &&
                        record.Id == recordId)
                    .Select(record => (long?)record.Version)
                    .SingleOrDefaultAsync(cancellationToken),
            WorkspacesDataRightsCoordinates
                .StaffRetentionCorrelationReceiptRecordType =>
                dbContext.StaffRetentionCorrelationReceipts
                    .AsNoTracking()
                    .Where(record =>
                        record.ScopeId == tenantId &&
                        record.Id == recordId)
                    .Select(record => (long?)record.ContractVersion)
                    .SingleOrDefaultAsync(cancellationToken),
            _ => Task.FromResult<long?>(null)
        };

    private bool IsValidScope(
        DataRightsCaseType caseType,
        string tenantId,
        Guid? propertyId) =>
        caseType == DataRightsCaseType.StaffRights &&
        scopeContext.IsEnabled &&
        !string.IsNullOrWhiteSpace(scopeContext.ScopeId) &&
        string.Equals(
            scopeContext.ScopeId,
            tenantId?.Trim(),
            StringComparison.Ordinal) &&
        !propertyId.HasValue;

    private static bool TryGetLookup(
        DataRightsSubjectLookup? lookup,
        out Guid? recordId,
        out string? subjectId)
    {
        recordId = null;
        subjectId = null;
        if (lookup is null ||
            !string.IsNullOrWhiteSpace(lookup.Email) ||
            !string.IsNullOrWhiteSpace(lookup.Phone) ||
            !string.IsNullOrWhiteSpace(lookup.Name) ||
            lookup.DateOfBirth.HasValue)
        {
            return false;
        }

        bool hasRecordId =
            lookup.RecordId.HasValue &&
            lookup.RecordId.Value != Guid.Empty;
        bool hasSubject =
            !string.IsNullOrWhiteSpace(lookup.AccountSubjectId);
        if (hasRecordId == hasSubject)
        {
            return false;
        }

        recordId = hasRecordId ? lookup.RecordId : null;
        subjectId = hasSubject
            ? lookup.AccountSubjectId!.Trim()
            : null;
        return subjectId is null ||
            subjectId.Length <=
                DataRightsSubjectDiscoveryLimits.AccountSubjectIdMaxLength;
    }

    private sealed record WorkspaceStaffOnboardingCandidate(
        Guid Id,
        long Version,
        WorkspaceStaffOnboardingState Status);

    private sealed record WorkspaceStaffOnboardingRecoveryCandidate(
        Guid Id,
        long ContractVersion,
        WorkspaceStaffOnboardingRecoveryOutcome Outcome,
        DateTimeOffset RecordedAtUtc);

    private sealed record WorkspaceStaffAccessProcessCandidate(
        Guid Id,
        long Version,
        WorkspaceStaffAccessTargetState TargetState,
        DateOnly EffectiveOn);

    private sealed record WorkspaceStaffAccessPlanCandidate(
        Guid Id,
        long Version,
        WorkspaceStaffAccessPlanState Status);

    private sealed record WorkspaceStaffRetentionCorrelationCandidate(
        Guid Id,
        int ContractVersion,
        DateTimeOffset CompletedAtUtc);
}
