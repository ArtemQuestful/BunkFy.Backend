namespace BunkFy.Modules.Workspaces.Tests.Api;

using BunkFy.Modules.Workspaces.Api;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Results;
using Microsoft.AspNetCore.Http;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffOnboardingEndpointOutcomeTests
{
    [Fact]
    public void Submission_authority_moved_success_maps_to_existing_conflict()
    {
        Result<WorkspaceStaffOnboardingDto> mapped =
            WorkspaceStaffOnboardingEndpoints.MapSubmissionOutcome(
                Result.Success(
                    WorkspaceStaffOnboardingSubmissionOutcome
                        .AuthorityMovedToStaff()));

        Assert.True(mapped.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .ProfileMutationAuthorityUnavailable,
            mapped.Error);
        Assert.Equal(
            StatusCodes.Status409Conflict,
            WorkspacesApiEndpointSupport.ErrorStatusCodes.GetStatusCode(
                mapped.Error));
    }

    [Fact]
    public void Correction_authority_moved_success_maps_to_existing_conflict()
    {
        Result<WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto> mapped =
            WorkspaceStaffOnboardingDataRightsEndpoints.MapCorrectionOutcome(
                Result.Success(
                    WorkspaceStaffOnboardingDataRightsCorrectionOutcome
                        .AuthorityMovedToStaff()));

        Assert.True(mapped.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .CorrectionTargetUnavailable,
            mapped.Error);
        Assert.Equal(
            StatusCodes.Status409Conflict,
            WorkspacesApiEndpointSupport.ErrorStatusCodes.GetStatusCode(
                mapped.Error));
    }
}
