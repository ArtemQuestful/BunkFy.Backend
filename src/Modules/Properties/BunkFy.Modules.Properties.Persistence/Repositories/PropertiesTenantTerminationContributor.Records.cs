namespace BunkFy.Modules.Properties.Persistence.Repositories;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Entities;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

internal sealed partial class PropertiesTenantTerminationContributor
{
    private async Task<long> ExportRecordsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        CancellationToken cancellationToken)
    {
        long count = 0;
        count = await this.ExportPropertiesAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportPropertyMutationOperationsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportPropertyTimeZoneOperationsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportAcknowledgementsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportRoomsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        count = await this.ExportBedsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
        return await this.ExportGovernanceRevisionsAsync(
            tenantId,
            sink,
            count,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> ExportPropertiesAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (PropertyExportRow row in dbContext.Properties
            .AsNoTracking()
            .Where(property => property.ScopeId == tenantId)
            .OrderBy(property => property.Id)
            .Select(property => new PropertyExportRow(
                property.ScopeId,
                property.Id,
                property.Name,
                property.Code,
                property.TimeZoneId,
                property.Status,
                property.ProcessingState,
                property.GovernanceBinding,
                property.Version,
                property.ProjectionOrdinal,
                property.CreatedAtUtc,
                property.UpdatedAtUtc,
                property.RetiredAtUtc))
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            PropertiesGovernancePolicyTenantExport? policy =
                row.GovernanceBinding is null
                    ? null
                    : new(
                        row.GovernanceBinding.OperatingCountryCode,
                        row.GovernanceBinding.PolicyId,
                        row.GovernanceBinding.PolicyVersion,
                        row.GovernanceBinding.DataRegionId,
                        row.GovernanceBinding.TransferProfileId,
                        row.GovernanceBinding.RetentionPolicyId,
                        row.GovernanceBinding.RetentionPolicyVersion,
                        row.GovernanceBinding.ContentSha256,
                        row.GovernanceBinding.PolicyEffectiveAtUtc,
                        row.GovernanceBinding.PolicyExpiresAtUtc,
                        row.GovernanceBinding.ActivatedAtUtc);
            PropertiesPropertyTenantExport record = new(
                row.ScopeId,
                row.PropertyId,
                row.Name.Value,
                row.Code.Value,
                row.TimeZoneId.Value,
                row.Status,
                row.ProcessingStatus,
                policy,
                row.Version,
                row.ProjectionOrdinal,
                row.CreatedAtUtc,
                row.UpdatedAtUtc,
                row.RetiredAtUtc);
            await WriteAsync(
                PropertiesTenantTerminationMetadata.PropertyRecordType,
                row.PropertyId,
                row.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportPropertyMutationOperationsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (PropertyMutationOperation operation in
            dbContext.PropertyMutationOperations
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.PropertyId)
                .ThenBy(item => item.ResourceKind)
                .ThenBy(item => item.ResourceId)
                .ThenBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            PropertiesPropertyMutationOperationTenantExport record = new(
                operation.ScopeId,
                operation.PropertyId,
                operation.Id,
                new PropertiesPropertyMutationOperationStateTenantExport(
                    operation.ResourceKind,
                    operation.ResourceId,
                    operation.Kind,
                    operation.ExpectedVersion,
                    operation.RequestFingerprint,
                    operation.ResultStatus,
                    operation.ResultProcessingStatus,
                    operation.ResultRoomId,
                    operation.ResultRoomStatus,
                    operation.ResultBedId,
                    operation.ResultBedStatus,
                    operation.ResultAffectedBedCount,
                    operation.ResultVersion,
                    operation.ResultResourceVersion,
                    operation.CompletedAtUtc));
            await WriteAsync(
                PropertiesTenantTerminationMetadata
                    .PropertyMutationOperationRecordType,
                DataRightsExportRecordIds.CreateDeterministicChild(
                    operation.PropertyId,
                    $"{(int)operation.ResourceKind}:" +
                    $"{operation.ResourceId:N}:{operation.Id:N}"),
                operation.ResultVersion,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportAcknowledgementsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        IQueryable<GovernanceAcknowledgementExportRow> query =
            from property in dbContext.Properties.AsNoTracking()
            where property.ScopeId == tenantId
            from acknowledgement in property.GovernanceAcknowledgements
            orderby property.Id,
                acknowledgement.AcknowledgementId,
                acknowledgement.AcknowledgementVersion
            select new GovernanceAcknowledgementExportRow(
                property.ScopeId,
                property.Id,
                property.Version,
                acknowledgement.AcknowledgementId,
                acknowledgement.AcknowledgementVersion);
        await foreach (GovernanceAcknowledgementExportRow row in query
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            PropertiesGovernanceAcknowledgementTenantExport record = new(
                row.ScopeId,
                row.PropertyId,
                row.AcknowledgementId,
                row.AcknowledgementVersion);
            await WriteAsync(
                PropertiesTenantTerminationMetadata
                    .GovernanceAcknowledgementRecordType,
                DataRightsExportRecordIds.CreateDeterministicChild(
                    row.PropertyId,
                    $"{row.AcknowledgementId}|" +
                    row.AcknowledgementVersion),
                row.PropertyVersion,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportPropertyTimeZoneOperationsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (PropertyTimeZoneOperation operation in
            dbContext.PropertyTimeZoneOperations
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.PropertyId)
                .ThenBy(item => item.OperationId)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            PropertiesPropertyTimeZoneOperationTenantExport record = new(
                operation.ScopeId,
                operation.PropertyId,
                operation.OperationId,
                new PropertiesPropertyTimeZoneOperationStateTenantExport(
                    operation.RevisionId,
                    operation.ChangeKind,
                    operation.RequestedTimeZoneId,
                    operation.PreviousTimeZoneId,
                    operation.TimeZoneId,
                    operation.CatalogVersion,
                    operation.ExpectedVersion,
                    operation.ResultVersion),
                operation.ActorId,
                operation.OccurredAtUtc);
            await WriteAsync(
                PropertiesTenantTerminationMetadata
                    .PropertyTimeZoneOperationRecordType,
                DataRightsExportRecordIds.CreateDeterministicChild(
                    operation.PropertyId,
                    $"time-zone:{operation.OperationId:N}"),
                operation.ResultVersion,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportRoomsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (RoomExportRow row in dbContext.Rooms
            .AsNoTracking()
            .Where(room => room.ScopeId == tenantId)
            .OrderBy(room => room.Id)
            .Select(room => new RoomExportRow(
                room.ScopeId,
                room.PropertyId,
                room.Id,
                room.Name,
                room.BuildingLabel,
                room.FloorLabel,
                room.Status,
                room.Version,
                room.CreatedAtUtc,
                room.UpdatedAtUtc,
                room.RetiredAtUtc))
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            PropertiesRoomTenantExport record = new(
                row.ScopeId,
                row.PropertyId,
                row.RoomId,
                row.Name.Value,
                row.BuildingLabel?.Value,
                row.FloorLabel?.Value,
                row.Status,
                row.Version,
                row.CreatedAtUtc,
                row.UpdatedAtUtc,
                row.RetiredAtUtc);
            await WriteAsync(
                PropertiesTenantTerminationMetadata.RoomRecordType,
                row.RoomId,
                row.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportBedsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        IQueryable<BedExportRow> query =
            from room in dbContext.Rooms.AsNoTracking()
            where room.ScopeId == tenantId
            from bed in room.Beds
            orderby room.Id, bed.Id
            select new BedExportRow(
                bed.ScopeId,
                bed.PropertyId,
                bed.RoomId,
                bed.Id,
                bed.Label,
                bed.Status,
                bed.Version,
                bed.CreatedAtUtc,
                bed.UpdatedAtUtc,
                bed.RetiredAtUtc);
        await foreach (BedExportRow row in query
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            PropertiesBedTenantExport record = new(
                row.ScopeId,
                row.PropertyId,
                row.RoomId,
                row.BedId,
                row.Label.Value,
                row.Status,
                row.Version,
                row.CreatedAtUtc,
                row.UpdatedAtUtc,
                row.RetiredAtUtc);
            await WriteAsync(
                PropertiesTenantTerminationMetadata.BedRecordType,
                row.BedId,
                row.Version,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private async Task<long> ExportGovernanceRevisionsAsync(
        string tenantId,
        IDataRightsExportSink sink,
        long count,
        CancellationToken cancellationToken)
    {
        await foreach (PropertyGovernanceRevision revision in
            dbContext.GovernanceRevisions
                .AsNoTracking()
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.Id)
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false))
        {
            PropertiesGovernanceRevisionTenantExport record = new(
                revision.ScopeId,
                revision.Id,
                revision.PropertyId,
                revision.PropertyVersion,
                revision.Action,
                revision.DecisionReasonCode,
                Map(revision.Previous),
                Map(revision.Current),
                revision.ActorId,
                revision.OccurredAtUtc);
            await WriteAsync(
                PropertiesTenantTerminationMetadata
                    .GovernanceRevisionRecordType,
                revision.Id,
                revision.PropertyVersion,
                record,
                sink,
                cancellationToken).ConfigureAwait(false);
            count = checked(count + 1);
        }

        return count;
    }

    private static PropertiesGovernanceCoordinatesTenantExport? Map(
        PropertyGovernanceRevisionCoordinatesRecord? coordinates) =>
        coordinates is null
            ? null
            : new(
                coordinates.OperatingCountryCode,
                coordinates.PolicyId,
                coordinates.PolicyVersion,
                coordinates.DataRegionId,
                coordinates.TransferProfileId,
                coordinates.RetentionPolicyId,
                coordinates.RetentionPolicyVersion,
                coordinates.ContentSha256,
                coordinates.AcknowledgementSetSha256);

    private static ValueTask WriteAsync(
        string recordType,
        Guid recordId,
        long recordVersion,
        object source,
        IDataRightsExportSink sink,
        CancellationToken cancellationToken) =>
        sink.WriteAsync(
            PropertiesTenantTerminationExportSchema.CreateRecord(
                recordType,
                recordId,
                recordVersion,
                source),
            cancellationToken);

    private sealed record PropertyExportRow(
        string ScopeId,
        Guid PropertyId,
        PropertyName Name,
        PropertyCode Code,
        PropertyTimeZoneId TimeZoneId,
        PropertyState Status,
        PropertyProcessingState ProcessingStatus,
        PropertyGovernanceBinding? GovernanceBinding,
        long Version,
        long ProjectionOrdinal,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? UpdatedAtUtc,
        DateTimeOffset? RetiredAtUtc);

    private sealed record GovernanceAcknowledgementExportRow(
        string ScopeId,
        Guid PropertyId,
        long PropertyVersion,
        string AcknowledgementId,
        int AcknowledgementVersion);

    private sealed record RoomExportRow(
        string ScopeId,
        Guid PropertyId,
        Guid RoomId,
        RoomName Name,
        PhysicalLabel? BuildingLabel,
        PhysicalLabel? FloorLabel,
        RoomState Status,
        long Version,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? UpdatedAtUtc,
        DateTimeOffset? RetiredAtUtc);

    private sealed record BedExportRow(
        string ScopeId,
        Guid PropertyId,
        Guid RoomId,
        Guid BedId,
        BedLabel Label,
        BedState Status,
        long Version,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? UpdatedAtUtc,
        DateTimeOffset? RetiredAtUtc);
}
