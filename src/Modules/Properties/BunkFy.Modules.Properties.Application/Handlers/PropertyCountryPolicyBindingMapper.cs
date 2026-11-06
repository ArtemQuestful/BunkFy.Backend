namespace BunkFy.Modules.Properties.Application.Handlers;

using BunkFy.DataGovernance;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.ValueObjects;

internal static class PropertyCountryPolicyBindingMapper
{
    public static CountryPolicyBinding? ToCountryPolicyBinding(
        Property property)
    {
        ArgumentNullException.ThrowIfNull(property);
        PropertyGovernanceBinding? binding = property.GovernanceBinding;
        return binding is null
            ? null
            : new CountryPolicyBinding(
                binding.OperatingCountryCode,
                binding.PolicyId,
                binding.PolicyVersion,
                binding.DataRegionId,
                binding.TransferProfileId,
                binding.RetentionPolicyId,
                binding.RetentionPolicyVersion,
                binding.ContentSha256,
                property.GovernanceAcknowledgements
                    .Select(acknowledgement =>
                        new CountryPolicyAcknowledgement(
                            acknowledgement.AcknowledgementId,
                            acknowledgement.AcknowledgementVersion))
                    .ToArray());
    }
}
