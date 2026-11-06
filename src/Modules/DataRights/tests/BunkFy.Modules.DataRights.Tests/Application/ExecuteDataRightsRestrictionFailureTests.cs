namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed partial class ExecuteDataRightsRestrictionCommandHandlerTests
{
    [Fact]
    public async Task Declared_owner_failure_preserves_case_and_requests_retry()
    {
        DataRightsCase dataRightsCase = CreateApprovedCase(
            DataRightsRestrictionAction.Apply);
        ExecuteDataRightsRestrictionCommandHandler handler = CreateHandler(
            dataRightsCase,
            DataRightsRestrictionContributionResult.Failed("owner.timeout"));

        var result = await handler.HandleAsync(
            CommandFor(dataRightsCase),
            CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.RestrictionOwnerRetryRequired,
            result.Error);
        Assert.Equal(DataRightsCaseState.Approved, dataRightsCase.Status);
        Assert.Null(dataRightsCase.RestrictionExecutionProof);
    }

    [Fact]
    public async Task Well_formed_owner_blocker_preserves_case_and_returns_conflict()
    {
        DataRightsCase dataRightsCase = CreateApprovedCase(
            DataRightsRestrictionAction.Apply);
        ExecuteDataRightsRestrictionCommandHandler handler = CreateHandler(
            dataRightsCase,
            DataRightsRestrictionContributionResult.Blocked("owner.legal-hold"));

        var result = await handler.HandleAsync(
            CommandFor(dataRightsCase),
            CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.RestrictionExecutionBlocked,
            result.Error);
        Assert.Equal(DataRightsCaseState.Approved, dataRightsCase.Status);
        Assert.Null(dataRightsCase.RestrictionExecutionProof);
    }

    [Fact]
    public async Task Malformed_declared_failure_is_an_invalid_owner_result()
    {
        DataRightsCase dataRightsCase = CreateApprovedCase(
            DataRightsRestrictionAction.Apply);
        ExecuteDataRightsRestrictionCommandHandler handler = CreateHandler(
            dataRightsCase,
            new DataRightsRestrictionContributionResult(
                DataRightsRestrictionContract.CurrentVersion,
                DataRightsRestrictionContributionStatus.Failed,
                OwnerProof: null,
                OutcomeCode: "contains spaces"));

        var result = await handler.HandleAsync(
            CommandFor(dataRightsCase),
            CancellationToken.None);

        Assert.Equal(
            DataRightsApplicationErrors.RestrictionOwnerProofInvalid,
            result.Error);
        Assert.Equal(DataRightsCaseState.Approved, dataRightsCase.Status);
        Assert.Null(dataRightsCase.RestrictionExecutionProof);
    }

    private static ExecuteDataRightsRestrictionCommandHandler CreateHandler(
        DataRightsCase dataRightsCase,
        DataRightsRestrictionContributionResult ownerResult) =>
        new(
            DataRightsMutationTestSupport.Case(
                new StubCaseRepository(dataRightsCase)),
            new RecordingApprovalGate(
                BunkFy.Modules.DataRights.Contracts.Authorization
                    .DataRightsOperationApprovalResult.Approved),
            [new RecordingContributor(ownerResult)],
            new TestClock(),
            NullLogger<ExecuteDataRightsRestrictionCommandHandler>.Instance);

    private static ExecuteDataRightsRestrictionCommand CommandFor(
        DataRightsCase dataRightsCase) =>
        new(
            DataRightsCaseScope.ForProperty(dataRightsCase.PropertyId!.Value),
            dataRightsCase.Id,
            Guid.NewGuid(),
            dataRightsCase.Version,
            "user:executor");
}
