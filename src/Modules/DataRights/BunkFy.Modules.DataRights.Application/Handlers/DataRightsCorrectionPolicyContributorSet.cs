namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Results;

internal static class DataRightsCorrectionPolicyContributorSet
{
    public static Result<IDataRightsCorrectionPolicyContributor> Resolve(
        IEnumerable<IDataRightsCorrectionPolicyContributor> contributors,
        string ownerKey,
        string recordType)
    {
        string requiredOwner = Normalize(ownerKey);
        string requiredRecordType = Normalize(recordType);
        if (requiredOwner.Length == 0 || requiredRecordType.Length == 0)
        {
            return OwnerUnavailable();
        }

        try
        {
            IDataRightsCorrectionPolicyContributor[] catalog = contributors.ToArray();
            if (catalog.Any(contributor => contributor is null) ||
                catalog.Any(contributor => !IsValid(contributor)) ||
                catalog.GroupBy(
                        contributor => Key(contributor),
                        StringComparer.Ordinal)
                    .Any(group => group.Count() != 1))
            {
                return CatalogInvalid();
            }

            IDataRightsCorrectionPolicyContributor? match = catalog.SingleOrDefault(
                contributor => string.Equals(
                    Normalize(contributor.OwnerKey),
                    requiredOwner,
                    StringComparison.Ordinal) &&
                string.Equals(
                    Normalize(contributor.RecordType),
                    requiredRecordType,
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

    private static bool IsValid(IDataRightsCorrectionPolicyContributor contributor)
    {
        string owner = Normalize(contributor.OwnerKey);
        string recordType = Normalize(contributor.RecordType);
        string fieldPolicy = Normalize(contributor.FieldPolicyKey);
        return contributor.ContractVersion == DataRightsCorrectionContract.CurrentVersion &&
            owner.Length is > 0 and <= DataRightsCorrectionContract.OwnerKeyMaxLength &&
            recordType.Length is > 0 and <= DataRightsCorrectionContract.RecordTypeMaxLength &&
            fieldPolicy.Length is > 0 and <= DataRightsCorrectionContract.FieldPolicyKeyMaxLength &&
            fieldPolicy.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '-') &&
            string.Equals(contributor.OwnerKey, owner, StringComparison.Ordinal) &&
            string.Equals(contributor.RecordType, recordType, StringComparison.Ordinal) &&
            string.Equals(contributor.FieldPolicyKey, fieldPolicy, StringComparison.Ordinal);
    }

    private static string Key(IDataRightsCorrectionPolicyContributor contributor) =>
        $"{Normalize(contributor.OwnerKey)}\u001f{Normalize(contributor.RecordType)}";

    private static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static Result<IDataRightsCorrectionPolicyContributor> CatalogInvalid() =>
        Result.Failure<IDataRightsCorrectionPolicyContributor>(
            DataRightsApplicationErrors.CorrectionOwnerCatalogInvalid);

    private static Result<IDataRightsCorrectionPolicyContributor> OwnerUnavailable() =>
        Result.Failure<IDataRightsCorrectionPolicyContributor>(
            DataRightsApplicationErrors.CorrectionOwnerUnavailable);
}
