namespace BunkFy.Modules.Properties.Application.Handlers;

using BunkFy.DataGovernance;
using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using BunkFy.TimeZones;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;

internal sealed class SetPropertyTimeZoneCommandHandler(
    PropertiesMutationCoordinator mutations,
    IPropertyTimeZoneRevisionReader revisions,
    IPropertyTimeZoneRevisionWriter revisionWriter,
    CountryPolicyRegistry countryPolicies,
    ISystemClock clock,
    IIdGenerator ids,
    TimeZoneRuntimeCompatibilityProbe runtimeTimeZones)
    : ICommandHandler<SetPropertyTimeZoneCommand,
        SetPropertyTimeZoneReceiptDto>
{
    public async Task<Result<SetPropertyTimeZoneReceiptDto>> HandleAsync(
        SetPropertyTimeZoneCommand command,
        CancellationToken cancellationToken)
    {
        if (command.OperationId == Guid.Empty)
        {
            return Result.Failure<SetPropertyTimeZoneReceiptDto>(
                PropertiesApplicationErrors.ManagementOperationInvalid);
        }

        string requestedTimeZoneId;
        try
        {
            requestedTimeZoneId = PropertyTimeZoneId.RestorePersisted(
                command.TimeZoneId).Value;
        }
        catch (ArgumentException)
        {
            return Result.Failure<SetPropertyTimeZoneReceiptDto>(
                PropertiesDomainErrors.TimeZoneInvalid);
        }

        PropertyTimeZoneRevisionReadModel? existing =
            await revisions.GetAsync(
                command.PropertyId,
                command.OperationId,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Replay(existing, command, requestedTimeZoneId);
        }

        Result<PropertyMutationActor> actorResult =
            PropertyMutationActor.Required(command.ActorId);
        if (actorResult.IsFailure)
        {
            return Result.Failure<SetPropertyTimeZoneReceiptDto>(
                actorResult.Error);
        }

        Result<PropertyTimeZoneId> timeZoneResult =
            PropertyTimeZoneId.Create(requestedTimeZoneId);
        if (timeZoneResult.IsFailure)
        {
            return Result.Failure<SetPropertyTimeZoneReceiptDto>(
                timeZoneResult.Error);
        }

        PropertyTimeZoneId requested = timeZoneResult.Value;
        bool acquired = await mutations.AcquirePropertyOperationAsync(
            command.PropertyId,
            cancellationToken).ConfigureAwait(false);
        if (!acquired)
        {
            return Result.Failure<SetPropertyTimeZoneReceiptDto>(
                PropertiesDomainErrors.PropertyNotFound);
        }

        existing = await revisions.GetAsync(
            command.PropertyId,
            command.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Replay(existing, command, requestedTimeZoneId);
        }

        Property? property = await mutations.ReloadPropertyAsync(
            command.PropertyId,
            cancellationToken).ConfigureAwait(false);
        if (property is null)
        {
            return Result.Failure<SetPropertyTimeZoneReceiptDto>(
                PropertiesDomainErrors.PropertyNotFound);
        }

        Result<PropertyDetailsUpdateOutcome> evaluation =
            property.EvaluateTimeZoneChange(
                requested,
                command.ExpectedVersion);
        if (evaluation.IsFailure)
        {
            return Result.Failure<SetPropertyTimeZoneReceiptDto>(
                evaluation.Error);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        if (!PropertiesObservationTime.IsValid(nowUtc))
        {
            return Result.Failure<SetPropertyTimeZoneReceiptDto>(
                PropertiesApplicationErrors.TimeSourceUnavailable);
        }

        if (!runtimeTimeZones.IsCompatible(
                requested.Value,
                nowUtc))
        {
            return Result.Failure<SetPropertyTimeZoneReceiptDto>(
                PropertiesApplicationErrors.TimeZoneRuntimeUnavailable);
        }

        PropertyTimeZoneChangeKind changeKind = ClassifyChange(
            property.TimeZoneId.Value,
            requested.Value);
        if (changeKind == PropertyTimeZoneChangeKind.Changed &&
            !command.Confirmed)
        {
            return Result.Failure<SetPropertyTimeZoneReceiptDto>(
                PropertiesApplicationErrors.ConfirmationRequired);
        }

        if (changeKind == PropertyTimeZoneChangeKind.Changed)
        {
            CountryPolicyTimeZoneDecision policy =
                EvaluatePolicy(
                    countryPolicies,
                    property,
                    requested.Value,
                    nowUtc);
            if (!policy.IsAllowed)
            {
                return Result.Failure<SetPropertyTimeZoneReceiptDto>(
                    PropertiesApplicationErrors.CountryPolicyDenied(
                        policy.Reason));
            }
        }

        string previousTimeZoneId = property.TimeZoneId.Value;
        Result<PropertyDetailsUpdateOutcome> update = property.SetTimeZone(
            requested,
            command.ExpectedVersion,
            changeKind == PropertyTimeZoneChangeKind.Unchanged
                ? Guid.Empty
                : ids.NewId(),
            nowUtc);
        if (update.IsFailure)
        {
            return Result.Failure<SetPropertyTimeZoneReceiptDto>(
                update.Error);
        }

        PropertyTimeZoneRevisionWriteModel revision = new(
            ids.NewId(),
            property.ScopeId,
            property.Id,
            command.OperationId,
            changeKind,
            requestedTimeZoneId,
            previousTimeZoneId,
            property.TimeZoneId.Value,
            TimeZoneCatalog.Default.CatalogVersion,
            command.ExpectedVersion,
            property.Version,
            actorResult.Value.Value!,
            nowUtc);
        await revisionWriter.AppendAsync(
            revision,
            cancellationToken).ConfigureAwait(false);

        return Result.Success(ToReceipt(revision));
    }

    private static Result<SetPropertyTimeZoneReceiptDto> Replay(
        PropertyTimeZoneRevisionReadModel existing,
        SetPropertyTimeZoneCommand command,
        string requestedTimeZoneId) =>
        existing.ExpectedVersion == command.ExpectedVersion &&
        string.Equals(
            existing.RequestedTimeZoneId,
            requestedTimeZoneId,
            StringComparison.Ordinal)
            ? Result.Success(ToReceipt(existing))
            : Result.Failure<SetPropertyTimeZoneReceiptDto>(
                PropertiesApplicationErrors.ManagementOperationConflict);

    private static CountryPolicyTimeZoneDecision EvaluatePolicy(
        CountryPolicyRegistry countryPolicies,
        Property property,
        string timeZoneId,
        DateTimeOffset observedAtUtc) =>
        property.ProcessingState == PropertyProcessingState.Unconfigured
            ? CountryPolicyTimeZoneDecision.Allow(timeZoneId)
            : countryPolicies.EvaluateTimeZoneCompatibility(new(
                PropertyCountryPolicyBindingMapper.ToCountryPolicyBinding(
                    property),
                ActivatePropertyProcessingCommandHandler.AccommodationType,
                timeZoneId,
                observedAtUtc));

    private static PropertyTimeZoneChangeKind ClassifyChange(
        string currentTimeZoneId,
        string requestedCanonicalTimeZoneId)
    {
        if (string.Equals(
                currentTimeZoneId,
                requestedCanonicalTimeZoneId,
                StringComparison.Ordinal))
        {
            return PropertyTimeZoneChangeKind.Unchanged;
        }

        return TimeZoneCatalog.Default.TryResolve(
                   currentTimeZoneId,
                   out TimeZoneCatalogResolution? current) &&
               string.Equals(
                   current.CanonicalTimeZoneId,
                   requestedCanonicalTimeZoneId,
                   StringComparison.Ordinal)
            ? PropertyTimeZoneChangeKind.Canonicalized
            : PropertyTimeZoneChangeKind.Changed;
    }

    internal static SetPropertyTimeZoneReceiptDto ToReceipt(
        PropertyTimeZoneRevisionReadModel revision) => new(
            revision.PropertyId,
            revision.OperationId,
            revision.ChangeKind,
            revision.RequestedTimeZoneId,
            revision.PreviousTimeZoneId,
            revision.TimeZoneId,
            PropertyTimeZoneStatus.Canonical,
            revision.CatalogVersion,
            revision.ExpectedVersion,
            revision.ResultVersion,
            revision.ActorId,
            revision.OccurredAtUtc);

    private static SetPropertyTimeZoneReceiptDto ToReceipt(
        PropertyTimeZoneRevisionWriteModel revision) => new(
            revision.PropertyId,
            revision.OperationId,
            revision.ChangeKind,
            revision.RequestedTimeZoneId,
            revision.PreviousTimeZoneId,
            revision.TimeZoneId,
            PropertyTimeZoneStatus.Canonical,
            revision.CatalogVersion,
            revision.ExpectedVersion,
            revision.ResultVersion,
            revision.ActorId,
            revision.OccurredAtUtc);
}
