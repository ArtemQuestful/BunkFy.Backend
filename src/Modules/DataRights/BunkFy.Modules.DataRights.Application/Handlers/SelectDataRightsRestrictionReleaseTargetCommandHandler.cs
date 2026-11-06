namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Mapping;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.Extensions.Logging;
using SelectedSubject = BunkFy.Modules.DataRights.Domain.Entities.DataRightsSubjectCoordinate;

internal sealed class SelectDataRightsRestrictionReleaseTargetCommandHandler(
    DataRightsCaseMutationCoordinator mutations,
    IEnumerable<IDataRightsRestrictionContributor> contributors,
    IScopeContext scopeContext,
    ISystemClock clock,
    ILogger<SelectDataRightsRestrictionReleaseTargetCommandHandler> logger)
    : ICommandHandler<
        SelectDataRightsRestrictionReleaseTargetCommand,
        DataRightsCaseDto>
{
    private static readonly TimeSpan OwnerDeadline = TimeSpan.FromSeconds(30);

    public async Task<Result<DataRightsCaseDto>> HandleAsync(
        SelectDataRightsRestrictionReleaseTargetCommand command,
        CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled || string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Result.Failure<DataRightsCaseDto>(
                DataRightsApplicationErrors.TenantRequired);
        }

        DataRightsCase? dataRightsCase = await mutations.AcquireAsync(
            command.Scope,
            command.CaseId,
            cancellationToken).ConfigureAwait(false);
        if (dataRightsCase is null)
        {
            return Result.Failure<DataRightsCaseDto>(
                DataRightsApplicationErrors.CaseNotFound);
        }

        Result<SelectedSubject> subject =
            DataRightsRestrictionReleaseTargetCase.RequireSubject(
                dataRightsCase,
                command.Scope);
        if (subject.IsFailure)
        {
            return Result.Failure<DataRightsCaseDto>(subject.Error);
        }

        Result<IDataRightsRestrictionContributor> contributor =
            DataRightsRestrictionContributorSet.Resolve(
                contributors,
                subject.Value.OwnerKey);
        if (contributor.IsFailure)
        {
            return Result.Failure<DataRightsCaseDto>(contributor.Error);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        Result<DataRightsRestrictionTargetSet> resolved =
            await GetDataRightsRestrictionReleaseTargetsQueryHandler.ResolveAsync(
                contributor.Value,
                dataRightsCase,
                command.Scope,
                subject.Value,
                command.OwnerOperationId,
                command.OwnerOperationVersion,
                nowUtc.Add(OwnerDeadline),
                clock,
                logger,
                cancellationToken).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return Result.Failure<DataRightsCaseDto>(resolved.Error);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Result selected = dataRightsCase.SelectRestrictionReleaseTarget(
            subject.Value.OwnerKey,
            command.OwnerOperationId,
            command.OwnerOperationVersion,
            command.ExpectedVersion,
            command.ActorId,
            clock.UtcNow);
        return selected.IsSuccess
            ? Result.Success(dataRightsCase.ToDto())
            : Result.Failure<DataRightsCaseDto>(selected.Error);
    }
}
