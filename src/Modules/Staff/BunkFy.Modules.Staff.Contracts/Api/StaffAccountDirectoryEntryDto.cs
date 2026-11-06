namespace BunkFy.Modules.Staff.Contracts;

public sealed record StaffAccountDirectoryEntryDto(
    Guid StaffMemberId,
    string AuthSubjectId,
    string DisplayName,
    string? JobTitle,
    StaffStatus Status,
    long Version);
