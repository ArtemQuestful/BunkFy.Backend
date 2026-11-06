namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Results;

internal static class DataRightsSubjectContributorSet
{
    public static Result<IReadOnlyCollection<IDataRightsSubjectDiscoveryContributor>> Order(
        IEnumerable<IDataRightsSubjectDiscoveryContributor> contributors,
        DataRightsCaseType caseType)
    {
        if (contributors is null || caseType == DataRightsCaseType.Unknown)
        {
            return Result.Failure<IReadOnlyCollection<IDataRightsSubjectDiscoveryContributor>>(
                DataRightsApplicationErrors.SubjectOwnerCatalogInvalid);
        }

        IDataRightsSubjectDiscoveryContributor[] supplied = contributors.ToArray();
        if (supplied.Any(contributor =>
                contributor is null ||
                string.IsNullOrWhiteSpace(contributor.OwnerKey) ||
                contributor.OwnerKey.Trim().Length >
                    DataRightsSubjectDiscoveryLimits.OwnerKeyMaxLength ||
                !HasValidCaseTypes(contributor.SupportedCaseTypes)) ||
            supplied.GroupBy(
                    contributor => contributor.OwnerKey.Trim().ToLowerInvariant(),
                    StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
        {
            return Result.Failure<IReadOnlyCollection<IDataRightsSubjectDiscoveryContributor>>(
                DataRightsApplicationErrors.SubjectOwnerCatalogInvalid);
        }

        IDataRightsSubjectDiscoveryContributor[] ordered = supplied
            .Where(contributor => contributor.SupportedCaseTypes.Contains(caseType))
            .OrderBy(
                contributor => contributor.OwnerKey.Trim().ToLowerInvariant(),
                StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0)
        {
            return Result.Failure<IReadOnlyCollection<IDataRightsSubjectDiscoveryContributor>>(
                DataRightsApplicationErrors.SubjectOwnerUnavailable);
        }

        return Result.Success<IReadOnlyCollection<IDataRightsSubjectDiscoveryContributor>>(ordered);
    }

    public static Result<IDataRightsSubjectDiscoveryContributor> Find(
        IEnumerable<IDataRightsSubjectDiscoveryContributor> contributors,
        string ownerKey,
        DataRightsCaseType caseType)
    {
        string normalizedOwner = ownerKey?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedOwner.Length is 0 or >
            DataRightsSubjectDiscoveryLimits.OwnerKeyMaxLength)
        {
            return Result.Failure<IDataRightsSubjectDiscoveryContributor>(
                DataRightsApplicationErrors.SubjectCoordinateInvalid);
        }

        Result<IReadOnlyCollection<IDataRightsSubjectDiscoveryContributor>> ordered =
            Order(contributors, caseType);
        if (ordered.IsFailure)
        {
            return Result.Failure<IDataRightsSubjectDiscoveryContributor>(ordered.Error);
        }

        IDataRightsSubjectDiscoveryContributor[] matches = ordered.Value
            .Where(contributor => string.Equals(
                contributor.OwnerKey.Trim(),
                normalizedOwner,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1
            ? Result.Success(matches[0])
            : Result.Failure<IDataRightsSubjectDiscoveryContributor>(
                DataRightsApplicationErrors.SubjectOwnerUnavailable);
    }

    private static bool HasValidCaseTypes(
        IReadOnlyCollection<DataRightsCaseType>? caseTypes) =>
        caseTypes is not null &&
        caseTypes.Count > 0 &&
        caseTypes.All(caseType => caseType != DataRightsCaseType.Unknown) &&
        caseTypes.Distinct().Count() == caseTypes.Count;
}
