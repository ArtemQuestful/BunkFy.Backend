namespace BunkFy.Modules.Staff.Api.Requests;

public sealed record StaffAccountDirectoryResolveRequest(
    IReadOnlyList<string> AuthSubjectIds);
