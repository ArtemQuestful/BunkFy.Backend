namespace BunkFy.Modules.Properties.Application.Handlers;

using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Application.Mapping;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using BunkFy.TimeZones;

internal sealed class CreatePropertyCommandHandler(
    IPropertyRepository repository,
    PropertiesMutationCoordinator mutations,
    IPropertiesCreationOperationLock creationLock,
    IPropertyTimeZoneRevisionReader timeZoneRevisionReader,
    IPropertyTimeZoneRevisionWriter timeZoneRevisions,
    IScopeContext scopeContext,
    ISystemClock clock,
    IIdGenerator idGenerator,
    TimeZoneRuntimeCompatibilityProbe runtimeTimeZones)
    : ICommandHandler<CreatePropertyCommand, PropertyMutationReceiptDto>
{
    public async Task<Result<PropertyMutationReceiptDto>> HandleAsync(
        CreatePropertyCommand command,
        CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled ||
            string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Result.Failure<PropertyMutationReceiptDto>(PropertiesDomainErrors.TenantRequired);
        }

        if (command.OperationId == Guid.Empty)
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                PropertiesApplicationErrors.CreationOperationInvalid);
        }

        Result<PropertyMutationActor> actorResult =
            PropertyMutationActor.Required(command.ActorId);
        if (actorResult.IsFailure)
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                actorResult.Error);
        }

        Result<PropertyDetails> requestedDetails =
            PropertyDetails.RestorePersistedTimeZone(
            command.Name,
            command.Code,
            command.TimeZoneId);
        if (requestedDetails.IsFailure)
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                requestedDetails.Error);
        }

        await creationLock.AcquireAsync(
            scopeContext.ScopeId,
            command.OperationId,
            cancellationToken).ConfigureAwait(false);
        Property? existing = await repository.GetAsync(
            command.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            PropertyTimeZoneRevisionReadModel? creationRevision =
                await timeZoneRevisionReader.GetAsync(
                    existing.Id,
                    command.OperationId,
                    cancellationToken).ConfigureAwait(false);
            if (creationRevision is not null)
            {
                Result<PropertyDetails> ledgerReplay =
                    PropertyDetails.RestorePersistedTimeZone(
                        command.Name,
                        command.Code,
                        creationRevision.TimeZoneId);
                return creationRevision.ChangeKind ==
                           PropertyTimeZoneChangeKind.Created &&
                       string.Equals(
                           creationRevision.RequestedTimeZoneId,
                           requestedDetails.Value.TimeZoneId.Value,
                           StringComparison.Ordinal) &&
                       ledgerReplay.IsSuccess &&
                       existing.MatchesCreation(ledgerReplay.Value)
                    ? Result.Success(PropertiesMapper.ToReceipt(existing))
                    : Result.Failure<PropertyMutationReceiptDto>(
                        PropertiesApplicationErrors
                            .CreationOperationConflict);
            }

            if (existing.MatchesCreation(requestedDetails.Value))
            {
                return Result.Success(PropertiesMapper.ToReceipt(existing));
            }

            Result<PropertyDetails> canonicalReplay =
                PropertyDetails.Create(
                    command.Name,
                    command.Code,
                    command.TimeZoneId);
            return canonicalReplay.IsSuccess &&
                   existing.MatchesCreation(canonicalReplay.Value)
                ? Result.Success(PropertiesMapper.ToReceipt(existing))
                : Result.Failure<PropertyMutationReceiptDto>(
                    PropertiesApplicationErrors.CreationOperationConflict);
        }


        Result<PropertyDetails> details = PropertyDetails.Create(
            command.Name,
            command.Code,
            command.TimeZoneId);
        if (details.IsFailure)
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                details.Error);
        }

        await mutations.AcquirePropertyCodeAsync(
            details.Value.Code,
            cancellationToken).ConfigureAwait(false);
        if (await repository.CodeExistsAsync(
                details.Value.Code.Value,
                excludingPropertyId: null,
                cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<PropertyMutationReceiptDto>(PropertiesDomainErrors.PropertyCodeAlreadyExists);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        if (!PropertiesObservationTime.IsValid(nowUtc))
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                PropertiesApplicationErrors.TimeSourceUnavailable);
        }

        if (!runtimeTimeZones.IsCompatible(
                details.Value.TimeZoneId.Value,
                nowUtc))
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                PropertiesApplicationErrors.TimeZoneRuntimeUnavailable);
        }

        Result<Property> propertyResult = Property.Create(
            command.OperationId,
            scopeContext.ScopeId,
            details.Value,
            idGenerator.NewId(),
            nowUtc);
        if (propertyResult.IsFailure)
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                propertyResult.Error);
        }

        Property property = propertyResult.Value;
        await repository.AddAsync(property, cancellationToken).ConfigureAwait(false);
        await timeZoneRevisions.AppendAsync(
            new PropertyTimeZoneRevisionWriteModel(
                idGenerator.NewId(),
                property.ScopeId,
                property.Id,
                command.OperationId,
                PropertyTimeZoneChangeKind.Created,
                command.TimeZoneId.Trim(),
                null,
                property.TimeZoneId.Value,
                BunkFy.TimeZones.TimeZoneCatalog.Default.CatalogVersion,
                0,
                property.Version,
                actorResult.Value.Value!,
                nowUtc),
            cancellationToken).ConfigureAwait(false);

        return Result.Success(PropertiesMapper.ToReceipt(property));
    }
}
