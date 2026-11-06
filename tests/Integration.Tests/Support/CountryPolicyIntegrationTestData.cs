namespace Integration.Tests.Support;

using System.Text;
using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Persistence;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Ingestion.Persistence;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Staff.Domain.Governance;
using BunkFy.TimeZones;
using Gma.Framework.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

internal static class CountryPolicyIntegrationTestData
{
    private const string PolicyId = "integration-hostel-baseline";
    private const int PolicyVersion = 1;
    private const string OperatingCountryCode = "GB";
    private const string DataRegionId = "integration-region";
    private const string TransferProfileId = "integration-no-transfer";
    private const string RetentionPolicyId = "integration-guest-operational";
    private const int RetentionPolicyVersion = 1;
    private const string StaffRetentionPolicyId =
        "integration-staff-employment";
    private const int StaffRetentionPolicyVersion = 1;
    private const string AcknowledgementId = "integration-operator-notice";
    private const int AcknowledgementVersion = 1;

    private static readonly CountryPolicyPackArtifact Artifact =
        CountryPolicyPackJson.Parse(Encoding.UTF8.GetBytes(PolicyJson));

    public static CountryPolicyRegistry Registry { get; } = CountryPolicyRegistry.Create(
        [Artifact],
        [new(
            OperatingCountryCode,
            PolicyId,
            PolicyVersion,
            Artifact.ContentSha256,
            CountryLaunchStatus.Engineering)],
        CountryPolicyRuntimeMode.Engineering,
        new CountryPolicyTimeZoneRules(
            TryResolveTimeZone,
            TimeZoneCalendarMath.TryAddCalendarPeriod));

    public static StaffEmploymentGovernanceBinding
        CreateStaffGovernanceBinding(DateTimeOffset evaluatedAtUtc) =>
        StaffEmploymentGovernanceBinding.Create(
            OperatingCountryCode,
            PolicyId,
            PolicyVersion,
            DataRegionId,
            TransferProfileId,
            StaffRetentionPolicyId,
            StaffRetentionPolicyVersion,
            Artifact.ContentSha256,
            Artifact.Document.EffectiveAtUtc,
            Artifact.Document.ExpiresAtUtc,
            evaluatedAtUtc).Value;

    public static IReadOnlyCollection<
        StaffEmploymentGovernanceAcknowledgement>
        CreateStaffGovernanceAcknowledgements() =>
        [
            StaffEmploymentGovernanceAcknowledgement.Create(
                AcknowledgementId,
                AcknowledgementVersion).Value
        ];

    public static void InstallRegistry(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.RemoveAll<CountryPolicyRegistry>();
        services.AddSingleton(Registry);
    }

    private static bool TryResolveTimeZone(
        string? timeZoneId,
        out CountryPolicyTimeZoneResolution resolution)
    {
        if (!TimeZoneCatalog.Default.TryResolve(
                timeZoneId,
                out TimeZoneCatalogResolution? catalogResolution))
        {
            resolution = default;
            return false;
        }

        resolution = new(
            catalogResolution.RequestedTimeZoneId,
            catalogResolution.CanonicalTimeZoneId,
            catalogResolution.Kind == TimeZoneCatalogResolutionKind.Canonical
                ? CountryPolicyTimeZoneResolutionKind.Canonical
                : CountryPolicyTimeZoneResolutionKind.Alias);
        return true;
    }

