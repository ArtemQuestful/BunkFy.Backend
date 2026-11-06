namespace BunkFy.Modules.Guests.Tests.Application;

using BunkFy.DataGovernance;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.TimeZones;

internal static class GuestRetentionTestData
{
    public static readonly DateTimeOffset Now =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    public static GuestRetentionCandidateSnapshot Snapshot(
        PropertyGovernancePolicyBinding binding,
        GuestStayStatus status,
        DateOnly terminalDate,
        long projectionOrdinal = 8,
        long guestVersion = 4,
        Guid? guestId = null,
        Guid? propertyId = null)
    {
        Guid resolvedPropertyId = propertyId ?? Guid.NewGuid();
        return new(
            guestId ?? Guid.NewGuid(),
            guestVersion,
            projectionOrdinal,
            GuestProfileState.Active,
            resolvedPropertyId,
            Stays:
            [
                new(
                    resolvedPropertyId,
                    ProjectionSupported: true,
                    HasOperationalStay: status is
                        GuestStayStatus.PendingAllocation or
                        GuestStayStatus.Confirmed or
                        GuestStayStatus.CancellationPending or
                        GuestStayStatus.CheckedIn or
                        GuestStayStatus.NoShowPending or
                        GuestStayStatus.CheckoutPending,
                    LatestTerminalBusinessDate: status is
                        GuestStayStatus.AllocationRejected or
                        GuestStayStatus.Cancelled or
                        GuestStayStatus.NoShow or
                        GuestStayStatus.CheckedOut
                            ? terminalDate
                            : null)
            ],
            ActiveHolds: [],
            Properties:
            [
                new(
                    resolvedPropertyId,
                    IsKnown: true,
                    PropertyStatus.Active,
                    PropertyProcessingStatus.Enabled,
                    "Etc/UTC",
                    "Etc/UTC",
                    PropertyTimeZoneStatus.Canonical,
                    TimeZoneCatalog.Default.CatalogVersion,
                    GuestPropertyTimeZoneEvidenceSource.Generic,
                    TimeZoneEvidenceSourceVersion: 3,
                    TopologySourceVersion: 3,
                    PolicySourceVersion: 4,
                    binding)
            ],
            AssociationOverflowed: false);
    }

    public static GuestRetentionPolicyFixture CreatePolicy()
    {
        CountryPolicyPackDocument document = new()
        {
            SchemaVersion = 1,
            PolicyId = "gb-hostel",
            PolicyVersion = 1,
            OperatingCountryCode = "GB",
            ApprovalState = CountryPolicyApprovalState.Approved,
            EffectiveAtUtc = Now.AddYears(-2),
            ExpiresAtUtc = Now.AddYears(2),
            AccommodationTypes = ["hostel"],
            GuestCategories = ["ordinary-guest"],
            FieldRules =
            [
                new()
                {
                    FieldPolicyKey = "guest.primary-name",
                    GuestCategory = "ordinary-guest",
                    Requirement =
                        CountryPolicyFieldRequirement.Required,
                    PurposeCodes = ["guest-profile-retention"]
                }
            ],
            PurposeRules =
            [
                new()
                {
                    PurposeCode = "guest-profile-retention",
                    LegalRuleReferenceKeys = ["storage-limitation"],
                    AllowedSurfaces = [CountryPolicySurface.Retention],
                    AllowedSourceProvenance = ["retention-worker"]
                }
            ],
            RetentionRules =
            [
                new()
                {
                    RetentionPolicyId = "guest-operational",
                    RetentionPolicyVersion = 1,
                    DataClass = "guest-operational",
                    Trigger = "stay-ended",
                    Period = "365.00:00:00"
                }
            ],
            RightsRule = new()
            {
                Registration = "standard-registration",
                Export = "standard-export",
                Correction = "standard-correction",
                Restriction = "standard-restriction",
                Erasure = "review-before-erasure"
            },
            Restrictions = new()
            {
                Minors = "not-assessed",
                Documents = "document-images-prohibited",
                SpecialCategoryData = "prohibited"
            },
            PermittedDataRegions = ["eu-west-2"],
            PermittedTransferProfiles = ["uk-no-transfer"],
            RequiredAcknowledgements = [],
            Approval = new()
            {
                OwnerReference = "private-owner",
                ReviewerReference = "private-reviewer",
                ReviewedAtUtc = Now.AddYears(-2),
                Sources =
                [
                    new()
                    {
                        ReferenceId = "source-1",
                        Uri = "https://example.test/policy"
                    }
                ],
                DetachedSignatureReference = "signature-1"
            }
        };
        CountryPolicyPackValidator.ValidateAndThrow(document);
        string digest = new('a', 64);
        CountryPolicyRegistry registry = CountryPolicyRegistry.Create(
            [new(document, digest)],
            [
                new(
                    document.OperatingCountryCode,
                    document.PolicyId,
                    document.PolicyVersion,
                    digest,
                    CountryLaunchStatus.Approved)
            ],
            CountryPolicyRuntimeMode.Production);
        PropertyGovernancePolicyBinding binding = new(
            document.OperatingCountryCode,
            document.PolicyId,
            document.PolicyVersion,
            document.PermittedDataRegions.Single(),
            document.PermittedTransferProfiles.Single(),
            document.RetentionRules.Single().RetentionPolicyId,
            document.RetentionRules.Single().RetentionPolicyVersion,
            digest,
            document.EffectiveAtUtc,
            document.ExpiresAtUtc,
            Now.AddDays(-1),
            []);
        return new(registry, binding);
    }
}

internal sealed record GuestRetentionPolicyFixture(
    CountryPolicyRegistry Registry,
    PropertyGovernancePolicyBinding Binding);
