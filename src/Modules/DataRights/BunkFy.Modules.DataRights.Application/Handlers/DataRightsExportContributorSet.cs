namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Results;

internal static class DataRightsExportContributorSet
{
    public static Result<IReadOnlyDictionary<string, IDataRightsSubjectExportContributor>>
        Resolve(
            IEnumerable<IDataRightsSubjectExportContributor> contributors,
            DataRightsCaseType caseType,
            IEnumerable<DataRightsSubjectCoordinate> subjects)
    {
        if (contributors is null ||
            subjects is null ||
            caseType is not DataRightsCaseType.GuestRights and
                not DataRightsCaseType.StaffRights)
        {
            return CatalogInvalid();
        }

        try
        {
            IDataRightsSubjectExportContributor[] catalog =
                contributors.ToArray();
            if (catalog.Any(contributor => contributor is null) ||
                catalog.Any(contributor => !IsValid(contributor)))
            {
                return CatalogInvalid();
            }

            IDataRightsSubjectExportContributor[] available = catalog
                .Where(contributor =>
                    contributor.SupportedCaseTypes.Contains(caseType))
                .ToArray();
            if (available.GroupBy(
                    contributor => NormalizeOwner(contributor.OwnerKey),
                    StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
            {
                return CatalogInvalid();
            }

            Dictionary<string, IDataRightsSubjectExportContributor> byOwner =
                available.ToDictionary(
                    contributor => NormalizeOwner(contributor.OwnerKey),
                    StringComparer.Ordinal);
            string[] requiredOwners = subjects
                .Select(subject => NormalizeOwner(subject.OwnerKey))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (requiredOwners.Length == 0 ||
                requiredOwners.Any(owner => !byOwner.ContainsKey(owner)))
            {
                return OwnerUnavailable();
            }

            return Result.Success<IReadOnlyDictionary<
                string,
                IDataRightsSubjectExportContributor>>(byOwner);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            return CatalogInvalid();
        }
    }

    private static bool IsValid(
        IDataRightsSubjectExportContributor contributor)
    {
        try
        {
            string owner = NormalizeOwner(contributor.OwnerKey);
            IReadOnlyCollection<DataRightsCaseType>? supported =
                contributor.SupportedCaseTypes;
            if (owner.Length is 0 or > DataRightsExportLimits.OwnerKeyMaxLength ||
                supported is null ||
                supported.Count is <= 0 or > 2 ||
                supported.Any(caseType => caseType is not
                    DataRightsCaseType.GuestRights and not
                    DataRightsCaseType.StaffRights) ||
                supported.Distinct().Count() != supported.Count)
            {
                return false;
            }

            _ = DataRightsExportSchemaValidator.Validate(
                contributor.Descriptor,
                owner);
            return true;
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static string NormalizeOwner(string? ownerKey) =>
        ownerKey?.Trim().ToLowerInvariant() ?? string.Empty;

    private static Result<IReadOnlyDictionary<
        string,
        IDataRightsSubjectExportContributor>> CatalogInvalid() =>
        Result.Failure<IReadOnlyDictionary<
            string,
            IDataRightsSubjectExportContributor>>(
                DataRightsApplicationErrors.ExportOwnerCatalogInvalid);

    private static Result<IReadOnlyDictionary<
        string,
        IDataRightsSubjectExportContributor>> OwnerUnavailable() =>
        Result.Failure<IReadOnlyDictionary<
            string,
            IDataRightsSubjectExportContributor>>(
                DataRightsApplicationErrors.ExportOwnerUnavailable);
}
