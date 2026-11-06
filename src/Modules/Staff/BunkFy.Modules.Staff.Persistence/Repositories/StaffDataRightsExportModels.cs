namespace BunkFy.Modules.Staff.Persistence.Repositories;

using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.Models;
using BunkFy.Modules.Staff.Contracts;

internal sealed record StaffProfileDataRightsExport(
    Guid StaffMemberId,
    string DisplayName,
    string? LegalName,
    string? WorkEmail,
    string? WorkPhone,
    string? EmployeeNumber,
    string? JobTitle,
    string? Department,
    string? AuthSubjectId,
    StaffMemberState Status,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc,
    DateTimeOffset? SuspendedAtUtc,
    DateTimeOffset? DepartedAtUtc,
    DateOnly? DepartureEffectiveOn);

internal sealed record StaffAssignmentDataRightsExport(
    Guid AssignmentId,
    Guid StaffMemberId,
    Guid PropertyId,
    string? PropertyJobTitle,
    bool IsPrimary,
    bool IsCurrent,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    DateTimeOffset AssignedAtUtc,
    long AssignedAtVersion,
    DateTimeOffset? UnassignedAtUtc,
    long? UnassignedAtVersion)
{
    public long RecordVersion => this.UnassignedAtVersion ?? this.AssignedAtVersion;
}

internal sealed record StaffEmploymentGovernanceDataRightsExport(
    Guid StaffMemberId,
    int ContractVersion,
    long SelectedStaffVersion,
    string OperatingCountryCode,
    string PolicyId,
    int PolicyVersion,
    string DataRegionId,
    string TransferProfileId,
    string RetentionPolicyId,
    int RetentionPolicyVersion,
    string PolicyContentSha256,
    DateTimeOffset PolicyEffectiveAtUtc,
    DateTimeOffset PolicyExpiresAtUtc,
    DateTimeOffset EvaluatedAtUtc,
    string[] AcceptedAcknowledgements,
    DateTimeOffset ConfiguredAtUtc,
    long Version);

internal sealed record StaffDataHoldDataRightsExport(
    Guid HoldId,
    Guid StaffMemberId,
    string ReasonCode,
    StaffDataHoldState Status,
    DateTimeOffset PlacedAtUtc,
    DateTimeOffset? ReleasedAtUtc,
    long Version);

internal sealed record StaffMemberMutationOperationDataRightsExport(
    Guid OperationId,
    string ScopeId,
    Guid StaffMemberId,
    StaffMemberMutationKind Kind,
    long ExpectedVersion,
    string RequestFingerprint,
    StaffStatus ResultStatus,
    long ResultVersion,
    DateTimeOffset CompletedAtUtc);

internal sealed record StaffIdentityProvisioningAnchorDataRightsExport(
    Guid StaffMemberId,
    StaffIdentityProvisioningSourceKind SourceKind,
    Guid SourceId,
    Guid? ResolutionEventId,
    DateTimeOffset AnchoredAtUtc);

internal sealed record StaffIdentityProvisioningAnchorResolutionDataRightsExport(
    Guid StaffMemberId,
    StaffIdentityProvisioningSourceKind SourceKind,
    Guid SourceId,
    long WorkspaceApplicationVersion,
    StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition Disposition,
    Guid ResolutionEventId,
    DateTimeOffset ResolvedAtUtc);
