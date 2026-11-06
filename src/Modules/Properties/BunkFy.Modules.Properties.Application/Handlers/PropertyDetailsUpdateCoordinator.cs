namespace BunkFy.Modules.Properties.Application.Handlers;

using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;

internal sealed class PropertyDetailsUpdateCoordinator(
    IPropertyRepository properties,
    PropertyMutationOperationJournal journal,
    PropertiesMutationCoordinator mutations,
    ISystemClock clock,
    IIdGenerator ids)
{
    public async Task<Result<PropertyMutationReceiptDto>> ExecuteAsync(
        Property property,
        Guid operationId,
        long expectedVersion,
        PropertyDetails details,
        string? requestedTimeZoneId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(details);
        string fingerprint = PropertyDetailsUpdateFingerprint.ComputeV3(
            property.Id,
            expectedVersion,
            details,
            requestedTimeZoneId);
        string? versionTwoFingerprint = requestedTimeZoneId is null
            ? null
            : PropertyDetailsUpdateFingerprint.ComputeV2(
                property.Id,
                expectedVersion,
                details);
        string? versionOneFingerprint = requestedTimeZoneId is null
            ? null
            : PropertyDetailsUpdateFingerprint.ComputeV1(
                property.Id,
                expectedVersion,
                details,
                requestedTimeZoneId);
        PropertyMutationReplayDecision<PropertyMutationReceiptDto> replay =
            await journal.InspectPropertyAsync(
            property,
            operationId,
            PropertyMutationKind.DetailsUpdate,
            expectedVersion,
            fingerprint,
            versionTwoFingerprint,
            versionOneFingerprint,
            cancellationToken).ConfigureAwait(false);
        if (replay.Exists)
        {
            return replay.ToResult();
        }

        if (property.TimeZoneId != details.TimeZoneId)
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                PropertiesApplicationErrors
                    .TimeZoneDedicatedOperationRequired);
        }

        Result<PropertyDetailsUpdateOutcome> evaluation =
            property.EvaluateDetailsUpdate(details, expectedVersion);
        if (evaluation.IsFailure)
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                evaluation.Error);
        }

        if (evaluation.Value == PropertyDetailsUpdateOutcome.Changed)
        {
            await mutations.AcquirePropertyCodeAsync(
                details.Code,
                cancellationToken).ConfigureAwait(false);
            if (await properties.CodeExistsAsync(
                    details.Code.Value,
                    property.Id,
                    cancellationToken).ConfigureAwait(false))
            {
                return Result.Failure<PropertyMutationReceiptDto>(
                    PropertiesDomainErrors.PropertyCodeAlreadyExists);
            }
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        if (!PropertiesObservationTime.IsValid(nowUtc))
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                PropertiesApplicationErrors.TimeSourceUnavailable);
        }

        if (evaluation.Value == PropertyDetailsUpdateOutcome.Changed)
        {
            Result<PropertyDetailsUpdateOutcome> update =
                property.UpdateDetails(
                    details,
                    expectedVersion,
                    ids.NewId(),
                    nowUtc);
            if (update.IsFailure ||
                update.Value != PropertyDetailsUpdateOutcome.Changed)
            {
                return update.IsFailure
                    ? Result.Failure<PropertyMutationReceiptDto>(
                        update.Error)
                    : throw new InvalidOperationException(
                        "The Property details update outcome changed while locked.");
            }
        }

        PropertyMutationReceiptDto receipt = await journal.RecordPropertyAsync(
            property,
            operationId,
            PropertyMutationKind.DetailsUpdate,
            expectedVersion,
            fingerprint,
            nowUtc,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(receipt);
    }
}
