namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DataRightsMutationContributorSetTests
{
    [Fact]
    public void Restriction_catalog_distinguishes_missing_owner_from_invalid_composition()
    {
        var missing = DataRightsRestrictionContributorSet.Resolve(
            [new RestrictionContributor("staff")],
            "guests");
        var duplicate = DataRightsRestrictionContributorSet.Resolve(
            [
                new RestrictionContributor("guests"),
                new RestrictionContributor("guests")
            ],
            "guests");
        var malformed = DataRightsRestrictionContributorSet.Resolve(
            [new RestrictionContributor(" Guests ")],
            "guests");

        Assert.Equal(
            DataRightsApplicationErrors.RestrictionOwnerUnavailable,
            missing.Error);
        Assert.Equal(
            DataRightsApplicationErrors.RestrictionOwnerCatalogInvalid,
            duplicate.Error);
        Assert.Equal(
            DataRightsApplicationErrors.RestrictionOwnerCatalogInvalid,
            malformed.Error);
    }

    [Fact]
    public void Correction_catalog_requires_one_canonical_current_policy_per_coordinate()
    {
        var resolved = DataRightsCorrectionPolicyContributorSet.Resolve(
            [new CorrectionPolicy("guests", "guest-profile")],
            "guests",
            "guest-profile");
        var missing = DataRightsCorrectionPolicyContributorSet.Resolve(
            [new CorrectionPolicy("staff", "staff-member")],
            "guests",
            "guest-profile");
        var duplicate = DataRightsCorrectionPolicyContributorSet.Resolve(
            [
                new CorrectionPolicy("guests", "guest-profile"),
                new CorrectionPolicy("guests", "guest-profile")
            ],
            "guests",
            "guest-profile");
        var malformed = DataRightsCorrectionPolicyContributorSet.Resolve(
            [new CorrectionPolicy(
                "guests",
                "guest-profile",
                fieldPolicyKey: "guest correction values")],
            "guests",
            "guest-profile");

        Assert.True(resolved.IsSuccess, resolved.Error.Code);
        Assert.Equal(DataRightsApplicationErrors.CorrectionOwnerUnavailable, missing.Error);
        Assert.Equal(DataRightsApplicationErrors.CorrectionOwnerCatalogInvalid, duplicate.Error);
        Assert.Equal(DataRightsApplicationErrors.CorrectionOwnerCatalogInvalid, malformed.Error);
    }

    private sealed class RestrictionContributor(string ownerKey)
        : IDataRightsRestrictionContributor
    {
        public string OwnerKey => ownerKey;
        public int ContractVersion => DataRightsRestrictionContract.CurrentVersion;

        public Task<DataRightsRestrictionTargetResolutionResult>
            ResolveReleaseTargetsAsync(
                DataRightsRestrictionTargetResolutionRequest request,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DataRightsRestrictionContributionResult> ExecuteAsync(
            DataRightsRestrictionContributionRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CorrectionPolicy(
        string ownerKey,
        string recordType,
        string fieldPolicyKey = "guests.guest-profile.correction.v1")
        : IDataRightsCorrectionPolicyContributor
    {
        public int ContractVersion => DataRightsCorrectionContract.CurrentVersion;
        public string OwnerKey => ownerKey;
        public string RecordType => recordType;
        public string FieldPolicyKey => fieldPolicyKey;
    }
}
