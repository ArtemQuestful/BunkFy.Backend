namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Mapping;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Logging;
using RestrictionReleaseTarget =
    BunkFy.Modules.DataRights.Domain.ValueObjects.DataRightsRestrictionReleaseTarget;
using SelectedSubject = BunkFy.Modules.DataRights.Domain.Entities.DataRightsSubjectCoordinate;

internal sealed class RequireDataRightsReviewCommandHandler(
    DataRightsCaseMutationCoordinator mutations,
    DataRightsRequiredCompanionExpander companionExpander,
    IEnumerable<IDataRightsRestrictionContributor> restrictionContributors,
    ISystemClock clock,
    ILogger<RequireDataRightsReviewCommandHandler> logger)
    : ICommandHandler<RequireDataRightsReviewCommand, DataRightsCaseDto>
{
    private static readonly TimeSpan OwnerDeadline = TimeSpan.FromSeconds(30);

    public async Task<Result<DataRightsCaseDto>> HandleAsync(
        RequireDataRightsReviewCommand command,
        CancellationToken cancellationToken)
    {
        DataRightsCase? dataRightsCase = await mutations.AcquireAsync(
            command.Scope,
            command.CaseId,
            cancellationToken).ConfigureAwait(false);
        if (dataRightsCase is null)
        {
            return Result.Failure<DataRightsCaseDto>(
                DataRightsApplicationErrors.CaseNotFound);
        }

        Result<IReadOnlyCollection<DataRightsSubjectSelection>> companions =
            await companionExpander.ExpandAsync(
                dataRightsCase,
                cancellationToken).ConfigureAwait(false);
        if (companions.IsFailure)
        {
            return Result.Failure<DataRightsCaseDto>(companions.Error);
        }

        DateTimeOffset targetStartedAtUtc = clock.UtcNow;
        Result targetCurrent = await ValidateRestrictionReleaseTargetAsync(
            dataRightsCase,
            command.Scope,
            restrictionContributors,
            targetStartedAtUtc.Add(OwnerDeadline),
            clock,
            logger,
            cancellationToken).ConfigureAwait(false);
        if (targetCurrent.IsFailure)
        {
            return Result.Failure<DataRightsCaseDto>(targetCurrent.Error);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Result transitioned = dataRightsCase.RequireReview(
            companions.Value,
            command.ExpectedVersion,
            command.ActorId,
            clock.UtcNow);
        return transitioned.IsSuccess
            ? Result.Success(dataRightsCase.ToDto())
            : Result.Failure<DataRightsCaseDto>(transitioned.Error);
    }

    private static async Task<Result> ValidateRestrictionReleaseTargetAsync(
        DataRightsCase dataRightsCase,
        DataRightsCaseScope scope,
        IEnumerable<IDataRightsRestrictionContributor> contributors,
        DateTimeOffset deadlineUtc,
        ISystemClock clock,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (dataRightsCase.RestrictionTargetingContractVersion !=
            RestrictionReleaseTarget.CurrentBindingVersion)
        {
            return Result.Success();
        }

        Result<SelectedSubject> subject =
            DataRightsRestrictionReleaseTargetCase.RequireSubject(
                dataRightsCase,
                scope);
        if (subject.IsFailure)
        {
            return Result.Failure(subject.Error);
        }

        RestrictionReleaseTarget? target =
            dataRightsCase.RestrictionReleaseTarget;
        if (target is null)
        {
            return Result.Failure(
                DataRightsApplicationErrors.RestrictionReleaseTargetRequired);
        }

        Result<IDataRightsRestrictionContributor> contributor =
            DataRightsRestrictionContributorSet.Resolve(
                contributors,
                subject.Value.OwnerKey);
        if (contributor.IsFailure)
        {
            return Result.Failure(contributor.Error);
        }

        Result<DataRightsRestrictionTargetSet> resolved =
            await GetDataRightsRestrictionReleaseTargetsQueryHandler.ResolveAsync(
                contributor.Value,
                dataRightsCase,
                scope,
                subject.Value,
                target.OwnerOperationId,
                target.OwnerOperationVersion,
                deadlineUtc,
                clock,
                logger,
                cancellationToken).ConfigureAwait(false);
        return resolved.IsSuccess
            ? Result.Success()
            : Result.Failure(resolved.Error);
    }
}
