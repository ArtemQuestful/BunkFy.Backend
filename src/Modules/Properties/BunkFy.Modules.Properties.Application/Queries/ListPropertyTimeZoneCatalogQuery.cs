namespace BunkFy.Modules.Properties.Application.Queries;

using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Cqrs;

public sealed record ListPropertyTimeZoneCatalogQuery(
    string? Search,
    string? CountryCode,
    string? Cursor,
    int PageSize = 50)
    : IQuery<PropertyTimeZoneCatalogPageDto>;
