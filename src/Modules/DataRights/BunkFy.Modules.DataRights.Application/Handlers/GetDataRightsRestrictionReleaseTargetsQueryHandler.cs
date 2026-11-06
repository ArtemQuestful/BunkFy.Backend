namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Queries;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.Extensions.Logging;
using SelectedSubject = BunkFy.Modules.DataRights.Domain.Entities.DataRightsSubjectCoordinate;

internal sealed class GetDataRightsRestrictionReleaseTargetsQueryHandler(
    IDataRightsCaseRepository cases,
    IEnumerable<IDataRightsRestrictionContributor> contributors,
    IScopeContext scopeContext,
    ISystemClock clock,
    ILogger<GetDataRightsRestrictionReleaseTargetsQueryHandler> logger)
    : IQueryHandler<
        GetDataRightsRestrictionReleaseTargetsQuery,
        DataRightsRestrictionReleaseTargetListResponse>
{
    private static readonly TimeSpan OwnerDeadline = TimeSpan.FromSeconds(30);

    public async Task<Result<DataRightsRestrictionReleaseTargetListResponse>> HandleAsync(
        GetDataRightsRestrictionReleaseTargetsQuery query,
        CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled || string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Result.Failure<DataRightsRestrictionReleaseTargetListResponse>(
                DataRightsApplicationErrors.TenantRequired);
        }

        DataRightsCase? dataRightsCase = await cases.GetAsync(
            query.Scope,
            query.CaseId,
            cancellationToken).ConfigureAwait(false);
        if (dataRightsCase is null)
        {
            return Result.Failure<DataRightsRestrictionReleaseTargetListResponse>(
                DataRightsApplicationErrors.CaseNotFound);
        }

        Result<SelectedSubject> subject =
            DataRightsRestrictionReleaseTargetCase.RequireSubject(
                dataRightsCase,
                query.Scope);
        if (subject.IsFailure)
        {
            return Result.Failure<DataRightsRestrictionReleaseTargetListResponse>(
                subject.Error);
        }

        Result<IDataRightsRestrictionContributor> contributor =
            DataRightsRestrictionContributorSet.Resolve(
                contributors,
                subject.Value.OwnerKey);
        if (contributor.IsFailure)
        {
            return Result.Failure<DataRightsRestrictionReleaseTargetListResponse>(
                contributor.Error);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        Result<DataRightsRestrictionTargetSet> resolved = await ResolveAsync(
            contributor.Value,
            dataRightsCase,
            query.Scope,
            subject.Value,
            targetId: null,
            targetVersion: null,
            nowUtc.Add(OwnerDeadline),
            clock,
            logger,
            cancellationToken).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return Result.Failure<DataRightsRestrictionReleaseTargetListResponse>(
                resolved.Error);
        }

        return Result.Success(new DataRightsRestrictionReleaseTargetListResponse(
            dataRightsCase.Version,
            resolved.Value.Targets.Select(target =>
                new DataRightsRestrictionReleaseTargetCandidateDto(
                    target.OwnerOperationId,
                    target.OwnerOperationVersion,
                    target.SourceCaseId,
                    target.AppliedAtUtc)).ToArray(),
            resolved.Value.LimitReached));
    }

    internal static async Task<Result<DataRightsRestrictionTargetSet>> ResolveAsync(
        IDataRightsRestrictionContributor contributor,
        DataRightsCase dataRightsCase,
        DataRightsCaseScope scope,
        SelectedSubject subject,
        Guid? targetId,
        long? targetVersion,
        DateTimeOffset deadlineUtc,
        ISystemClock clock,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            DataRightsDeadlineExecution<
                DataRightsRestrictionTargetResolutionResult> execution =
                await DataRightsDeadlineExecutor.ExecuteAsync(
                    clock,
                    deadlineUtc,
                    "DataRights.RestrictionTargetDeadlineExceeded",
                    token => contributor.ResolveReleaseTargetsAsync(
                        new DataRightsRestrictionTargetResolutionRequest(
                            DataRightsRestrictionContract.CurrentVersion,
                            dataRightsCase.ScopeId,
                            scope.PropertyId,
                            dataRightsCase.Id,
                            new BunkFy.Modules.DataRights.Contracts
                                .DataRightsSubjectCoordinate(
                                    subject.OwnerKey,
                                    subject.RecordType,
                                    subject.RecordId,
                                    subject.RecordVersion),
                            deadlineUtc,
                            scope.CaseType,
                            targetId,
                            targetVersion),
                        token),
                    cancellationToken).ConfigureAwait(false);
            return DataRightsRestrictionTargetResolution.Validate(
                execution.Value,
                execution.ObservedAtUtc,
                targetId,
                targetVersion);
        }
        catch (TimeoutException)
        {
            logger.LogWarning(
                "Data Rights restriction target owner {OwnerKey} exceeded its deadline.",
                contributor.OwnerKey);
            return Result.Failure<DataRightsRestrictionTargetSet>(
                DataRightsApplicationErrors.RestrictionOwnerRetryRequired);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Data Rights restriction target owner {OwnerKey} requires retry because {ExceptionType} was raised.",
                contributor.OwnerKey,
                exception.GetType().Name);
            return Result.Failure<DataRightsRestrictionTargetSet>(
                DataRightsApplicationErrors.RestrictionOwnerRetryRequired);
        }
    }
}
