namespace BunkFy.Modules.Properties.Application.Validation;

using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Cqrs;

internal sealed class ListPropertyTimeZoneCatalogQueryValidator
    : IQueryValidator<ListPropertyTimeZoneCatalogQuery>
{
    public IEnumerable<string> Validate(
        ListPropertyTimeZoneCatalogQuery query)
    {
        if (query.PageSize is <= 0 or
            > PropertiesContractLimits.PropertyTimeZonePageSizeMax)
        {
            yield return "Page size must be between 1 and 100.";
        }

        if (query.Search?.Length > 128 ||
            query.Search?.Any(char.IsControl) == true)
        {
            yield return
                "Search must be 128 characters or fewer and cannot contain control characters.";
        }

        if (!string.IsNullOrWhiteSpace(query.CountryCode) &&
            (query.CountryCode.Trim().Length != 2 ||
             !query.CountryCode.Trim().All(char.IsAsciiLetter)))
        {
            yield return "Country code must contain two ASCII letters.";
        }

        if (query.Cursor?.Length > 2048)
        {
            yield return "Cursor must be 2048 characters or fewer.";
        }
    }
}
