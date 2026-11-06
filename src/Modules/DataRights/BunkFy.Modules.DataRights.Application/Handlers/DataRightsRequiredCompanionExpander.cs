namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Results;
using Microsoft.Extensions.Logging;
using SelectedSubject =
    BunkFy.Modules.DataRights.Domain.Entities.DataRightsSubjectCoordinate;

internal sealed class DataRightsRequiredCompanionExpander(
    IEnumerable<IDataRightsRequiredCompanionContributor> contributors,
    IEnumerable<IDataRightsSubjectDiscoveryContributor> discoveryContributors,
    ILogger<DataRightsRequiredCompanionExpander> logger)
{
    public async Task<Result<IReadOnlyCollection<DataRightsSubjectSelection>>>
        ExpandAsync(
            DataRightsCase dataRightsCase,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataRightsCase);

        DataRightsOperation operation =
            dataRightsCase.RequestedOperations switch
            {
                DataRightsCaseOperation.AccessExport =>
                    DataRightsOperation.AccessExport,
                DataRightsCaseOperation.Anonymisation =>
                    DataRightsOperation.Anonymisation,
                _ => DataRightsOperation.None
            };
        if (operation == DataRightsOperation.None)
        {
            return Result.Success<
                IReadOnlyCollection<DataRightsSubjectSelection>>([]);
        }

        Result<IReadOnlyCollection<
            IDataRightsRequiredCompanionContributor>> orderedContributors =
            OrderContributors(
                contributors,
                (DataRightsCaseType)dataRightsCase.Kind,
                operation);
        if (orderedContributors.IsFailure)
        {
            return Result.Failure<
                IReadOnlyCollection<DataRightsSubjectSelection>>(
                    orderedContributors.Error);
        }

        Dictionary<CoordinateKey, DataRightsSubjectCoordinate> closure = [];
        Queue<DataRightsSubjectCoordinate> pending = new(
            dataRightsCase.SelectedSubjects
                .OrderBy(subject => subject.OwnerKey, StringComparer.Ordinal)
                .ThenBy(subject => subject.RecordType, StringComparer.Ordinal)
                .ThenBy(subject => subject.RecordId)
                .Select(ToContract));
        foreach (DataRightsSubjectCoordinate selected in pending)
        {
            closure.Add(CoordinateKey.Create(selected), selected);
        }

        HashSet<CoordinateKey> initialKeys = [.. closure.Keys];
        while (pending.TryDequeue(out DataRightsSubjectCoordinate? source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Result<DataRightsSubjectCoordinate> validatedSource =
                await this.ValidateCoordinateAsync(
                    dataRightsCase,
                    source,
                    cancellationToken).ConfigureAwait(false);
            if (validatedSource.IsFailure)
            {
                return Result.Failure<
                    IReadOnlyCollection<DataRightsSubjectSelection>>(
                        validatedSource.Error);
            }

            IDataRightsRequiredCompanionContributor[] matches =
                orderedContributors.Value
                    .Where(contributor =>
                        string.Equals(
                            contributor.SourceOwnerKey.Trim(),
                            source.OwnerKey,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            contributor.SourceRecordType.Trim(),
                            source.RecordType,
                            StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            if (matches.Length >
                DataRightsRequiredCompanionContract
                    .MaximumContributorsPerCoordinate)
            {
                return Invalid();
            }

            foreach (IDataRightsRequiredCompanionContributor contributor
                in matches)
            {
                int remaining =
                    DataRightsCase.MaxSelectedSubjects - closure.Count;
                DataRightsRequiredCompanionResult result;
                try
                {
                    result = await contributor.ExpandAsync(
                        new DataRightsRequiredCompanionRequest(
                            DataRightsRequiredCompanionContract
                                .CurrentVersion,
                            dataRightsCase.ScopeId,
                            (DataRightsCaseType)dataRightsCase.Kind,
                            operation,
                            dataRightsCase.PropertyId,
                            dataRightsCase.Id,
                            validatedSource.Value,
                            remaining),
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (Exception exception)
                    when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(
                        "Required Data Rights companion contributor {ContributorKey} failed because {ExceptionType} was raised.",
                        contributor.ContributorKey,
                        exception.GetType().Name);
                    return RetryRequired();
                }

                Result<IReadOnlyCollection<DataRightsSubjectCoordinate>>
                    contribution = ValidateResult(result);
                if (contribution.IsFailure)
                {
                    return Result.Failure<
                        IReadOnlyCollection<DataRightsSubjectSelection>>(
                            contribution.Error);
                }

                foreach (DataRightsSubjectCoordinate candidate in
                    contribution.Value
                        .OrderBy(
                            coordinate => coordinate.OwnerKey,
                            StringComparer.Ordinal)
                        .ThenBy(
                            coordinate => coordinate.RecordType,
                            StringComparer.Ordinal)
                        .ThenBy(coordinate => coordinate.RecordId))
                {
                    Result<DataRightsSubjectCoordinate> validated =
                        await this.ValidateCoordinateAsync(
                            dataRightsCase,
                            candidate,
                            cancellationToken).ConfigureAwait(false);
                    if (validated.IsFailure)
                    {
                        return Result.Failure<
                            IReadOnlyCollection<DataRightsSubjectSelection>>(
                                validated.Error);
                    }

                    CoordinateKey key =
                        CoordinateKey.Create(validated.Value);
                    if (closure.TryGetValue(
                            key,
                            out DataRightsSubjectCoordinate? existing))
                    {
                        if (existing.RecordVersion !=
                            validated.Value.RecordVersion)
                        {
                            return Result.Failure<
                                IReadOnlyCollection<
                                    DataRightsSubjectSelection>>(
                                DataRightsApplicationErrors.SubjectStale);
                        }

                        continue;
                    }

                    if (closure.Count >=
                        DataRightsCase.MaxSelectedSubjects)
                    {
                        return Result.Failure<
                            IReadOnlyCollection<DataRightsSubjectSelection>>(
                            DataRightsApplicationErrors
                                .SubjectSelectionLimitReached);
                    }

                    closure.Add(key, validated.Value);
                    pending.Enqueue(validated.Value);
                }
            }
        }

        DataRightsSubjectSelection[] added = closure.Values
            .OrderBy(coordinate => coordinate.OwnerKey, StringComparer.Ordinal)
            .ThenBy(
                coordinate => coordinate.RecordType,
                StringComparer.Ordinal)
            .ThenBy(coordinate => coordinate.RecordId)
            .Where(coordinate =>
                !initialKeys.Contains(CoordinateKey.Create(coordinate)))
            .Select(coordinate => new DataRightsSubjectSelection(
                coordinate.OwnerKey,
                coordinate.RecordType,
                coordinate.RecordId,
                coordinate.RecordVersion))
            .ToArray();
        return Result.Success<
            IReadOnlyCollection<DataRightsSubjectSelection>>(added);
    }

    private async Task<Result<DataRightsSubjectCoordinate>>
        ValidateCoordinateAsync(
            DataRightsCase dataRightsCase,
            DataRightsSubjectCoordinate coordinate,
            CancellationToken cancellationToken)
    {
        Result<IDataRightsSubjectDiscoveryContributor> owner =
            DataRightsSubjectContributorSet.Find(
                discoveryContributors,
                coordinate.OwnerKey,
                (DataRightsCaseType)dataRightsCase.Kind);
        if (owner.IsFailure)
        {
            return Result.Failure<DataRightsSubjectCoordinate>(
                string.Equals(
                    owner.Error.Code,
                    DataRightsApplicationErrors.SubjectOwnerUnavailable.Code,
                    StringComparison.Ordinal)
                    ? DataRightsApplicationErrors.RequiredCompanionUnavailable
                    : DataRightsApplicationErrors.RequiredCompanionResultInvalid);
        }

        DataRightsSubjectSelectionValidation validation;
        try
        {
            validation = await owner.Value.ValidateSelectionAsync(
                new DataRightsSubjectSelectionRequest(
                    dataRightsCase.ScopeId,
                    (DataRightsCaseType)dataRightsCase.Kind,
                    dataRightsCase.PropertyId,
                    coordinate),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Required Data Rights companion validation failed for owner {OwnerKey} because {ExceptionType} was raised.",
                owner.Value.OwnerKey,
                exception.GetType().Name);
            return Result.Failure<DataRightsSubjectCoordinate>(
                DataRightsApplicationErrors
                    .RequiredCompanionRetryRequired);
        }

        if (validation is null)
        {
            return Result.Failure<DataRightsSubjectCoordinate>(
                DataRightsApplicationErrors
                    .RequiredCompanionResultInvalid);
        }

        if (validation.Status !=
            DataRightsSubjectSelectionValidationStatus.Valid)
        {
            if (validation.Coordinate is not null)
            {
                return Result.Failure<DataRightsSubjectCoordinate>(
                    DataRightsApplicationErrors
                        .RequiredCompanionResultInvalid);
            }

            return Result.Failure<DataRightsSubjectCoordinate>(
                MapValidationError(validation.Status));
        }

        if (validation.Coordinate is not { } validated ||
            !Matches(coordinate, validated))
        {
            return Result.Failure<DataRightsSubjectCoordinate>(
                DataRightsApplicationErrors
                    .RequiredCompanionResultInvalid);
        }

        return Result.Success(validated);
    }

    private static Result<IReadOnlyCollection<
        IDataRightsRequiredCompanionContributor>> OrderContributors(
            IEnumerable<IDataRightsRequiredCompanionContributor> supplied,
            DataRightsCaseType caseType,
            DataRightsOperation operation)
    {
        IDataRightsRequiredCompanionContributor[] all =
            supplied?.ToArray() ?? [];
        if (all.Any(contributor =>
                contributor is null ||
                contributor.ContractVersion !=
                    DataRightsRequiredCompanionContract.CurrentVersion ||
                contributor.CaseType == DataRightsCaseType.Unknown ||
                contributor.Operation == DataRightsOperation.None ||
                string.IsNullOrWhiteSpace(
                    contributor.ContributorKey) ||
                contributor.ContributorKey.Trim().Length >
                    DataRightsRequiredCompanionContract
                        .ContributorKeyMaxLength ||
                !IsCoordinatePart(contributor.SourceOwnerKey) ||
                !IsCoordinatePart(contributor.SourceRecordType)) ||
            all.GroupBy(
                    contributor =>
                        contributor.ContributorKey.Trim()
                            .ToLowerInvariant(),
                    StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
        {
            return Result.Failure<IReadOnlyCollection<
                IDataRightsRequiredCompanionContributor>>(
                DataRightsApplicationErrors
                    .RequiredCompanionUnavailable);
        }

        return Result.Success<IReadOnlyCollection<
            IDataRightsRequiredCompanionContributor>>(
            all.Where(contributor =>
                    contributor.CaseType == caseType &&
                    contributor.Operation == operation)
                .OrderBy(
                    contributor =>
                        contributor.ContributorKey.Trim()
                            .ToLowerInvariant(),
                    StringComparer.Ordinal)
                .ToArray());
    }

    private static Result<IReadOnlyCollection<DataRightsSubjectCoordinate>>
        ValidateResult(DataRightsRequiredCompanionResult? result)
    {
        if (result is null ||
            result.ContractVersion !=
                DataRightsRequiredCompanionContract.CurrentVersion ||
            result.Coordinates is null)
        {
            return InvalidCoordinates();
        }

        if (result.Status ==
            DataRightsRequiredCompanionStatus.Completed)
        {
            if (result.OutcomeCode is not null ||
                result.Coordinates.Count >
                    DataRightsRequiredCompanionContract
                        .MaximumCoordinatesPerContribution ||
                result.Coordinates.Any(coordinate =>
                    coordinate is null ||
                    !IsCoordinatePart(coordinate.OwnerKey) ||
                    !IsCoordinatePart(coordinate.RecordType) ||
                    coordinate.RecordId == Guid.Empty ||
                    coordinate.RecordVersion <= 0))
            {
                return InvalidCoordinates();
            }

            return Result.Success<
                IReadOnlyCollection<DataRightsSubjectCoordinate>>(
                result.Coordinates);
        }

        if (result.Coordinates.Count != 0 ||
            !IsOutcomeCode(result.OutcomeCode))
        {
            return InvalidCoordinates();
        }

        return result.Status switch
        {
            DataRightsRequiredCompanionStatus.Blocked =>
                Result.Failure<
                    IReadOnlyCollection<DataRightsSubjectCoordinate>>(
                    DataRightsApplicationErrors
                        .RequiredCompanionBlocked),
            DataRightsRequiredCompanionStatus.RetryRequired =>
                Result.Failure<
                    IReadOnlyCollection<DataRightsSubjectCoordinate>>(
                    DataRightsApplicationErrors
                        .RequiredCompanionRetryRequired),
            _ => InvalidCoordinates()
        };
    }

    private static Error MapValidationError(
        DataRightsSubjectSelectionValidationStatus status) =>
        status switch
        {
            DataRightsSubjectSelectionValidationStatus.Stale =>
                DataRightsApplicationErrors.SubjectStale,
            DataRightsSubjectSelectionValidationStatus.NotFound =>
                DataRightsApplicationErrors.SubjectNotFound,
            DataRightsSubjectSelectionValidationStatus.ScopeUnavailable =>
                DataRightsApplicationErrors.DiscoveryScopeUnavailable,
            DataRightsSubjectSelectionValidationStatus.RetryRequired =>
                DataRightsApplicationErrors.RequiredCompanionRetryRequired,
            _ =>
                DataRightsApplicationErrors
                    .RequiredCompanionResultInvalid
        };

    private static bool Matches(
        DataRightsSubjectCoordinate requested,
        DataRightsSubjectCoordinate validated) =>
        string.Equals(
            requested.OwnerKey?.Trim(),
            validated.OwnerKey?.Trim(),
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            requested.RecordType?.Trim(),
            validated.RecordType?.Trim(),
            StringComparison.OrdinalIgnoreCase) &&
        requested.RecordId == validated.RecordId &&
        requested.RecordVersion == validated.RecordVersion;

    private static bool IsCoordinatePart(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <=
            SelectedSubject.OwnerKeyMaxLength;
    }

    private static bool IsOutcomeCode(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <=
            DataRightsRequiredCompanionContract.OutcomeCodeMaxLength;
    }

    private static DataRightsSubjectCoordinate ToContract(
        SelectedSubject subject) =>
        new(
            subject.OwnerKey,
            subject.RecordType,
            subject.RecordId,
            subject.RecordVersion);

    private static Result<IReadOnlyCollection<DataRightsSubjectSelection>>
        Invalid() =>
        Result.Failure<
            IReadOnlyCollection<DataRightsSubjectSelection>>(
            DataRightsApplicationErrors
                .RequiredCompanionResultInvalid);

    private static Result<IReadOnlyCollection<DataRightsSubjectCoordinate>>
        InvalidCoordinates() =>
        Result.Failure<
            IReadOnlyCollection<DataRightsSubjectCoordinate>>(
            DataRightsApplicationErrors
                .RequiredCompanionResultInvalid);

    private static Result<IReadOnlyCollection<DataRightsSubjectSelection>>
        RetryRequired() =>
        Result.Failure<
            IReadOnlyCollection<DataRightsSubjectSelection>>(
            DataRightsApplicationErrors
                .RequiredCompanionRetryRequired);

    private readonly record struct CoordinateKey(
        string OwnerKey,
        string RecordType,
        Guid RecordId)
    {
        public static CoordinateKey Create(
            DataRightsSubjectCoordinate coordinate) =>
            new(
                coordinate.OwnerKey.Trim().ToLowerInvariant(),
                coordinate.RecordType.Trim().ToLowerInvariant(),
                coordinate.RecordId);
    }
}
