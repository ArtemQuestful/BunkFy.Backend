namespace BunkFy.Modules.DataRights.Domain.Aggregates;

using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Errors;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Results;

public sealed partial class DataRightsCase
{
    public Result RequireReview(
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc) =>
        this.RequireReview(
            [],
            expectedVersion,
            actorId,
            nowUtc);

    public Result RequireReview(
        IReadOnlyCollection<DataRightsSubjectSelection> requiredSubjects,
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(requiredSubjects);

        Result ready = this.EnsureTransition(
            expectedVersion,
            actorId,
            nowUtc,
            DataRightsCaseState.Discovery);
        if (ready.IsFailure)
        {
            return ready;
        }

        List<DataRightsSubjectCoordinate> additions = [];
        foreach (DataRightsSubjectSelection required in requiredSubjects
            .OrderBy(subject => subject.OwnerKey, StringComparer.Ordinal)
            .ThenBy(subject => subject.RecordType, StringComparer.Ordinal)
            .ThenBy(subject => subject.RecordId))
        {
            Result<DataRightsSubjectCoordinate> coordinate =
                DataRightsSubjectCoordinate.Create(
                    required.OwnerKey,
                    required.RecordType,
                    required.RecordId,
                    required.RecordVersion,
                    actorId,
                    nowUtc);
            if (coordinate.IsFailure)
            {
                return Result.Failure(coordinate.Error);
            }

            DataRightsSubjectCoordinate? selected = this.selectedSubjects
                .Concat(additions)
                .SingleOrDefault(subject =>
                    string.Equals(
                        subject.OwnerKey,
                        coordinate.Value.OwnerKey,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        subject.RecordType,
                        coordinate.Value.RecordType,
                        StringComparison.Ordinal) &&
                    subject.RecordId == coordinate.Value.RecordId);
            if (selected is not null)
            {
                if (selected.RecordVersion !=
                    coordinate.Value.RecordVersion)
                {
                    return Result.Failure(
                        DataRightsDomainErrors.SubjectCoordinateInvalid);
                }

                continue;
            }

            if (this.selectedSubjects.Count + additions.Count >=
                MaxSelectedSubjects)
            {
                return Result.Failure(
                    DataRightsDomainErrors.SubjectSelectionLimitReached);
            }

            additions.Add(coordinate.Value);
        }

        int selectedSubjectCount =
            this.selectedSubjects.Count + additions.Count;
        if (selectedSubjectCount == 0)
        {
            return Result.Failure(DataRightsDomainErrors.SubjectSelectionRequired);
        }

        if ((this.RequestedOperations == DataRightsCaseOperation.Restriction ||
             this.RequestedOperations == DataRightsCaseOperation.Correction) &&
            selectedSubjectCount != 1)
        {
            return Result.Failure(
                this.RequestedOperations == DataRightsCaseOperation.Restriction
                    ? DataRightsDomainErrors.RestrictionExecutionInvalid
                    : DataRightsDomainErrors.CorrectionExecutionInvalid);
        }

        if (this.RequestedOperations == DataRightsCaseOperation.Restriction &&
            this.RestrictionAction == DataRightsRestrictionAction.Release &&
            this.RestrictionTargetingContractVersion ==
                DataRightsRestrictionReleaseTarget.CurrentBindingVersion &&
            (this.RestrictionReleaseTarget is null ||
             !string.Equals(
                 this.RestrictionReleaseTarget.OwnerKey,
                 this.selectedSubjects.Concat(additions).Single().OwnerKey,
                 StringComparison.Ordinal)))
        {
            return Result.Failure(
                DataRightsDomainErrors.RestrictionReleaseTargetRequired);
        }

        this.selectedSubjects.AddRange(additions);
        this.Status = DataRightsCaseState.ReviewRequired;
        this.CompleteChange(actorId, nowUtc);
        return Result.Success();
    }

    public Result SelectSubject(
        string ownerKey,
        string recordType,
        Guid recordId,
        long recordVersion,
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        Result ready = this.EnsureTransition(
            expectedVersion,
            actorId,
            nowUtc,
            DataRightsCaseState.Discovery);
        if (ready.IsFailure)
        {
            return ready;
        }

        Result<DataRightsSubjectCoordinate> coordinate = DataRightsSubjectCoordinate.Create(
            ownerKey,
            recordType,
            recordId,
            recordVersion,
            actorId,
            nowUtc);
        if (coordinate.IsFailure)
        {
            return Result.Failure(coordinate.Error);
        }

        if (this.selectedSubjects.Any(selected =>
            string.Equals(selected.OwnerKey, coordinate.Value.OwnerKey, StringComparison.Ordinal) &&
            string.Equals(selected.RecordType, coordinate.Value.RecordType, StringComparison.Ordinal) &&
            selected.RecordId == coordinate.Value.RecordId))
        {
            return Result.Failure(DataRightsDomainErrors.SubjectAlreadySelected);
        }

        if (this.selectedSubjects.Count >= MaxSelectedSubjects)
        {
            return Result.Failure(DataRightsDomainErrors.SubjectSelectionLimitReached);
        }

        this.selectedSubjects.Add(coordinate.Value);
        this.ClearRestrictionReleaseTarget();
        this.CompleteChange(actorId, nowUtc);
        return Result.Success();
    }

    public Result UnselectSubject(
        string ownerKey,
        string recordType,
        Guid recordId,
        long expectedVersion,
        string actorId,
        DateTimeOffset nowUtc)
    {
        Result ready = this.EnsureTransition(
            expectedVersion,
            actorId,
            nowUtc,
            DataRightsCaseState.Discovery);
        if (ready.IsFailure)
        {
            return ready;
        }

        string normalizedOwner = ownerKey?.Trim().ToLowerInvariant() ?? string.Empty;
        string normalizedType = recordType?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedOwner.Length is 0 or > DataRightsSubjectCoordinate.OwnerKeyMaxLength ||
            normalizedType.Length is 0 or > DataRightsSubjectCoordinate.RecordTypeMaxLength ||
            recordId == Guid.Empty)
        {
            return Result.Failure(DataRightsDomainErrors.SubjectCoordinateInvalid);
        }

        DataRightsSubjectCoordinate? selected = this.selectedSubjects.SingleOrDefault(subject =>
            string.Equals(subject.OwnerKey, normalizedOwner, StringComparison.Ordinal) &&
            string.Equals(subject.RecordType, normalizedType, StringComparison.Ordinal) &&
            subject.RecordId == recordId);
        if (selected is null)
        {
            return Result.Failure(DataRightsDomainErrors.SubjectNotSelected);
        }

        this.selectedSubjects.Remove(selected);
        this.ClearRestrictionReleaseTarget();
        this.CompleteChange(actorId, nowUtc);
        return Result.Success();
    }
}
