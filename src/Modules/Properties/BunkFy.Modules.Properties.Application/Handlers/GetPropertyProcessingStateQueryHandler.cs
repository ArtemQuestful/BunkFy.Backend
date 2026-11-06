namespace BunkFy.Modules.Properties.Application.Handlers;

using BunkFy.DataGovernance;
using BunkFy.Modules.Properties.Application.Mapping;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Errors;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class GetPropertyProcessingStateQueryHandler(
    IPropertyRepository properties,
    CountryPolicyRegistry countryPolicies,
    ISystemClock clock)
    : IQueryHandler<GetPropertyProcessingStateQuery, PropertyProcessingStateDto>
{
    internal const string UnconfiguredReasonCode = "Properties.PropertyProcessing.Unconfigured";
    internal const string SuspendedReasonCode = "Properties.PropertyProcessing.Suspended";
    internal const string RetiredReasonCode = "Properties.PropertyRetired";
    internal const string AllowedReasonCode = "Properties.CountryPolicy.Allowed";

    public async Task<Result<PropertyProcessingStateDto>> HandleAsync(
        GetPropertyProcessingStateQuery query,
        CancellationToken cancellationToken)
    {
        Property? property = await properties.GetAsync(query.PropertyId, cancellationToken).ConfigureAwait(false);
        if (property is null)
        {
            return Result.Failure<PropertyProcessingStateDto>(PropertiesDomainErrors.PropertyNotFound);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        if (!PropertiesObservationTime.IsValid(nowUtc))
        {
            return Result.Failure<PropertyProcessingStateDto>(
                PropertiesApplicationErrors.TimeSourceUnavailable);
        }

        (PropertyProcessingEffectiveStatus effectiveStatus, string reasonCode) = this.Evaluate(property, nowUtc);
        return Result.Success(new PropertyProcessingStateDto(
            property.Id,
            PropertiesMapper.MapProcessingStatus(property.ProcessingState),
            effectiveStatus,
            reasonCode,
            PropertiesMapper.MapGovernanceBinding(property),
            property.Version,
            nowUtc));
    }

    private (PropertyProcessingEffectiveStatus Status, string ReasonCode) Evaluate(
        Property property,
        DateTimeOffset nowUtc)
    {
        if (property.Status == PropertyState.Retired)
        {
            return (PropertyProcessingEffectiveStatus.Suspended, RetiredReasonCode);
        }

        if (property.ProcessingState == PropertyProcessingState.Unconfigured || property.GovernanceBinding is null)
        {
            return (PropertyProcessingEffectiveStatus.Unconfigured, UnconfiguredReasonCode);
        }

        if (property.ProcessingState == PropertyProcessingState.Suspended)
        {
            return (PropertyProcessingEffectiveStatus.Suspended, SuspendedReasonCode);
        }

        CountryPolicyDecision decision = countryPolicies.EvaluateOperation(new CountryPolicyOperationRequest(
            new CountryPolicyBinding(
                property.GovernanceBinding.OperatingCountryCode,
                property.GovernanceBinding.PolicyId,
                property.GovernanceBinding.PolicyVersion,
                property.GovernanceBinding.DataRegionId,
                property.GovernanceBinding.TransferProfileId,
                property.GovernanceBinding.RetentionPolicyId,
                property.GovernanceBinding.RetentionPolicyVersion,
                property.GovernanceBinding.ContentSha256,
                property.GovernanceAcknowledgements.Select(acknowledgement =>
                    new CountryPolicyAcknowledgement(
                        acknowledgement.AcknowledgementId,
                        acknowledgement.AcknowledgementVersion)).ToArray()),
            ActivatePropertyProcessingCommandHandler.AccommodationType,
            ActivatePropertyProcessingCommandHandler.ActivationPurpose,
            CountryPolicySurface.PropertyActivation,
            ActivatePropertyProcessingCommandHandler.OperatorProvenance,
            nowUtc));
        if (decision.IsAllowed)
        {
            return (PropertyProcessingEffectiveStatus.Enabled, AllowedReasonCode);
        }

        PropertyProcessingEffectiveStatus status = decision.Reason == CountryPolicyDecisionReason.PolicyExpired
            ? PropertyProcessingEffectiveStatus.Expired
            : PropertyProcessingEffectiveStatus.Revoked;
        return (status, $"Properties.CountryPolicy.{decision.Reason}");
    }
}
