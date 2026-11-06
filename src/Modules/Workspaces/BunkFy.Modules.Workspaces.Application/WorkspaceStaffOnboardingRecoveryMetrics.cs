namespace BunkFy.Modules.Workspaces.Application;

using System.Diagnostics.Metrics;
using BunkFy.Modules.Workspaces.Domain;

internal static class WorkspaceStaffOnboardingRecoveryMetrics
{
    internal const string MeterName =
        "bunkfy.workspaces.staff_onboarding_recovery";
    internal const string CounterName =
        "bunkfy.workspaces.staff_onboarding_recovery.outcomes";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Outcomes = Meter.CreateCounter<long>(
        CounterName,
        unit: "{attempt}",
        description: "Bounded workspace staff onboarding recovery outcomes.");

    public static void Record(
        WorkspaceStaffOnboardingRecoveryReceipt receipt,
        bool replay) => Outcomes.Add(
        1,
        new KeyValuePair<string, object?>(
            "outcome",
            Outcome(receipt.Outcome)),
        new KeyValuePair<string, object?>(
            "reason",
            Reason(receipt.Reason)),
        new KeyValuePair<string, object?>("replay", replay));

    private static string Outcome(
        WorkspaceStaffOnboardingRecoveryOutcome outcome) => outcome switch
        {
            WorkspaceStaffOnboardingRecoveryOutcome.Recovered => "recovered",
            WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired =>
                "review-required",
            WorkspaceStaffOnboardingRecoveryOutcome.RetryLater => "retry-later",
            _ => "unknown"
        };

    private static string Reason(
        WorkspaceStaffOnboardingRecoveryReason reason) => reason switch
        {
            WorkspaceStaffOnboardingRecoveryReason.ExactStateRecovered =>
                "exact-state-recovered",
            WorkspaceStaffOnboardingRecoveryReason
                .LegacyMembershipBindingMissing =>
                "legacy-membership-binding-missing",
            WorkspaceStaffOnboardingRecoveryReason.MembershipConflict =>
                "membership-conflict",
            WorkspaceStaffOnboardingRecoveryReason.MembershipUnavailable =>
                "membership-unavailable",
            WorkspaceStaffOnboardingRecoveryReason.StaffConflict =>
                "staff-conflict",
            WorkspaceStaffOnboardingRecoveryReason.StaffUnavailable =>
                "staff-unavailable",
            WorkspaceStaffOnboardingRecoveryReason.AccessConflict =>
                "access-conflict",
            WorkspaceStaffOnboardingRecoveryReason.AccessUnavailable =>
                "access-unavailable",
            WorkspaceStaffOnboardingRecoveryReason.ProcessingRestricted =>
                "processing-restricted",
            _ => "unknown"
        };
}
