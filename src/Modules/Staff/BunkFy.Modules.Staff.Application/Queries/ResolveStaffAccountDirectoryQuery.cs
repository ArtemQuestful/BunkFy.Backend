namespace BunkFy.Modules.Staff.Application.Queries;

using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Cqrs;

public sealed record ResolveStaffAccountDirectoryQuery(
    IReadOnlyList<string> AuthSubjectIds)
    : IQuery<StaffAccountDirectoryResponse>;
