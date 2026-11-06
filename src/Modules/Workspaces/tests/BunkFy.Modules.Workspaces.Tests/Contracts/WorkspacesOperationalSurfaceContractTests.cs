namespace BunkFy.Modules.Workspaces.Tests.Contracts;

using BunkFy.Modules.Workspaces.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspacesOperationalSurfaceContractTests
{
    [Fact]
    public void Legacy_list_retains_full_dto_wire_identity_and_summary_is_a_separate_contract()
    {
        Assert.Equal(typeof(IReadOnlyList<WorkspaceStaffOnboardingDto>),
            typeof(WorkspaceStaffOnboardingListResponse).GetProperty("Items")!.PropertyType);
        Assert.Equal(typeof(IReadOnlyList<WorkspaceStaffOnboardingActionableSummaryDto>),
            typeof(WorkspaceStaffOnboardingSummaryListResponse).GetProperty("Items")!.PropertyType);
        WorkspaceStaffOnboardingDto applicant = new(
            Guid.NewGuid(), Guid.NewGuid(), WorkspaceStaffOnboardingSourceKind.EnrollmentLink,
            Guid.NewGuid(), Guid.NewGuid(), 1, "applicant-subject", "verified@example.test",
            "Applicant Profile", "Applicant Legal", "work@example.test", "+1 555 0100",
            "EMP-710", "Manager", "Operations", WorkspaceStaffOnboardingStatus.PendingApproval,
            null, 2, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        string json = System.Text.Json.JsonSerializer.Serialize(
            new WorkspaceStaffOnboardingListResponse([applicant], 1, 25, false),
            System.Text.Json.JsonSerializerOptions.Web);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
        System.Text.Json.JsonElement item = document.RootElement.GetProperty("items")[0];
        Assert.Equal("Applicant Profile", item.GetProperty("displayName").GetString());
        Assert.Equal("verified@example.test", item.GetProperty("verifiedAccountEmail").GetString());
        Assert.Equal("work@example.test", item.GetProperty("workEmail").GetString());
        Assert.Equal("Manager", item.GetProperty("jobTitle").GetString());
        Assert.False(item.TryGetProperty("hasStaffTarget", out _));
    }

    [Fact]
    public void Actionable_onboarding_summary_has_no_staged_profile_or_subject_coordinates()
    {
        string[] members = typeof(WorkspaceStaffOnboardingActionableSummaryDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains(
            nameof(WorkspaceStaffOnboardingActionableSummaryDto.HasStaffTarget),
            members);
        foreach (string forbidden in new[]
                 {
                     "SubjectId",
                     "VerifiedAccountEmail",
                     "DisplayName",
                     "LegalName",
                     "WorkEmail",
                     "WorkPhone",
                     "EmployeeNumber",
                     "JobTitle",
                     "Department",
                     "StaffMemberId"
                 })
        {
            Assert.DoesNotContain(forbidden, members);
        }
    }

    [Theory]
    [InlineData(typeof(WorkspaceStaffOnboardingListResponse))]
    [InlineData(typeof(WorkspaceStaffOnboardingSummaryListResponse))]
    [InlineData(typeof(WorkspaceStaffAccessProcessListResponse))]
    [InlineData(typeof(WorkspaceStaffJoinSourceListResponse))]
    public void Workspaces_owned_operational_directories_report_continuation(Type responseType)
    {
        System.Reflection.PropertyInfo property = Assert.Single(
            responseType.GetProperties(),
            candidate => string.Equals(
                candidate.Name,
                "HasMore",
                StringComparison.Ordinal));

        Assert.Equal(typeof(bool), property.PropertyType);
    }
}
