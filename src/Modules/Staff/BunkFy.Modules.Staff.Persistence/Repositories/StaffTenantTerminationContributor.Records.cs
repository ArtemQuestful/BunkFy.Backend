namespace BunkFy.Modules.Staff.Persistence.Repositories;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.Entities;
using Microsoft.EntityFrameworkCore;

internal sealed partial class StaffTenantTerminationContributor
{
    private async Task<long> ExportRecordsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        CancellationToken cancellationToken)
    {
        long count = 0;
        count = await this.ExportStaffMembersAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportIdentityProvisioningAnchorsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportIdentityProvisioningAnchorResolutionsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportPropertyAssignmentsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportMemberMutationOperationsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportGovernanceRecordsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        return await this.ExportRetentionRecordsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> ExportStaffMembersAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (StaffMember member in dbContext.StaffMembers
            .AsNoTracking()
            .Where(item => item.ScopeId == tenantId)
            .OrderBy(item => item.Id)
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            StaffMemberTenantExport record = new(
                member.ScopeId,
                member.Id,
                new StaffProfileStateTenantExport(
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
                    member.DepartureEffectiveOn,
                    member.AnonymisedAtUtc),
                new StaffChangeAttributionTenantExport(
                    member.CreatedBy,
                    member.LastChangedBy));
            await WriteAsync(
                StaffTenantTerminationMetadata.StaffMemberRecordType,
                member.Id,
                member.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportPropertyAssignmentsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (StaffPropertyAssignment assignment in
            dbContext.PropertyAssignments
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.StaffMemberId)
                .ThenBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            StaffPropertyAssignmentTenantExport record = new(
                assignment.ScopeId,
                assignment.PropertyId,
                assignment.StaffMemberId,
                assignment.Id,
                new StaffPropertyAssignmentStateTenantExport(
                    assignment.PropertyJobTitle,
                    assignment.IsPrimary,
                    assignment.IsCurrent,
                    assignment.EffectiveFrom,
                    assignment.EffectiveTo,
                    assignment.AssignedAtVersion,
                    assignment.UnassignmentReason,
                    assignment.UnassignedAtVersion),
                new StaffAssignmentAttributionTenantExport(
                    assignment.AssignedBy,
                    assignment.AssignedAtUtc,
                    assignment.UnassignedBy,
                    assignment.UnassignedAtUtc));
            await WriteAsync(
                StaffTenantTerminationMetadata.PropertyAssignmentRecordType,
                assignment.Id,
                assignment.UnassignedAtVersion ??
                    assignment.AssignedAtVersion,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private static ValueTask WriteAsync(
        string recordType,
        Guid recordId,
        long recordVersion,
        object source,
        IDataRightsExportSink sink,
        CancellationToken cancellationToken) =>
        sink.WriteAsync(
            StaffTenantTerminationExportSchema.CreateRecord(
                recordType,
                recordId,
                recordVersion,
                source),
            cancellationToken);
}
