namespace BunkFy.Modules.Staff.Persistence.Repositories;

using System.Globalization;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Governance;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;

internal sealed class StaffDataRightsExportContributor(
    StaffDbContext dbContext,
    IScopeContext scopeContext) : IDataRightsSubjectExportContributor
{
    public const int MaximumAssignmentRecords = 1_000;
    public const int MaximumHoldRecords =
        StaffDataHold.MaximumRecordsPerStaffMember;
    public const int MaximumMemberMutationOperationRecords = 10_000;
    public const int MaximumIdentityProvisioningAnchorRecords = 10_000;
    public const int MaximumIdentityProvisioningAnchorResolutionRecords =
        10_000;
    public const string AssignmentRecordType = "staff-property-assignment";
    public const string EmploymentGovernanceRecordType =
        "staff-employment-governance";
    public const string DataHoldRecordType = "staff-data-hold";
    public const string MemberMutationOperationRecordType =
        "staff-member-mutation-operation";
    public const string IdentityProvisioningAnchorRecordType =
        "staff-identity-provisioning-anchor";
    public const string IdentityProvisioningAnchorResolutionRecordType =
        "staff-identity-provisioning-anchor-resolution";

    public string OwnerKey => StaffDataRightsDiscoveryContributor.Owner;

    public IReadOnlyCollection<DataRightsCaseType> SupportedCaseTypes { get; } =
        [DataRightsCaseType.StaffRights];

    public DataRightsExportDescriptor Descriptor => StaffDataRightsExportSchema.Descriptor;

    public async Task<DataRightsSubjectExportResult> ExportAsync(
        DataRightsSubjectExportRequest request,
        IDataRightsExportSink sink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);

        if (!this.IsValidScope(request.CaseType, request.TenantId, request.PropertyId))
        {
            return DataRightsSubjectExportResult.ScopeUnavailable();
        }

        if (request.Coordinate is not { } coordinate ||
            !string.Equals(
                coordinate.OwnerKey,
                StaffDataRightsDiscoveryContributor.Owner,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                coordinate.RecordType,
                StaffDataRightsDiscoveryContributor.ProfileRecordType,
                StringComparison.OrdinalIgnoreCase) ||
            coordinate.RecordId == Guid.Empty ||
            coordinate.RecordVersion <= 0)
        {
            return DataRightsSubjectExportResult.NotFound();
        }

        StaffExportSnapshot? snapshot = await dbContext.StaffMembers
            .AsNoTracking()
            .Where(member => member.Id == coordinate.RecordId)
            .Select(member => new StaffExportSnapshot(
                new StaffProfileDataRightsExport(
                    member.Id,
                    member.DisplayName,
                    member.LegalName,
                    member.WorkEmail,
                    member.WorkPhone,
                    member.EmployeeNumber,
                    member.JobTitle,
                    member.Department,
                    member.AuthSubjectId,
                    member.Status,
                    member.Version,
                    member.CreatedAtUtc,
                    member.LastChangedAtUtc,
                    member.SuspendedAtUtc,
                    member.DepartedAtUtc,
                    member.DepartureEffectiveOn),
                member.Assignments
                    .OrderBy(assignment => assignment.Id)
                    .Select(assignment => new StaffAssignmentDataRightsExport(
                        assignment.Id,
                        assignment.StaffMemberId,
                        assignment.PropertyId,
                        assignment.PropertyJobTitle,
                        assignment.IsPrimary,
                        assignment.IsCurrent,
                        assignment.EffectiveFrom,
                        assignment.EffectiveTo,
                        assignment.AssignedAtUtc,
                        assignment.AssignedAtVersion,
                        assignment.UnassignedAtUtc,
                        assignment.UnassignedAtVersion))
                    .Take(MaximumAssignmentRecords + 1)
                    .ToArray()))
            .AsSingleQuery()
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return DataRightsSubjectExportResult.NotFound();
        }

        if (snapshot.Profile.Version != coordinate.RecordVersion)
        {
            return DataRightsSubjectExportResult.Stale();
        }

        if (snapshot.Assignments.Length > MaximumAssignmentRecords)
        {
            return DataRightsSubjectExportResult.ScopeUnavailable();
        }

        StaffEmploymentGovernance? governanceState =
            await dbContext.EmploymentGovernance
                .AsNoTracking()
                .Include(item => item.AcceptedAcknowledgements)
                .Where(item =>
                    item.Id == coordinate.RecordId)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        StaffEmploymentGovernanceDataRightsExport? governance =
            governanceState is null
                ? null
                : new StaffEmploymentGovernanceDataRightsExport(
                    governanceState.Id,
                    governanceState.GovernanceContractVersion,
                    governanceState.SelectedStaffVersion,
                    governanceState.Binding.OperatingCountryCode,
                    governanceState.Binding.PolicyId,
                    governanceState.Binding.PolicyVersion,
                    governanceState.Binding.DataRegionId,
                    governanceState.Binding.TransferProfileId,
                    governanceState.Binding.RetentionPolicyId,
                    governanceState.Binding.RetentionPolicyVersion,
                    governanceState.Binding.ContentSha256,
                    governanceState.Binding.PolicyEffectiveAtUtc,
                    governanceState.Binding.PolicyExpiresAtUtc,
                    governanceState.Binding.EvaluatedAtUtc,
                    governanceState.AcceptedAcknowledgements
                        .OrderBy(
                            acknowledgement =>
                                acknowledgement.AcknowledgementId,
                            StringComparer.Ordinal)
                        .ThenBy(
                            acknowledgement =>
                                acknowledgement
                                    .AcknowledgementVersion)
                        .Select(
                            acknowledgement =>
                                string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"{acknowledgement.AcknowledgementId}:" +
                                    $"{acknowledgement.AcknowledgementVersion}"))
                        .ToArray(),
                    governanceState.ConfiguredAtUtc,
                    governanceState.Version);
        StaffDataHoldDataRightsExport[] dataHolds =
            await dbContext.DataHolds
                .AsNoTracking()
                .Where(hold =>
                    hold.StaffMemberId == coordinate.RecordId)
                .OrderBy(hold => hold.PlacedAtUtc)
                .ThenBy(hold => hold.Id)
                .Take(MaximumHoldRecords + 1)
                .Select(hold =>
                    new StaffDataHoldDataRightsExport(
                        hold.Id,
                        hold.StaffMemberId,
                        hold.ReasonCode,
                        hold.State,
                        hold.PlacedAtUtc,
                        hold.ReleasedAtUtc,
                        hold.Version))
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        if (dataHolds.Length > MaximumHoldRecords)
        {
            return DataRightsSubjectExportResult.ScopeUnavailable();
        }

        StaffMemberMutationOperationDataRightsExport[] memberMutationOperations =
            await dbContext.MemberMutationOperations
                .AsNoTracking()
                .Where(operation =>
                    operation.StaffMemberId == coordinate.RecordId)
                .OrderBy(operation => operation.CompletedAtUtc)
                .ThenBy(operation => operation.Id)
                .Take(MaximumMemberMutationOperationRecords + 1)
                .Select(operation =>
                    new StaffMemberMutationOperationDataRightsExport(
                        operation.Id,
                        operation.ScopeId,
                        operation.StaffMemberId,
                        operation.Kind,
                        operation.ExpectedVersion,
                        operation.RequestFingerprint,
                        operation.ResultStatus,
                        operation.ResultVersion,
                        operation.CompletedAtUtc))
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        if (memberMutationOperations.Length >
            MaximumMemberMutationOperationRecords)
        {
            return DataRightsSubjectExportResult.ScopeUnavailable();
        }

        StaffIdentityProvisioningAnchorDataRightsExport[]
            identityProvisioningAnchors = await dbContext
                .IdentityProvisioningAnchors
                .AsNoTracking()
                .Where(anchor =>
                    anchor.StaffMemberId == coordinate.RecordId)
                .OrderBy(anchor => anchor.SourceKind)
                .ThenBy(anchor => anchor.SourceId)
                .Take(MaximumIdentityProvisioningAnchorRecords + 1)
                .Select(anchor =>
                    new StaffIdentityProvisioningAnchorDataRightsExport(
                        anchor.StaffMemberId,
                        anchor.SourceKind,
                        anchor.SourceId,
                        anchor.ResolutionEventId,
                        anchor.AnchoredAtUtc))
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        if (identityProvisioningAnchors.Length >
            MaximumIdentityProvisioningAnchorRecords)
        {
            return DataRightsSubjectExportResult.ScopeUnavailable();
        }

        StaffIdentityProvisioningAnchorResolutionDataRightsExport[]
            identityProvisioningAnchorResolutions = await dbContext
                .IdentityProvisioningAnchorResolutions
                .AsNoTracking()
                .Where(resolution =>
                    resolution.StaffMemberId == coordinate.RecordId)
                .OrderBy(resolution => resolution.SourceKind)
                .ThenBy(resolution => resolution.SourceId)
                .Take(MaximumIdentityProvisioningAnchorResolutionRecords + 1)
                .Select(resolution =>
                    new
                        StaffIdentityProvisioningAnchorResolutionDataRightsExport(
                            resolution.StaffMemberId,
                            resolution.SourceKind,
                            resolution.SourceId,
                            resolution.WorkspaceApplicationVersion,
                            resolution.Disposition,
                            resolution.ResolutionEventId,
                            resolution.ResolvedAtUtc))
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        if (identityProvisioningAnchorResolutions.Length >
            MaximumIdentityProvisioningAnchorResolutionRecords)
        {
            return DataRightsSubjectExportResult.ScopeUnavailable();
        }

        await sink.WriteAsync(
            StaffDataRightsExportSchema.CreateProfileRecord(snapshot.Profile),
            cancellationToken).ConfigureAwait(false);
        int recordCount = 1;

        foreach (StaffIdentityProvisioningAnchorDataRightsExport anchor in
                 identityProvisioningAnchors)
        {
            await sink.WriteAsync(
                StaffDataRightsExportSchema
                    .CreateIdentityProvisioningAnchorRecord(anchor),
                cancellationToken).ConfigureAwait(false);
            recordCount = checked(recordCount + 1);
        }

        foreach (
            StaffIdentityProvisioningAnchorResolutionDataRightsExport
                resolution in identityProvisioningAnchorResolutions)
        {
            await sink.WriteAsync(
                StaffDataRightsExportSchema
                    .CreateIdentityProvisioningAnchorResolutionRecord(
                        resolution),
                cancellationToken).ConfigureAwait(false);
            recordCount = checked(recordCount + 1);
        }

        foreach (StaffMemberMutationOperationDataRightsExport operation in
                 memberMutationOperations)
        {
            await sink.WriteAsync(
                StaffDataRightsExportSchema
                    .CreateMemberMutationOperationRecord(operation),
                cancellationToken).ConfigureAwait(false);
            recordCount = checked(recordCount + 1);
        }

        foreach (StaffAssignmentDataRightsExport assignment in snapshot.Assignments)
        {
            await sink.WriteAsync(
                StaffDataRightsExportSchema.CreateAssignmentRecord(assignment),
                cancellationToken).ConfigureAwait(false);
            recordCount = checked(recordCount + 1);
        }

        if (governance is not null)
        {
            await sink.WriteAsync(
                StaffDataRightsExportSchema
                    .CreateEmploymentGovernanceRecord(governance),
                cancellationToken).ConfigureAwait(false);
            recordCount = checked(recordCount + 1);
        }

        foreach (StaffDataHoldDataRightsExport dataHold in dataHolds)
        {
            await sink.WriteAsync(
                StaffDataRightsExportSchema
                    .CreateDataHoldRecord(dataHold),
                cancellationToken).ConfigureAwait(false);
            recordCount = checked(recordCount + 1);
        }

        return DataRightsSubjectExportResult.Success(recordCount);
    }

    private bool IsValidScope(
        DataRightsCaseType caseType,
        string tenantId,
        Guid? propertyId) =>
        caseType == DataRightsCaseType.StaffRights &&
        scopeContext.IsEnabled &&
        !string.IsNullOrWhiteSpace(scopeContext.ScopeId) &&
        string.Equals(scopeContext.ScopeId, tenantId?.Trim(), StringComparison.Ordinal) &&
        !propertyId.HasValue;

    private sealed record StaffExportSnapshot(
        StaffProfileDataRightsExport Profile,
        StaffAssignmentDataRightsExport[] Assignments);
}
