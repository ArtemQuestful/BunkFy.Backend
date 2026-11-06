namespace BunkFy.Modules.DataRights.Application.Policies;

using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Results;

internal sealed class GuestDataRightsResponseDeadlinePolicy(
    IDataRightsPropertyProjectionRepository properties,
    CountryPolicyRegistry countryPolicies)
    : IDataRightsResponseDeadlinePolicy
{
    public const string AccommodationType = "hostel";

    public async Task<Result<DataRightsResponseDeadlinePolicyEvidence>> ResolveGuestAsync(
        Guid propertyId,
        DataRightsCaseOperation requestedOperations,
        DateTimeOffset receivedAtUtc,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        DataRightsPropertyPolicySnapshot? property = await properties.GetPolicyAsync(
            propertyId,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (property is null ||
            !property.IsKnown ||
            property.Status is not (PropertyStatus.Active or PropertyStatus.Retired) ||
            property.ProcessingStatus is not (
                PropertyProcessingStatus.Enabled or
                PropertyProcessingStatus.Suspended) ||
            string.IsNullOrWhiteSpace(property.TimeZoneId) ||
            property.GovernancePolicy is null ||
            property.TopologySourceVersion <= 0 ||
            property.PolicySourceVersion <= 0)
        {
            return Unavailable();
        }

        CountryPolicyRight[] rights = MapRights(requestedOperations);
        if (rights.Length == 0)
        {
            return Unavailable();
        }

        PropertyGovernancePolicyBinding binding = property.GovernancePolicy;
        CountryPolicyBinding countryPolicyBinding = new(
            binding.OperatingCountryCode,
            binding.PolicyId,
            binding.PolicyVersion,
            binding.DataRegionId,
            binding.TransferProfileId,
            binding.RetentionPolicyId,
            binding.RetentionPolicyVersion,
            binding.ContentSha256,
            binding.Acknowledgements.Select(acknowledgement =>
                new CountryPolicyAcknowledgement(
                    acknowledgement.AcknowledgementId,
                    acknowledgement.AcknowledgementVersion)).ToArray());
        List<CountryPolicyRightsResponseEvidence> decisions = new(rights.Length);
        foreach (CountryPolicyRight right in rights)
        {
            CountryPolicyRightsResponseDecision decision =
                countryPolicies.EvaluateRightsResponse(new(
                    countryPolicyBinding,
                    AccommodationType,
                    right,
                    property.TimeZoneId,
                    receivedAtUtc));
            if (!decision.IsAllowed || decision.Evidence is null)
            {
                return Unavailable();
            }

            decisions.Add(decision.Evidence);
        }

        CountryPolicyRightsResponseEvidence controlling = decisions
            .OrderBy(decision => decision.DueAtUtc)
            .ThenBy(decision => decision.Right)
            .First();
        Result<DataRightsResponseDeadlinePolicyEvidence> evidence =
            DataRightsResponseDeadlinePolicyEvidence.Create(
                propertyId,
                property.TopologySourceVersion,
                property.PolicySourceVersion,
                controlling.OperatingCountryCode,
                controlling.PolicyId,
                controlling.PolicyVersion,
                controlling.ContentSha256,
                MapRight(controlling.Right),
                controlling.RuleReference,
                controlling.PeriodYears,
                controlling.PeriodMonths,
                controlling.PeriodDays,
                controlling.TimeZoneId,
                controlling.PolicyEffectiveAtUtc,
                controlling.PolicyExpiresAtUtc,
                controlling.ReceivedAtUtc,
                evaluatedAtUtc,
                controlling.DueAtUtc);
        return evidence.IsSuccess ? evidence : Unavailable();
    }

    private static CountryPolicyRight[] MapRights(
        DataRightsCaseOperation requestedOperations)
    {
        List<CountryPolicyRight> rights = [];
        if (requestedOperations.HasFlag(DataRightsCaseOperation.AccessExport))
        {
            rights.Add(CountryPolicyRight.Export);
        }

        if (requestedOperations.HasFlag(DataRightsCaseOperation.Correction))
        {
            rights.Add(CountryPolicyRight.Correction);
        }

        if (requestedOperations.HasFlag(DataRightsCaseOperation.Restriction))
        {
            rights.Add(CountryPolicyRight.Restriction);
        }

        if (requestedOperations.HasFlag(DataRightsCaseOperation.Erasure) ||
            requestedOperations.HasFlag(DataRightsCaseOperation.Anonymisation))
        {
            rights.Add(CountryPolicyRight.Erasure);
        }

        return [.. rights.Distinct()];
    }

    private static DataRightsResponseRight MapRight(CountryPolicyRight right) =>
        right switch
        {
            CountryPolicyRight.Export => DataRightsResponseRight.Export,
            CountryPolicyRight.Correction => DataRightsResponseRight.Correction,
            CountryPolicyRight.Restriction => DataRightsResponseRight.Restriction,
            CountryPolicyRight.Erasure => DataRightsResponseRight.Erasure,
            _ => DataRightsResponseRight.Unknown
        };

    private static Result<DataRightsResponseDeadlinePolicyEvidence> Unavailable() =>
        Result.Failure<DataRightsResponseDeadlinePolicyEvidence>(
            DataRightsApplicationErrors.ResponseDeadlinePolicyUnavailable);
}
