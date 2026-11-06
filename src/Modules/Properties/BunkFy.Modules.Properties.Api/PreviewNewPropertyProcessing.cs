namespace BunkFy.Modules.Properties.Api;

using System.Security.Cryptography;
using System.Text;
using BunkFy.DataGovernance;
using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.TimeZones;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;

internal sealed class PreviewNewPropertyProcessing(bool enabled, CountryPolicyRegistry registry)
{
    public bool Enabled { get; } = ValidateConfiguration(enabled, registry);

    private static bool ValidateConfiguration(bool enabled, CountryPolicyRegistry registry)
    {
        if (enabled && (registry.RuntimeMode != CountryPolicyRuntimeMode.Engineering ||
            !registry.ListPolicies().Any(policy =>
                policy.PolicyId.StartsWith(PreviewCountryPolicyDefaults.PolicyIdPrefix, StringComparison.Ordinal) &&
                policy.LaunchStatus == CountryLaunchStatus.Engineering &&
                policy.ApprovalState == CountryPolicyApprovalState.Example)))
        {
            throw new InvalidOperationException("Preview activation requires an installed engineering test policy.");
        }

        return enabled;
    }

    public async Task<Result<PropertyMutationReceiptDto>> ApplyAsync(
        Result<PropertyMutationReceiptDto> creation,
        string requestedTimeZoneId,
        string actorId,
        IRequestDispatcher dispatcher,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!this.Enabled || creation.IsFailure ||
            creation.Value.Version != 1 ||
            creation.Value.ProcessingStatus != PropertyProcessingStatus.Unconfigured)
        {
            return creation;
        }

        if (registry.RuntimeMode != CountryPolicyRuntimeMode.Engineering ||
            !TimeZoneCatalog.Default.TryResolve(requestedTimeZoneId, out TimeZoneCatalogResolution? zone))
        {
            return Result.Failure<PropertyMutationReceiptDto>(
                PropertiesApplicationErrors.CountryPolicyDenied(CountryPolicyDecisionReason.InvalidRequest));
        }

        foreach (CountryPolicyDescriptor policy in registry.ListPolicies().Where(policy =>
                     policy.PolicyId.StartsWith(PreviewCountryPolicyDefaults.PolicyIdPrefix, StringComparison.Ordinal) &&
                     policy.LaunchStatus == CountryLaunchStatus.Engineering &&
                     policy.ApprovalState == CountryPolicyApprovalState.Example))
        {
            CountryPolicyRetentionDescriptor retention = policy.RetentionPolicies.First();
            CountryPolicyAcknowledgement[] acknowledgements = [.. policy.RequiredAcknowledgements];
            CountryPolicyDecision decision = registry.EvaluateActivation(new(
                policy.OperatingCountryCode, policy.PolicyId, policy.PolicyVersion,
                policy.PermittedDataRegions.First(), policy.PermittedTransferProfiles.First(),
                retention.RetentionPolicyId, retention.RetentionPolicyVersion, acknowledgements,
                "hostel", "property-activation", "authorized-workspace-operator", observedAtUtc));
            if (!decision.IsAllowed || decision.Evidence is null ||
                !registry.EvaluateTimeZoneCompatibility(new(decision.Evidence.ToBinding(), "hostel",
                    zone.CanonicalTimeZoneId, observedAtUtc)).IsAllowed)
            {
                continue;
            }

            // Creation has committed before this second command. Retries keep
            // the same operation id and never reactivate a modified property.
            Guid operationId = new(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"bunkfy.preview-default-processing:{creation.Value.PropertyId:D}"))[..16]);
            return await dispatcher.SendAsync(new ActivatePropertyProcessingCommand(
                creation.Value.PropertyId, operationId,
                policy.OperatingCountryCode, policy.PolicyId, policy.PolicyVersion,
                policy.PermittedDataRegions.First(), policy.PermittedTransferProfiles.First(),
                retention.RetentionPolicyId, retention.RetentionPolicyVersion,
                [.. acknowledgements.Select(value => new PropertyGovernanceAcknowledgementDto(
                    value.AcknowledgementId, value.AcknowledgementVersion))],
                true, 1, actorId), cancellationToken).ConfigureAwait(false);
        }

        return Result.Failure<PropertyMutationReceiptDto>(
            PropertiesApplicationErrors.CountryPolicyDenied(CountryPolicyDecisionReason.UnknownPolicy));
    }
}
