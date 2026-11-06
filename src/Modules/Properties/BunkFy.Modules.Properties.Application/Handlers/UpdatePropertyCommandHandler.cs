namespace BunkFy.Modules.Properties.Application.Handlers;

using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;

internal sealed class UpdatePropertyCommandHandler(
    PropertiesMutationCoordinator mutations,
    PropertyDetailsUpdateCoordinator updates)
    : ICommandHandler<UpdatePropertyCommand, PropertyMutationReceiptDto>
{
    public async Task<Result<PropertyMutationReceiptDto>> HandleAsync(
        UpdatePropertyCommand command,
        CancellationToken cancellationToken)
    {
        if (command.OperationId == Guid.Empty)
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                PropertiesApplicationErrors.ManagementOperationInvalid);
        }

        Property? property = await mutations
            .AcquirePropertyAsync(
                command.PropertyId,
                cancellationToken).ConfigureAwait(false);
        if (property is null)
        {
            return Result.Failure<PropertyMutationReceiptDto>(PropertiesDomainErrors.PropertyNotFound);
        }

        Result<PropertyDetails> details;
        try
        {
            details = PropertyDetails.RestorePersistedTimeZone(
                command.Name,
                command.Code,
                command.TimeZoneId ?? property.TimeZoneId.Value);
        }
        catch (ArgumentException)
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                PropertiesDomainErrors.TimeZoneInvalid);
        }
        if (details.IsFailure)
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                details.Error);
        }

        return await updates.ExecuteAsync(
            property,
            command.OperationId,
            command.ExpectedVersion,
            details.Value,
            command.TimeZoneId,
            cancellationToken).ConfigureAwait(false);
    }
}
