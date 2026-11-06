namespace BunkFy.Modules.Staff.Contracts;

public sealed record StaffAccountDirectoryResponse(
    IReadOnlyList<StaffAccountDirectoryEntryDto> Items);
