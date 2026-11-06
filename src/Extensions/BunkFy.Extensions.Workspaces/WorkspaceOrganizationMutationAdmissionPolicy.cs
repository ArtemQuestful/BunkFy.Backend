namespace BunkFy.Extensions.Workspaces;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Modules.Organizations.Contracts;

internal sealed class WorkspaceOrganizationMutationAdmissionPolicy(
    IWorkspaceOperationalAdmissionPolicy operationalAdmission,
    IStaffOperationalIdentityReader staff)
    : IOrganizationMutationAdmissionPolicy
{
    public async ValueTask<OrganizationMutationAdmissionDecision> EvaluateAsync(
        OrganizationMutationAdmissionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        WorkspaceOperationalAdmissionDecision decision =
            await operationalAdmission.EvaluateAsync(
                context.OrganizationId.ToString("D"),
                cancellationToken).ConfigureAwait(false);
        OrganizationMutationAdmissionDecision admission = decision.Outcome switch
        {
            WorkspaceOperationalAdmissionOutcome.Allowed =>
                OrganizationMutationAdmissionDecision.Allowed,
            WorkspaceOperationalAdmissionOutcome.Restricted =>
                OrganizationMutationAdmissionDecision.Denied,
            _ => OrganizationMutationAdmissionDecision.Unavailable
        };
        if (admission != OrganizationMutationAdmissionDecision.Allowed ||
            context.Operation !=
                OrganizationMutationAdmissionOperation.TransferOwnership)
        {
            return admission;
        }

        string targetSubjectId = context.TargetSubjectId?.Trim() ?? string.Empty;
        if (!IsValidSubject(targetSubjectId))
        {
            return OrganizationMutationAdmissionDecision.Denied;
        }

        StaffOperationalIdentitySnapshot? target = await staff.FindAsync(
                context.OrganizationId.ToString("D"),
                targetSubjectId,
                cancellationToken)
            .ConfigureAwait(false);
        return IsEligibleTarget(target, targetSubjectId)
            ? OrganizationMutationAdmissionDecision.Allowed
            : OrganizationMutationAdmissionDecision.Denied;
    }

    private static bool IsValidSubject(string subjectId) =>
        subjectId.Length is > 0 and <= StaffContractLimits.AuthSubjectIdMaxLength &&
        !subjectId.Any(char.IsControl);

    private static bool IsEligibleTarget(
        StaffOperationalIdentitySnapshot? target,
        string targetSubjectId) =>
        target is
        {
            StaffMemberId: var staffMemberId,
            Status: StaffStatus.Active,
            Version: > 0
        } &&
        staffMemberId != Guid.Empty &&
        string.Equals(
            target.AuthSubjectId,
            targetSubjectId,
            StringComparison.Ordinal);
}
