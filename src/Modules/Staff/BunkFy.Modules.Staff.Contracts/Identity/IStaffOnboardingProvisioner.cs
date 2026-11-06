namespace BunkFy.Modules.Staff.Contracts;

public interface IStaffOnboardingProvisioner
{
    Task<StaffOnboardingProvisioningResult> ProvisionAsync(
        StaffOnboardingProvisioningRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record StaffOnboardingProvisioningRequest(
    Guid OperationId,
    string AuthSubjectId,
    string DisplayName,
    string? LegalName,
    string? WorkEmail,
    string? WorkPhone,
    string? EmployeeNumber,
    string? JobTitle,
    string? Department,
    string ActorId);

public sealed record StaffOnboardingProvisioningResult(
    bool IsSuccess,
    Guid? StaffMemberId,
    string? ErrorCode,
    Guid? ResolutionEventId = null);
