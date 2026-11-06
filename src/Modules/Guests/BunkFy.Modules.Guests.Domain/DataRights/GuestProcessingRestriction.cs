namespace BunkFy.Modules.Guests.Domain.DataRights;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.Errors;
using BunkFy.Modules.Guests.Domain.Models;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class GuestProcessingRestriction : ScopedAggregateRoot<Guid>
{
    private GuestProcessingRestriction() { }

    private GuestProcessingRestriction(Guid id, string scopeId)
        : base(id, scopeId)
    {
    }

    public Guid PropertyId { get; private set; }
    public Guid GuestId { get; private set; }
    public Guid ApplyCaseId { get; private set; }
    public long ApplyApprovalRevision { get; private set; }
    public long ApplySelectedGuestVersion { get; private set; }
    public GuestProcessingRestrictionState Status { get; private set; }
    public long Version { get; private set; } = 1;
    public string AppliedBy { get; private set; } = string.Empty;
    public DateTimeOffset AppliedAtUtc { get; private set; }
    public Guid? ReleaseCaseId { get; private set; }
    public long? ReleaseApprovalRevision { get; private set; }
    public long? ReleaseSelectedGuestVersion { get; private set; }
    public string? ReleasedBy { get; private set; }
    public DateTimeOffset? ReleasedAtUtc { get; private set; }

    public static Result<GuestProcessingRestriction> Create(
        Guid restrictionId,
        string tenantId,
        Guid propertyId,
        Guid guestId,
        Guid applyCaseId,
        long applyApprovalRevision,
        long applySelectedGuestVersion,
        string actorId,
        DateTimeOffset appliedAtUtc)
    {
        if (restrictionId == Guid.Empty ||
            propertyId == Guid.Empty ||
            guestId == Guid.Empty ||
            applyCaseId == Guid.Empty ||
            !TenantIds.TryNormalize(tenantId, out string? scopeId))
        {
            return Result.Failure<GuestProcessingRestriction>(
                GuestsDomainErrors.RestrictionIdentityInvalid);
        }

        if (applyApprovalRevision < 1 || applySelectedGuestVersion < 1)
        {
            return Result.Failure<GuestProcessingRestriction>(
                GuestsDomainErrors.RestrictionApprovalInvalid);
        }

        string? normalizedActorId = NormalizeActor(actorId);
        if (normalizedActorId is null || appliedAtUtc == default)
        {
            return Result.Failure<GuestProcessingRestriction>(
                GuestsDomainErrors.RestrictionTransitionInvalid);
        }

        return Result.Success(new GuestProcessingRestriction(restrictionId, scopeId)
        {
            PropertyId = propertyId,
            GuestId = guestId,
            ApplyCaseId = applyCaseId,
            ApplyApprovalRevision = applyApprovalRevision,
            ApplySelectedGuestVersion = applySelectedGuestVersion,
            Status = GuestProcessingRestrictionState.Active,
            AppliedBy = normalizedActorId,
            AppliedAtUtc = appliedAtUtc
        });
    }

    public Result Release(
        Guid releaseCaseId,
        long releaseApprovalRevision,
        long releaseSelectedGuestVersion,
        long expectedVersion,
        string actorId,
        DateTimeOffset releasedAtUtc)
    {
        Result validation = this.ValidateRelease(
            releaseCaseId,
            releaseApprovalRevision,
            releaseSelectedGuestVersion,
            expectedVersion,
            actorId,
            releasedAtUtc);
        if (validation.IsFailure)
        {
            return validation;
        }

        this.ReleaseCaseId = releaseCaseId;
        this.ReleaseApprovalRevision = releaseApprovalRevision;
        this.ReleaseSelectedGuestVersion = releaseSelectedGuestVersion;
        this.ReleasedBy = NormalizeActor(actorId)!;
        this.ReleasedAtUtc = releasedAtUtc;
        this.Status = GuestProcessingRestrictionState.Released;
        this.Version++;
        return Result.Success();
    }

    public Result ValidateRelease(
        Guid releaseCaseId,
        long releaseApprovalRevision,
        long releaseSelectedGuestVersion,
        long expectedVersion,
        string actorId,
        DateTimeOffset releasedAtUtc)
    {
        if (expectedVersion != this.Version)
        {
            return Result.Failure(GuestsDomainErrors.RestrictionVersionConflict);
        }

        if (this.Status != GuestProcessingRestrictionState.Active)
        {
            return Result.Failure(GuestsDomainErrors.RestrictionAlreadyReleased);
        }

        if (releaseCaseId == Guid.Empty ||
            releaseCaseId == this.ApplyCaseId ||
            releaseApprovalRevision < 1 ||
            releaseSelectedGuestVersion < 1)
        {
            return Result.Failure(GuestsDomainErrors.RestrictionApprovalInvalid);
        }

        string? normalizedActorId = NormalizeActor(actorId);
        if (normalizedActorId is null ||
            releasedAtUtc == default ||
            releasedAtUtc < this.AppliedAtUtc)
        {
            return Result.Failure(GuestsDomainErrors.RestrictionTransitionInvalid);
        }

        return Result.Success();
    }

    private static string? NormalizeActor(string? actorId)
    {
        string? normalized = actorId?.Trim();
        return normalized is { Length: > 0 } &&
            normalized.Length <= GuestProfile.ActorIdMaxLength
            ? normalized
            : null;
    }
}
