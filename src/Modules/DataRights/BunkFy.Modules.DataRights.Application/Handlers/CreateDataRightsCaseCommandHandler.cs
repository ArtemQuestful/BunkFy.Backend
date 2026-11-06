namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Mapping;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class CreateDataRightsCaseCommandHandler(
    IDataRightsCaseRepository cases,
    DataRightsCaseMutationCoordinator mutations,
    IDataRightsResponseDeadlinePolicy responseDeadlinePolicy,
    IScopeContext scopeContext,
    ISystemClock clock) : ICommandHandler<CreateDataRightsCaseCommand, DataRightsCaseDto>
{
    public async Task<Result<DataRightsCaseDto>> HandleAsync(
        CreateDataRightsCaseCommand command,
        CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled || string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Result.Failure<DataRightsCaseDto>(DataRightsApplicationErrors.TenantRequired);
        }

        if (command.OperationId == Guid.Empty)
        {
            return Result.Failure<DataRightsCaseDto>(
                DataRightsApplicationErrors.CreationOperationInvalid);
        }

        Result<DataRightsCaseRequest> request = DataRightsCaseRequest.Create(
            command.Scope.PropertyId,
            (DataRightsCaseKind)command.Scope.CaseType,
            (DataRightsCaseOperation)command.RequestedOperations,
            (DataRightsRequesterRelation)command.RequesterRelationship,
            (DataRightsRestrictionAction)command.RestrictionDirective);
        if (request.IsFailure)
        {
            return Result.Failure<DataRightsCaseDto>(request.Error);
        }

        DataRightsCase? existing = await mutations.AcquireCreationAsync(
            command.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.MatchesCreation(request.Value, command.ActorId)
                ? Result.Success(existing.ToDto())
                : Result.Failure<DataRightsCaseDto>(
                    DataRightsApplicationErrors.CreationOperationConflict);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        DataRightsResponseDeadlinePolicyEvidence? deadlineEvidence = null;
        if (RequiresGuestResponseDeadline(request.Value))
        {
            Result<DataRightsResponseDeadlinePolicyEvidence> deadline =
                await responseDeadlinePolicy.ResolveGuestAsync(
                    request.Value.PropertyId!.Value,
                    request.Value.RequestedOperations,
                    nowUtc,
                    nowUtc,
                    cancellationToken).ConfigureAwait(false);
            if (deadline.IsSuccess)
            {
                deadlineEvidence = deadline.Value;
            }
        }

        Result<DataRightsCase> created = DataRightsCase.Create(
            command.OperationId,
            scopeContext.ScopeId,
            request.Value,
            command.ActorId,
            nowUtc,
            deadlineEvidence);
        if (created.IsFailure)
        {
            return Result.Failure<DataRightsCaseDto>(created.Error);
        }

        await cases.AddAsync(created.Value, cancellationToken).ConfigureAwait(false);
        return Result.Success(created.Value.ToDto());
    }

    private static bool RequiresGuestResponseDeadline(DataRightsCaseRequest request) =>
        request.Kind == DataRightsCaseKind.GuestRights &&
        request.RequesterRelationship is
            DataRightsRequesterRelation.DataSubject or
            DataRightsRequesterRelation.AuthorizedRepresentative;
}
