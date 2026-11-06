namespace BunkFy.Modules.Properties.Application.Handlers;

using BunkFy.Modules.Properties.Application.Mapping;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Entities;
using Gma.Framework.Results;

internal sealed class PropertyMutationOperationJournal(
    IPropertyMutationOperationRepository operations)
{
    public Task<PropertyMutationReplayDecision<PropertyMutationReceiptDto>>
        InspectPropertyAsync(
        Property property,
        Guid operationId,
        PropertyMutationKind kind,
        long expectedVersion,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(property);
        return this.InspectAsync(
            property.Id,
            PropertyMutationResourceKind.Property,
            property.Id,
            operationId,
            kind,
            expectedVersion,
            fingerprint,
            static operation => operation.ToPropertyReceipt(),
            cancellationToken);
    }

    public Task<PropertyMutationReplayDecision<PropertyMutationReceiptDto>>
        InspectPropertyAsync(
        Property property,
        Guid operationId,
        PropertyMutationKind kind,
        long expectedVersion,
        string fingerprint,
        string? compatibleFingerprint,
        string? secondCompatibleFingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(property);
        return this.InspectAsync(
            property.Id,
            PropertyMutationResourceKind.Property,
            property.Id,
            operationId,
            kind,
            expectedVersion,
            fingerprint,
            compatibleFingerprint,
            secondCompatibleFingerprint,
            static operation => operation.ToPropertyReceipt(),
            cancellationToken);
    }

    public Task<PropertyMutationReplayDecision<RoomMutationReceiptDto>>
        InspectRoomAsync(
        Guid propertyId,
        PropertyMutationResourceKind resourceKind,
        Guid resourceId,
        Guid operationId,
        PropertyMutationKind kind,
        long expectedVersion,
        string fingerprint,
        CancellationToken cancellationToken) => this.InspectAsync(
            propertyId,
            resourceKind,
            resourceId,
            operationId,
            kind,
            expectedVersion,
            fingerprint,
            static operation => operation.ToRoomReceipt(),
            cancellationToken);

    public Task<PropertyMutationReplayDecision<BedMutationReceiptDto>>
        InspectBedAsync(
        Guid propertyId,
        Guid roomId,
        Guid operationId,
        PropertyMutationKind kind,
        long expectedVersion,
        string fingerprint,
        CancellationToken cancellationToken) => this.InspectAsync(
            propertyId,
            PropertyMutationResourceKind.Room,
            roomId,
            operationId,
            kind,
            expectedVersion,
            fingerprint,
            static operation => operation.ToBedReceipt(),
            cancellationToken);

    public Task<PropertyMutationReplayDecision<BedBatchMutationReceiptDto>>
        InspectBedBatchAsync(
        Guid propertyId,
        Guid roomId,
        Guid operationId,
        PropertyMutationKind kind,
        long expectedVersion,
        string fingerprint,
        CancellationToken cancellationToken) => this.InspectAsync(
            propertyId,
            PropertyMutationResourceKind.Room,
            roomId,
            operationId,
            kind,
            expectedVersion,
            fingerprint,
            static operation => operation.ToBedBatchReceipt(),
            cancellationToken);

    private Task<PropertyMutationReplayDecision<TReceipt>> InspectAsync<TReceipt>(
        Guid propertyId,
        PropertyMutationResourceKind resourceKind,
        Guid resourceId,
        Guid operationId,
        PropertyMutationKind kind,
        long expectedVersion,
        string fingerprint,
        Func<PropertyMutationOperationRecord, TReceipt> receiptFactory,
        CancellationToken cancellationToken)
        where TReceipt : class => this.InspectAsync(
            propertyId,
            resourceKind,
            resourceId,
            operationId,
            kind,
            expectedVersion,
            fingerprint,
            compatibleFingerprint: null,
            secondCompatibleFingerprint: null,
            receiptFactory,
            cancellationToken);

    private async Task<PropertyMutationReplayDecision<TReceipt>> InspectAsync<TReceipt>(
        Guid propertyId,
        PropertyMutationResourceKind resourceKind,
        Guid resourceId,
        Guid operationId,
        PropertyMutationKind kind,
        long expectedVersion,
        string fingerprint,
        string? compatibleFingerprint,
        string? secondCompatibleFingerprint,
        Func<PropertyMutationOperationRecord, TReceipt> receiptFactory,
        CancellationToken cancellationToken)
        where TReceipt : class
    {
        PropertyMutationOperationRecord? existing =
            await operations.GetAsync(
                resourceKind,
                resourceId,
                operationId,
                cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return PropertyMutationReplayDecision<TReceipt>.Missing;
        }

        bool matches = existing.Matches(
                kind,
                propertyId,
                resourceKind,
                resourceId,
                expectedVersion,
                fingerprint) ||
            (compatibleFingerprint is not null &&
             existing.Matches(
                 kind,
                 propertyId,
                 resourceKind,
                 resourceId,
                 expectedVersion,
                 compatibleFingerprint)) ||
            (secondCompatibleFingerprint is not null &&
             existing.Matches(
                 kind,
                 propertyId,
                 resourceKind,
                 resourceId,
                 expectedVersion,
                 secondCompatibleFingerprint));
        return matches
            ? PropertyMutationReplayDecision<TReceipt>.Exact(
                receiptFactory(existing))
            : PropertyMutationReplayDecision<TReceipt>.Conflict;
    }

    public async Task<PropertyMutationReceiptDto> RecordPropertyAsync(
        Property property,
        Guid operationId,
        PropertyMutationKind kind,
        long expectedVersion,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(property);
        PropertyMutationReceiptDto receipt =
            PropertiesMapper.ToReceipt(property);
        await operations.AddAsync(
            PropertyMutationOperationRecord.ForProperty(
                operationId,
                property.ScopeId,
                property.Id,
                kind,
                expectedVersion,
                fingerprint,
                receipt,
                completedAtUtc),
            cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    public async Task<RoomMutationReceiptDto> RecordRoomAsync(
        Room room,
        PropertyMutationResourceKind resourceKind,
        Guid resourceId,
        Guid operationId,
        PropertyMutationKind kind,
        long expectedVersion,
        string fingerprint,
        long resultResourceVersion,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);
        RoomMutationReceiptDto receipt = PropertiesMapper.ToReceipt(room);
        await operations.AddAsync(
            PropertyMutationOperationRecord.ForRoom(
                operationId,
                room.ScopeId,
                room.PropertyId,
                resourceKind,
                resourceId,
                kind,
                expectedVersion,
                fingerprint,
                receipt,
                resultResourceVersion,
                completedAtUtc),
            cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    public async Task<BedMutationReceiptDto> RecordBedAsync(
        Room room,
        Bed bed,
        Guid operationId,
        PropertyMutationKind kind,
        long expectedVersion,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(bed);
        BedMutationReceiptDto receipt = PropertiesMapper.ToReceipt(
            bed,
            room.Version);
        await operations.AddAsync(
            PropertyMutationOperationRecord.ForBed(
                operationId,
                room.ScopeId,
                room.PropertyId,
                room.Id,
                kind,
                expectedVersion,
                fingerprint,
                receipt,
                completedAtUtc),
            cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    public async Task<BedBatchMutationReceiptDto> RecordBedBatchAsync(
        Room room,
        int affectedBedCount,
        Guid operationId,
        long expectedVersion,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(room);
        BedBatchMutationReceiptDto receipt = PropertiesMapper.ToBatchReceipt(
            room,
            affectedBedCount);
        await operations.AddAsync(
            PropertyMutationOperationRecord.ForBedBatch(
                operationId,
                room.ScopeId,
                room.PropertyId,
                room.Id,
                PropertyMutationKind.BedBatchAdd,
                expectedVersion,
                fingerprint,
                receipt,
                completedAtUtc),
            cancellationToken).ConfigureAwait(false);
        return receipt;
    }
}

internal readonly record struct PropertyMutationReplayDecision<TReceipt>(
    PropertyMutationReplayOutcome Outcome,
    TReceipt? Receipt)
    where TReceipt : class
{
    public static PropertyMutationReplayDecision<TReceipt> Missing => new(
        PropertyMutationReplayOutcome.Missing,
        null);

    public static PropertyMutationReplayDecision<TReceipt> Conflict => new(
        PropertyMutationReplayOutcome.Conflict,
        null);

    public static PropertyMutationReplayDecision<TReceipt> Exact(
        TReceipt receipt) => new(
        PropertyMutationReplayOutcome.Exact,
        receipt);

    public bool Exists => this.Outcome != PropertyMutationReplayOutcome.Missing;

    public Result<TReceipt> ToResult() =>
        this.Outcome switch
        {
            PropertyMutationReplayOutcome.Exact when this.Receipt is not null =>
                Result.Success(this.Receipt),
            PropertyMutationReplayOutcome.Conflict =>
                Result.Failure<TReceipt>(
                    PropertiesApplicationErrors.ManagementOperationConflict),
            _ => throw new InvalidOperationException(
                "A missing property mutation operation has no replay result.")
        };
}

internal enum PropertyMutationReplayOutcome
{
    Missing = 0,
    Exact = 1,
    Conflict = 2
}
