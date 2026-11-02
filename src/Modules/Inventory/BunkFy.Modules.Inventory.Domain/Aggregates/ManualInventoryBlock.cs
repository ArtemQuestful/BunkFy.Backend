namespace BunkFy.Modules.Inventory.Domain.Aggregates;

using Gma.Framework.Domain.Models;
using Gma.Framework.Results;
using BunkFy.Modules.Inventory.Domain.Errors;
using BunkFy.Modules.Inventory.Domain.Events;

public sealed class ManualInventoryBlock : ScopedAggregateRoot<Guid>
{
    public const int ReasonMaxLength = 500;

    private ManualInventoryBlock() { }

    private ManualInventoryBlock(
        Guid blockId,
        Guid blockGroupId,
        string scopeId,
        Guid propertyId,
        Guid inventoryUnitId,
        DateOnly arrival,
        DateOnly departure,
        string reason,
        DateTimeOffset createdAtUtc)
        : base(blockId, scopeId)
    {
        this.BlockGroupId = blockGroupId;
        this.PropertyId = propertyId;
        this.InventoryUnitId = inventoryUnitId;
        this.Arrival = arrival;
        this.Departure = departure;
        this.Reason = reason;
        this.CreatedAtUtc = createdAtUtc;
    }

    public Guid BlockGroupId { get; private set; }
    public Guid PropertyId { get; private set; }
    public Guid InventoryUnitId { get; private set; }
    public DateOnly Arrival { get; private set; }
    public DateOnly Departure { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public ManualInventoryBlockState Status { get; private set; } = ManualInventoryBlockState.Active;
    public long Version { get; private set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? ReleasedAtUtc { get; private set; }

    public static Result<ManualInventoryBlock> Create(
        Guid blockId,
        Guid blockGroupId,
        string scopeId,
        Guid propertyId,
        Guid inventoryUnitId,
        DateOnly arrival,
        DateOnly departure,
        string reason,
        Guid eventId,
        DateTimeOffset nowUtc,
        string? actorId = null)
    {
        if (blockId == Guid.Empty)
        {
            return Result.Failure<ManualInventoryBlock>(InventoryDomainErrors.BlockIdRequired);
        }

        if (blockGroupId == Guid.Empty)
        {
            return Result.Failure<ManualInventoryBlock>(InventoryDomainErrors.BlockGroupIdRequired);
        }

        if (propertyId == Guid.Empty)
        {
            return Result.Failure<ManualInventoryBlock>(InventoryDomainErrors.PropertyIdRequired);
        }

        if (inventoryUnitId == Guid.Empty)
        {
            return Result.Failure<ManualInventoryBlock>(InventoryDomainErrors.InventoryUnitIdRequired);
        }

        if (arrival >= departure)
        {
            return Result.Failure<ManualInventoryBlock>(InventoryDomainErrors.StayRangeInvalid);
        }

        string normalizedReason = reason?.Trim() ?? string.Empty;
        if (normalizedReason.Length is 0 or > ReasonMaxLength ||
            normalizedReason.Any(char.IsControl))
        {
            return Result.Failure<ManualInventoryBlock>(InventoryDomainErrors.BlockReasonInvalid);
        }

        ManualInventoryBlock block = new(
            blockId,
            blockGroupId,
            scopeId,
            propertyId,
            inventoryUnitId,
            arrival,
            departure,
            normalizedReason,
            nowUtc);
        block.RaiseDomainEvent(new ManualInventoryBlockCreatedDomainEvent(
            eventId,
            nowUtc,
            block.ScopeId,
            block.Id,
            block.BlockGroupId,
            block.PropertyId,
            block.InventoryUnitId,
            block.Arrival,
            block.Departure,
            block.Reason,
            block.Version,
            actorId));
        return Result.Success(block);
    }

    public Result Release(long expectedVersion, Guid eventId, DateTimeOffset nowUtc, string? actorId = null)
    {
        if (expectedVersion != this.Version)
        {
            return Result.Failure(InventoryDomainErrors.VersionConflict);
        }

        if (this.Status == ManualInventoryBlockState.Released)
        {
            return Result.Failure(InventoryDomainErrors.BlockAlreadyReleased);
        }

        this.Status = ManualInventoryBlockState.Released;
        this.Version++;
        this.ReleasedAtUtc = nowUtc;
        this.RaiseDomainEvent(new ManualInventoryBlockReleasedDomainEvent(
            eventId,
            nowUtc,
            this.ScopeId,
            this.Id,
            this.BlockGroupId,
            this.PropertyId,
            this.InventoryUnitId,
            this.Version,
            actorId));
        return Result.Success();
    }
}
