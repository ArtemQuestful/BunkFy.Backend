namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Mapping;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.Extensions.Logging;

internal sealed class SelectDataRightsSubjectCommandHandler(
    DataRightsCaseMutationCoordinator mutations,
    IEnumerable<IDataRightsSubjectDiscoveryContributor> contributors,
    IScopeContext scopeContext,
    ISystemClock clock,
    ILogger<SelectDataRightsSubjectCommandHandler> logger)
    : ICommandHandler<SelectDataRightsSubjectCommand, DataRightsCaseDto>
{
    public async Task<Result<DataRightsCaseDto>> HandleAsync(
        SelectDataRightsSubjectCommand command,
        CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled || string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Result.Failure<DataRightsCaseDto>(DataRightsApplicationErrors.TenantRequired);
        }

        DataRightsCase? dataRightsCase = await mutations.AcquireAsync(
            command.Scope,
            command.CaseId,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (dataRightsCase is null)
        {
            return Result.Failure<DataRightsCaseDto>(DataRightsApplicationErrors.CaseNotFound);
        }

        if (dataRightsCase.Status != DataRightsCaseState.Discovery)
        {
            return Result.Failure<DataRightsCaseDto>(
                DataRightsApplicationErrors.TransitionInvalid);
        }

        DataRightsSubjectCoordinate? requestedCoordinate = command.Coordinate;
        if (!IsValidCoordinate(requestedCoordinate))
        {
            return Result.Failure<DataRightsCaseDto>(
                DataRightsApplicationErrors.SubjectCoordinateInvalid);
        }

        Result<IDataRightsSubjectDiscoveryContributor> contributor =
            DataRightsSubjectContributorSet.Find(
                contributors,
                requestedCoordinate.OwnerKey,
                (DataRightsCaseType)dataRightsCase.Kind);
        if (contributor.IsFailure)
        {
            return Result.Failure<DataRightsCaseDto>(contributor.Error);
        }

        DataRightsSubjectSelectionValidation validation;
        try
        {
            validation = await contributor.Value.ValidateSelectionAsync(
                new DataRightsSubjectSelectionRequest(
                    scopeContext.ScopeId,
                    (DataRightsCaseType)dataRightsCase.Kind,
                    dataRightsCase.PropertyId,
                    requestedCoordinate!),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Data Rights subject selection owner {OwnerKey} failed because {ExceptionType} was raised.",
                contributor.Value.OwnerKey,
                exception.GetType().Name);
            return Result.Failure<DataRightsCaseDto>(
                DataRightsApplicationErrors.SubjectOwnerRetryRequired);
        }

        if (validation is null)
        {
            return Result.Failure<DataRightsCaseDto>(
                DataRightsApplicationErrors.SubjectOwnerResultInvalid);
        }

        if (validation.Status != DataRightsSubjectSelectionValidationStatus.Valid)
        {
            if (validation.Coordinate is not null)
            {
                return Result.Failure<DataRightsCaseDto>(
                    DataRightsApplicationErrors.SubjectOwnerResultInvalid);
            }

            return Result.Failure<DataRightsCaseDto>(MapValidationError(validation.Status));
        }

        DataRightsSubjectCoordinate? coordinate = validation.Coordinate;
        if (!IsValidCoordinate(coordinate) ||
            !IsSameSelection(requestedCoordinate!, coordinate!))
        {
            return Result.Failure<DataRightsCaseDto>(
                DataRightsApplicationErrors.SubjectOwnerResultInvalid);
        }
        DataRightsSubjectCoordinate validatedCoordinate = coordinate!;

        Result selected = dataRightsCase.SelectSubject(
            validatedCoordinate.OwnerKey,
            validatedCoordinate.RecordType,
            validatedCoordinate.RecordId,
            validatedCoordinate.RecordVersion,
            command.ExpectedVersion,
            command.ActorId,
            clock.UtcNow);
        return selected.IsSuccess
            ? Result.Success(dataRightsCase.ToDto())
            : Result.Failure<DataRightsCaseDto>(selected.Error);
    }

    private static Error MapValidationError(DataRightsSubjectSelectionValidationStatus status) =>
        status switch
        {
            DataRightsSubjectSelectionValidationStatus.Stale =>
                DataRightsApplicationErrors.SubjectStale,
            DataRightsSubjectSelectionValidationStatus.ScopeUnavailable =>
                DataRightsApplicationErrors.DiscoveryScopeUnavailable,
            DataRightsSubjectSelectionValidationStatus.NotFound =>
                DataRightsApplicationErrors.SubjectNotFound,
            DataRightsSubjectSelectionValidationStatus.RetryRequired =>
                DataRightsApplicationErrors.SubjectOwnerRetryRequired,
            _ => DataRightsApplicationErrors.SubjectOwnerResultInvalid
        };

    private static bool IsValidCoordinate(DataRightsSubjectCoordinate? coordinate) =>
        coordinate is not null &&
        !string.IsNullOrWhiteSpace(coordinate.OwnerKey) &&
        coordinate.OwnerKey.Trim().Length <=
            DataRightsSubjectDiscoveryLimits.OwnerKeyMaxLength &&
        !string.IsNullOrWhiteSpace(coordinate.RecordType) &&
        coordinate.RecordType.Trim().Length <=
            DataRightsSubjectDiscoveryLimits.RecordTypeMaxLength &&
        coordinate.RecordId != Guid.Empty &&
        coordinate.RecordVersion > 0;

    private static bool IsSameSelection(
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
}
