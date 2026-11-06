namespace BunkFy.Modules.DataRights.Domain.ValueObjects;

using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Errors;
using Gma.Framework.Results;

public sealed class DataRightsRestrictionReleaseTarget
{
    public const int CurrentBindingVersion = 1;

    private DataRightsRestrictionReleaseTarget() { }

    private DataRightsRestrictionReleaseTarget(
        string ownerKey,
        Guid ownerOperationId,
        long ownerOperationVersion,
        string selectedBy,
        DateTimeOffset selectedAtUtc)
    {
        this.OwnerKey = ownerKey;
        this.OwnerOperationId = ownerOperationId;
        this.OwnerOperationVersion = ownerOperationVersion;
        this.SelectedBy = selectedBy;
        this.SelectedAtUtc = selectedAtUtc;
    }

    public string OwnerKey { get; private set; } = string.Empty;
    public Guid OwnerOperationId { get; private set; }
    public long OwnerOperationVersion { get; private set; }
    public string SelectedBy { get; private set; } = string.Empty;
    public DateTimeOffset SelectedAtUtc { get; private set; }

    public static Result<DataRightsRestrictionReleaseTarget> Create(
        string ownerKey,
        Guid ownerOperationId,
        long ownerOperationVersion,
        string selectedBy,
        DateTimeOffset selectedAtUtc)
    {
        string owner = ownerKey?.Trim().ToLowerInvariant() ?? string.Empty;
        string actor = selectedBy?.Trim() ?? string.Empty;
        if (owner.Length is 0 or > DataRightsSubjectCoordinate.OwnerKeyMaxLength ||
            ownerOperationId == Guid.Empty ||
            ownerOperationVersion is < 1 or long.MaxValue ||
            actor.Length is 0 or > DataRightsCase.ActorIdMaxLength ||
            selectedAtUtc == default)
        {
            return Result.Failure<DataRightsRestrictionReleaseTarget>(
                DataRightsDomainErrors.RestrictionReleaseTargetInvalid);
        }

        return Result.Success(new DataRightsRestrictionReleaseTarget(
            owner,
            ownerOperationId,
            ownerOperationVersion,
            actor,
            selectedAtUtc));
    }

    public bool Matches(
        string ownerKey,
        Guid ownerOperationId,
        long ownerOperationVersion) =>
        string.Equals(
            this.OwnerKey,
            ownerKey?.Trim(),
            StringComparison.OrdinalIgnoreCase) &&
        this.OwnerOperationId == ownerOperationId &&
        this.OwnerOperationVersion == ownerOperationVersion;
}
