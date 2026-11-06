namespace BunkFy.Modules.Workspaces.Contracts;

public static class WorkspacesDataRightsCoordinates
{
    public const string Owner = WorkspacesModuleMetadata.Name;
    public const string StaffOnboardingRecordType = "staff-onboarding";
    public const string StaffOnboardingRecoveryReceiptRecordType =
        "staff-onboarding-recovery-receipt";
    public const string StaffOnboardingCorrectionFieldPolicyKey =
        "workspaces.staff-onboarding.applicant-correction.v1";
    public const string StaffAccessProcessRecordType =
        "staff-access-process";
    public const string StaffCorrelationCompanionContributorKey =
        "workspaces.staff-correlation";
    public const string StaffCorrelationStateBindingKey =
        "workspaces.staff-correlation";
    public const string StaffAccessPlanRecordType = "staff-access-plan";
    public const string StaffRetentionCorrelationReceiptRecordType =
        "staff-retention-correlation-receipt";
}
