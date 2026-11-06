namespace BunkFy.Modules.Properties.Application.Queries;

using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Cqrs;

public sealed record ListPropertyTimeZoneComplianceQuery(
    string? Cursor,
    int PageSize = 50)
    : IQuery<PropertyTimeZoneCompliancePageDto>;
