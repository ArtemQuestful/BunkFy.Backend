namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Results;

internal static class DataRightsRestrictionContributorSet
{
    public static Result<IDataRightsRestrictionContributor> Resolve(
        IEnumerable<IDataRightsRestrictionContributor> contributors,
        string ownerKey)
    {
        string requiredOwner = Normalize(ownerKey);
        if (requiredOwner.Length == 0)
        {
            return OwnerUnavailable();
        }

        try
        {
            IDataRightsRestrictionContributor[] catalog = contributors.ToArray();
            if (catalog.Any(contributor => contributor is null) ||
                catalog.Any(contributor => !IsValid(contributor)) ||
                catalog.GroupBy(
                        contributor => Normalize(contributor.OwnerKey),
                        StringComparer.Ordinal)
                    .Any(group => group.Count() != 1))
            {
                return CatalogInvalid();
            }

            IDataRightsRestrictionContributor? match = catalog.SingleOrDefault(
                contributor => string.Equals(
                    Normalize(contributor.OwnerKey),
                    requiredOwner,
                    StringComparison.Ordinal));
            return match is null
                ? OwnerUnavailable()
                : Result.Success(match);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            return CatalogInvalid();
        }
    }

    private static bool IsValid(IDataRightsRestrictionContributor contributor)
    {
        string owner = Normalize(contributor.OwnerKey);
        return contributor.ContractVersion ==
                DataRightsRestrictionContract.CurrentVersion &&
            owner.Length is > 0 and <=
                DataRightsRestrictionContract.OwnerKeyMaxLength &&
            string.Equals(contributor.OwnerKey, owner, StringComparison.Ordinal);
    }

    private static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static Result<IDataRightsRestrictionContributor> CatalogInvalid() =>
        Result.Failure<IDataRightsRestrictionContributor>(
            DataRightsApplicationErrors.RestrictionOwnerCatalogInvalid);

    private static Result<IDataRightsRestrictionContributor> OwnerUnavailable() =>
        Result.Failure<IDataRightsRestrictionContributor>(
            DataRightsApplicationErrors.RestrictionOwnerUnavailable);
}
