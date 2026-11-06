namespace BunkFy.Modules.DataRights.Application.Policies;

using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Logging;
using ContractEvidenceBinding =
    Contracts.DataRightsApprovalEvidenceBinding;
using DomainEvidenceBinding =
    Domain.ValueObjects.DataRightsApprovalEvidenceBinding;
using SelectedSubject =
    Domain.Entities.DataRightsSubjectCoordinate;

internal sealed class DataRightsAnonymisationApprovalPolicy(
    IDataRightsPropertyProjectionRepository properties,
    IEnumerable<IDataRightsAnonymisationPolicyContributor> contributors,
    CountryPolicyRegistry countryPolicies,
    ISystemClock clock,
    ILogger<DataRightsAnonymisationApprovalPolicy>? logger = null)
    : IDataRightsAnonymisationApprovalPolicy
{
    public const string AccommodationType = "hostel";
    public const string PurposeCode = "data-rights-anonymisation";
    public const string Surface = "erasure";
    public const string SourceProvenance = "authorized-workspace-operator";

    public async Task<Result<DataRightsApprovalPolicyEvidence>> EvaluateAsync(
        string tenantId,
        DataRightsCaseScope scope,
        Guid caseId,
        IReadOnlyCollection<SelectedSubject> subjects,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(subjects);

        return scope.CaseType switch
        {
            DataRightsCaseType.GuestRights
                when scope.PropertyId is Guid propertyId =>
                await this.EvaluateGuestAsync(
                    propertyId,
                    cancellationToken).ConfigureAwait(false),
            DataRightsCaseType.StaffRights
                when scope.PropertyId is null =>
                await this.EvaluateOwnerAsync(
                    scope,
                    tenantId,
                    caseId,
                    subjects,
                    cancellationToken).ConfigureAwait(false),
            _ => Denied()
        };
    }

    private async Task<Result<DataRightsApprovalPolicyEvidence>>
        EvaluateGuestAsync(
            Guid propertyId,
            CancellationToken cancellationToken)
    {
        DataRightsPropertyPolicySnapshot? property = await properties.GetPolicyAsync(
            propertyId,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (property is not
            {
                IsKnown: true,
                Status: PropertyStatus.Active,
                ProcessingStatus: PropertyProcessingStatus.Enabled,
                GovernancePolicy: not null,
                PolicySourceVersion: > 0
            })
        {
            return Denied();
        }

        PropertyGovernancePolicyBinding binding = property.GovernancePolicy;
        CountryPolicyDecision decision = countryPolicies.EvaluateOperation(
            new CountryPolicyOperationRequest(
                new CountryPolicyBinding(
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
                            acknowledgement.AcknowledgementVersion)).ToArray()),
                AccommodationType,
                PurposeCode,
                CountryPolicySurface.Erasure,
                SourceProvenance,
                clock.UtcNow));
        if (!decision.IsAllowed || decision.Evidence is null)
        {
            return Denied();
        }

        CountryPolicyEvidence evidence = decision.Evidence;
        return DataRightsApprovalPolicyEvidence.Create(
            propertyId,
            property.PolicySourceVersion,
            evidence.OperatingCountryCode,
            evidence.PolicyId,
            evidence.PolicyVersion,
            evidence.RetentionPolicyId,
            evidence.RetentionPolicyVersion,
            evidence.ContentSha256,
            evidence.PurposeCode,
            Surface,
            evidence.SourceProvenance,
            evidence.EvaluatedAtUtc);
    }

    private async Task<Result<DataRightsApprovalPolicyEvidence>>
        EvaluateOwnerAsync(
        DataRightsCaseScope scope,
        string tenantId,
        Guid caseId,
        IReadOnlyCollection<SelectedSubject> subjects,
        CancellationToken cancellationToken)
    {
        if (caseId == Guid.Empty ||
            subjects.Count is <= 0 or > DataRightsCase.MaxSelectedSubjects)
        {
            return Denied();
        }

        SelectedSubject[] orderedSubjects = subjects
            .OrderBy(subject => subject.OwnerKey, StringComparer.Ordinal)
            .ThenBy(subject => subject.RecordType, StringComparer.Ordinal)
            .ThenBy(subject => subject.RecordId)
            .ToArray();
        List<OwnerPolicyContribution> evaluated =
            new(orderedSubjects.Length);
        foreach (SelectedSubject subject in orderedSubjects)
        {
            IDataRightsAnonymisationPolicyContributor[] matches =
                contributors
                    .Where(contributor =>
                        contributor is not null &&
                        contributor.ContractVersion ==
                            DataRightsAnonymisationPolicyContract
                                .CurrentVersion &&
                        contributor.CaseType == scope.CaseType &&
                        string.Equals(
                            contributor.OwnerKey,
                            subject.OwnerKey,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            contributor.RecordType,
                            subject.RecordType,
                            StringComparison.Ordinal))
                    .Take(2)
                    .ToArray();
            if (matches.Length != 1)
            {
                return Denied();
            }

            DataRightsAnonymisationPolicyContributionResult? contribution;
            try
            {
                contribution = await matches[0].EvaluateAsync(
                        new DataRightsAnonymisationPolicyContributionRequest(
                            DataRightsAnonymisationPolicyContract
                                .CurrentVersion,
                            tenantId,
                            scope.CaseType,
                            scope.PropertyId,
                            caseId,
                            new(
                                subject.OwnerKey,
                                subject.RecordType,
                                subject.RecordId,
                                subject.RecordVersion)),
                        cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                logger?.LogWarning(
                    "Data Rights anonymisation policy contributor for owner {OwnerKey} and record type {RecordType} failed because {ExceptionType} was raised.",
                    matches[0].OwnerKey,
                    matches[0].RecordType,
                    exception.GetType().Name);
                return Denied();
            }

            if (!IsApprovedContribution(contribution))
            {
                return Denied();
            }

            evaluated.Add(new(subject, contribution!));
        }

        OwnerPolicyContribution[] authorities = evaluated
            .Where(item =>
                item.Contribution.Role ==
                    DataRightsAnonymisationPolicyContributionRole
                        .Authority)
            .ToArray();
        if (authorities.Length != 1 ||
            authorities[0].Contribution.Evidence is not
            { } authorityEvidence)
        {
            return Denied();
        }

        SelectedSubject authoritySubject = authorities[0].Subject;
        foreach (OwnerPolicyContribution companion in evaluated.Where(
            item =>
                item.Contribution.Role ==
                    DataRightsAnonymisationPolicyContributionRole
                        .Companion))
        {
            if (companion.Contribution.AuthorityCoordinate is not
                { } authorityCoordinate ||
                !Matches(authoritySubject, authorityCoordinate))
            {
                return Denied();
            }
        }

        ContractEvidenceBinding[] suppliedBindings = evaluated
            .SelectMany(item =>
                item.Contribution.Role ==
                    DataRightsAnonymisationPolicyContributionRole
                        .Authority
                    ? item.Contribution.Evidence!.StateBindings
                    : item.Contribution.StateBindings)
            .ToArray();
        if (suppliedBindings.Length is <= 0 or
                > DataRightsAnonymisationPolicyContract
                    .MaximumStateBindings ||
            suppliedBindings.Any(binding => binding is null))
        {
            return Denied();
        }

        ContractEvidenceBinding[] allBindings = suppliedBindings
            .OrderBy(binding => binding.Key, StringComparer.Ordinal)
            .ToArray();
        if (allBindings.GroupBy(
                    binding => binding.Key,
                    StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
        {
            return Denied();
        }

        List<DomainEvidenceBinding> bindings =
            new(allBindings.Length);
        foreach (ContractEvidenceBinding binding in allBindings)
        {
            Result<DomainEvidenceBinding> created =
                DomainEvidenceBinding.Create(
                    binding.Key,
                    binding.Version,
                    binding.Sha256);
            if (created.IsFailure)
            {
                return Denied();
            }

            bindings.Add(created.Value);
        }

        Result<DataRightsApprovalPolicyEvidence> frozen =
            DataRightsApprovalPolicyEvidence.CreateScoped(
                (DataRightsCaseKind)scope.CaseType,
                DataRightsCaseScopeKind.Tenant,
                propertyId: null,
                propertyVersion: 0,
                authorityEvidence.OperatingCountryCode,
                authorityEvidence.PolicyId,
                authorityEvidence.PolicyVersion,
                authorityEvidence.RetentionPolicyId,
                authorityEvidence.RetentionPolicyVersion,
                authorityEvidence.ContentSha256,
                authorityEvidence.PurposeCode,
                authorityEvidence.Surface,
                authorityEvidence.SourceProvenance,
                authorityEvidence.RetentionDataClass,
                authorityEvidence.RetentionTrigger,
                authorityEvidence.RetentionTriggeredAtUtc,
                authorityEvidence.RetentionDeadlineUtc,
                authorityEvidence.EvaluatedAtUtc,
                bindings,
                authorityEvidence.RequiresDistinctExecutor);
        return frozen.IsSuccess ? frozen : Denied();
    }

    private static bool IsApprovedContribution(
        DataRightsAnonymisationPolicyContributionResult? contribution)
    {
        if (contribution is null ||
            contribution.ContractVersion !=
                DataRightsAnonymisationPolicyContract.CurrentVersion ||
            contribution.Status !=
                DataRightsAnonymisationPolicyContributionStatus.Approved ||
            contribution.OutcomeCode is not null ||
            contribution.StateBindings is null)
        {
            return false;
        }

        return contribution.Role switch
        {
            DataRightsAnonymisationPolicyContributionRole.Authority =>
                contribution.Evidence is
                {
                    StateBindings: not null
                } evidence &&
                evidence.StateBindings.Count is > 0 and <=
                    DataRightsAnonymisationPolicyContract
                        .MaximumStateBindings &&
                contribution.AuthorityCoordinate is null &&
                contribution.StateBindings.Count == 0,
            DataRightsAnonymisationPolicyContributionRole.Companion =>
                contribution.Evidence is null &&
                contribution.AuthorityCoordinate is not null &&
                contribution.StateBindings.Count is > 0 and <=
                    DataRightsAnonymisationPolicyContract
                        .MaximumStateBindings,
            _ => false
        };
    }

    private static bool Matches(
        SelectedSubject authority,
        DataRightsSubjectCoordinate coordinate) =>
        string.Equals(
            authority.OwnerKey,
            coordinate.OwnerKey?.Trim(),
            StringComparison.Ordinal) &&
        string.Equals(
            authority.RecordType,
            coordinate.RecordType?.Trim(),
            StringComparison.Ordinal) &&
        authority.RecordId == coordinate.RecordId &&
        authority.RecordVersion == coordinate.RecordVersion;

    private sealed record OwnerPolicyContribution(
        SelectedSubject Subject,
        DataRightsAnonymisationPolicyContributionResult Contribution);

    private static Result<DataRightsApprovalPolicyEvidence> Denied() =>
        Result.Failure<DataRightsApprovalPolicyEvidence>(
            DataRightsApplicationErrors.AnonymisationApprovalPolicyDenied);
}