    public static async Task ApplyActivationAsync(
        IServiceProvider services,
        string consumerModule,
        string tenantId,
        Guid propertyId,
        long propertyVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        DbContext? consumerDbContext = consumerModule switch
        {
            DataRightsModuleMetadata.Name =>
                services.GetRequiredService<DataRightsDbContext>(),
            GuestsModuleMetadata.Name =>
                services.GetRequiredService<GuestsDbContext>(),
            IngestionModuleMetadata.Name =>
                services.GetRequiredService<IngestionDbContext>(),
            ReservationsModuleMetadata.Name =>
                services.GetRequiredService<ReservationsDbContext>(),
            _ => null
        };
        if (consumerDbContext is null)
        {
            await ApplyActivationCoreAsync(
                services,
                consumerModule,
                tenantId,
                propertyId,
                propertyVersion,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await ModuleTransactionIntegrationTestData.ExecuteAsync(
            consumerDbContext,
            token => ApplyActivationCoreAsync(
                services,
                consumerModule,
                tenantId,
                propertyId,
                propertyVersion,
                token),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyActivationCoreAsync(
        IServiceProvider services,
        string consumerModule,
        string tenantId,
        Guid propertyId,
        long propertyVersion,
        CancellationToken cancellationToken)
    {
        IntegrationEventSubscription subscription = services
            .GetRequiredService<IIntegrationEventSubscriptionRegistry>()
            .Subscriptions
            .Single(item =>
                item.ConsumerModule == consumerModule &&
                item.EventType == typeof(PropertyProcessingPolicyActivatedIntegrationEvent));
        IIntegrationEventHandler<PropertyProcessingPolicyActivatedIntegrationEvent> handler =
            (IIntegrationEventHandler<PropertyProcessingPolicyActivatedIntegrationEvent>)services
                .GetRequiredService(subscription.HandlerType);
        await handler.HandleAsync(
            CreateActivationEvent(tenantId, propertyId, propertyVersion),
            cancellationToken).ConfigureAwait(false);
    }

    private static PropertyProcessingPolicyActivatedIntegrationEvent CreateActivationEvent(
        string tenantId,
        Guid propertyId,
        long propertyVersion)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        CountryPolicyDecision decision = Registry.EvaluateActivation(new CountryPolicyActivationRequest(
            OperatingCountryCode,
            PolicyId,
            PolicyVersion,
            DataRegionId,
            TransferProfileId,
            RetentionPolicyId,
            RetentionPolicyVersion,
            [new(AcknowledgementId, AcknowledgementVersion)],
            "hostel",
            "property-activation",
            "integration-test",
            nowUtc));
        if (!decision.IsAllowed || decision.Evidence is null)
        {
            throw new InvalidOperationException(
                $"The integration-test country policy was denied: {decision.Reason}.");
        }

        CountryPolicyEvidence evidence = decision.Evidence;
        PropertyGovernancePolicyBinding binding = new(
            evidence.OperatingCountryCode,
            evidence.PolicyId,
            evidence.PolicyVersion,
            evidence.DataRegionId,
            evidence.TransferProfileId,
            evidence.RetentionPolicyId,
            evidence.RetentionPolicyVersion,
            evidence.ContentSha256,
            evidence.EffectiveAtUtc,
            evidence.ExpiresAtUtc,
            evidence.EvaluatedAtUtc,
            evidence.AcceptedAcknowledgements.Select(acknowledgement =>
                new PropertyGovernanceAcknowledgement(
                    acknowledgement.AcknowledgementId,
                    acknowledgement.AcknowledgementVersion)).ToArray());
        return new(
            Guid.NewGuid(),
            tenantId,
            nowUtc,
            propertyId,
            binding,
            propertyVersion);
    }

    private const string PolicyJson = /*lang=json,strict*/ """
        {
          "schemaVersion": 1,
          "policyId": "integration-hostel-baseline",
          "policyVersion": 1,
          "operatingCountryCode": "GB",
          "approvalState": "approved",
          "effectiveAtUtc": "2020-01-01T00:00:00Z",
          "expiresAtUtc": "2100-01-01T00:00:00Z",
          "accommodationTypes": [ "hostel" ],
          "guestCategories": [ "ordinary-guest", "staff" ],
          "fieldRules": [
            {
              "fieldPolicyKey": "guest.primary-name",
              "guestCategory": "ordinary-guest",
              "requirement": "required",
              "purposeCodes": [
                "data-rights-correction",
                "data-rights-anonymisation",
                "data-rights-restriction",
                "guest-profile-management",
                "reservation-management",
                "reservation-ingestion"
              ]
            },
            {
              "fieldPolicyKey": "staff.profile",
              "guestCategory": "staff",
              "requirement": "required",
              "purposeCodes": [ "staff-data-rights-anonymisation" ]
            }
          ],
          "purposeRules": [
            {
              "purposeCode": "property-activation",
              "legalRuleReferenceKeys": [ "integration-approval" ],
              "allowedSurfaces": [ "property-activation" ],
              "allowedSourceProvenance": [
                "integration-test",
                "authorized-workspace-operator"
              ]
            },
            {
              "purposeCode": "data-rights-anonymisation",
              "legalRuleReferenceKeys": [ "integration-erasure" ],
              "allowedSurfaces": [ "erasure" ],
              "allowedSourceProvenance": [ "authorized-workspace-operator" ]
            },
            {
              "purposeCode": "data-rights-correction",
              "legalRuleReferenceKeys": [ "integration-correction" ],
              "allowedSurfaces": [ "api-write" ],
              "allowedSourceProvenance": [ "authorized-workspace-operator" ]
            },
            {
              "purposeCode": "data-rights-restriction",
              "legalRuleReferenceKeys": [ "integration-restriction" ],
              "allowedSurfaces": [ "api-write" ],
              "allowedSourceProvenance": [ "authorized-workspace-operator" ]
            },
            {
              "purposeCode": "staff-data-rights-anonymisation",
              "legalRuleReferenceKeys": [ "integration-staff-erasure" ],
              "allowedSurfaces": [ "erasure" ],
              "allowedSourceProvenance": [ "authorized-workspace-operator" ]
            },
            {
              "purposeCode": "staff-profile-retention",
              "legalRuleReferenceKeys": [ "integration-staff-retention" ],
              "allowedSurfaces": [ "retention" ],
              "allowedSourceProvenance": [ "retention-worker" ]
            },
            {
              "purposeCode": "guest-profile-management",
              "legalRuleReferenceKeys": [ "integration-customer-instruction" ],
              "allowedSurfaces": [ "api-write" ],
              "allowedSourceProvenance": [ "authorized-workspace-operator" ]
            },
            {
              "purposeCode": "guest-profile-retention",
              "legalRuleReferenceKeys": [ "integration-retention" ],
              "allowedSurfaces": [ "retention" ],
              "allowedSourceProvenance": [ "retention-worker" ]
            },
            {
              "purposeCode": "reservation-retention",
              "legalRuleReferenceKeys": [ "integration-retention" ],
              "allowedSurfaces": [ "retention" ],
              "allowedSourceProvenance": [ "retention-worker" ]
            },
            {
              "purposeCode": "reservation-management",
              "legalRuleReferenceKeys": [ "integration-customer-instruction" ],
              "allowedSurfaces": [ "api-write" ],
              "allowedSourceProvenance": [ "authorized-workspace-operator" ]
            },
            {
              "purposeCode": "reservation-ingestion",
              "legalRuleReferenceKeys": [ "integration-customer-instruction" ],
              "allowedSurfaces": [ "api-write", "adapter-ingress", "import" ],
              "allowedSourceProvenance": [
                "authorized-workspace-operator",
                "approved-adapter",
                "approved-parser",
                "approved-ingestion"
              ]
            }
          ],
          "retentionRules": [
            {
              "retentionPolicyId": "integration-guest-operational",
              "retentionPolicyVersion": 1,
              "dataClass": "guest-operational",
              "trigger": "stay-ended",
              "period": "365.00:00:00"
            },
            {
              "retentionPolicyId": "integration-guest-operational",
              "retentionPolicyVersion": 1,
              "dataClass": "reservation-operational",
              "trigger": "reservation-ended",
              "period": "365.00:00:00"
            },
            {
              "retentionPolicyId": "integration-staff-employment",
              "retentionPolicyVersion": 1,
              "dataClass": "staff-employment",
              "trigger": "employment-ended",
              "period": "1.00:00:00"
            }
          ],
          "rightsRule": {
            "registration": "integration-registration",
            "export": "integration-export",
            "correction": "integration-correction",
            "restriction": "integration-restriction",
            "erasure": "integration-erasure"
          },
          "restrictions": {
            "minors": "integration-not-assessed",
            "documents": "integration-document-images-prohibited",
            "specialCategoryData": "integration-prohibited"
          },
          "permittedDataRegions": [ "integration-region" ],
          "permittedTransferProfiles": [ "integration-no-transfer" ],
          "requiredAcknowledgements": [
            {
              "acknowledgementId": "integration-operator-notice",
              "acknowledgementVersion": 1
            }
          ],
          "approval": {
            "ownerReference": "integration-policy-owner",
            "reviewerReference": "integration-policy-reviewer",
            "reviewedAtUtc": "2019-12-15T00:00:00Z",
            "sources": [
              {
                "referenceId": "integration-source",
                "uri": "https://example.test/integration-policy-source"
              }
            ],
            "detachedSignatureReference": "integration-signature"
          }
        }
        """;
}
